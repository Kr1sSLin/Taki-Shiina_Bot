"""
WebSocket API for TakiShiina Bot
实现防抖合并、断线继续处理、在线实时推送
"""

import asyncio
import base64
import json
import logging
import os
import random
import uuid
from datetime import datetime, timedelta, timezone

import httpx
from dotenv import load_dotenv
from fastapi import FastAPI, Header, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse
import google.generativeai as genai
from openai import AsyncOpenAI

from auth_utils import AuthContext, DeviceEntry, parse_device_tokens, resolve_auth_context
from app_constants import EMOTIONAL_TRIGGERS, LORE_TRIGGERS, USER_MEMO
from app_state import AppState
from secure_storage import SecureJsonStore
from services.history_store import HistoryStore
from services.memory_service import MemoryService
from services.prompt_service import PromptService
from services.weather_service import WeatherService
from text_utils import (
    clean_short_term_history,
    inject_emojis,
    is_repetitive_reply,
    sanitize_taki_reply,
    strip_polluted_tail,
    with_history_timestamp,
)

# ===== 互动礼物 · 积分机制 · 等级体系（PRD 新增功能） =====
from api.v1.admin import router as admin_router
from api.v1.deps import AuthRuntime, configure_auth, register_gamification
from api.v1.interaction import router as interaction_router
from api.v1.level import router as level_router
from api.v1.points import router as points_router
from handlers.interaction_handler import InteractionHandler
from jobs.points_jobs import PointsJobs
from services.gamification_service import GamificationService
from services.debounce_merge import collect_pending_after_quiet_window
from services.points_events import (
    level_changed_event,
    makeup_card_changed_event,
    points_changed_event,
)
from services.progress_store import SOURCE_CHAT
from time_utils import build_time_block, describe_business_period

_base_dir = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_base_dir, ".env"))

DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY")
QWEATHER_API_KEY = os.getenv("QWEATHER_API_KEY")
MY_LAT = float(os.getenv("MY_LAT", "0"))
MY_LON = float(os.getenv("MY_LON", "0"))
BOT_WS_TOKEN = (os.getenv("BOT_WS_TOKEN", "") or "").strip()
BOT_HTTP_TOKEN = (os.getenv("BOT_HTTP_TOKEN", "") or "").strip()
APP_USER_ID = os.getenv("APP_USER_ID", "default-user").strip() or "default-user"
AUTH_USERNAME = (os.getenv("AUTH_USERNAME", "") or "").strip()
AUTH_USER_ID = (os.getenv("AUTH_USER_ID", "") or "").strip()
DEFAULT_USER_ID = AUTH_USER_ID or AUTH_USERNAME or APP_USER_ID
DEBUG_REPLY_TRACE = os.getenv("DEBUG_REPLY_TRACE", "0") == "1"
GEMINI_API_KEY = os.getenv("GEMINI_API_KEY", "")
GEMINI_MODEL = os.getenv("GEMINI_MODEL", "gemini-2.5-flash")
VISION_MAX_IMAGE_MB = int(os.getenv("VISION_MAX_IMAGE_MB", "20"))
VISION_MAX_IMAGE_COUNT = int(os.getenv("VISION_MAX_IMAGE_COUNT", "3"))
VISION_ALLOWED_MIME = {
    m.strip() for m in os.getenv("VISION_ALLOWED_MIME", "image/jpeg,image/png").split(",") if m.strip()
}
AUTH_JWT_SECRET = (os.getenv("AUTH_JWT_SECRET", "") or "").strip()
BOT_DEVICE_TOKENS = (os.getenv("BOT_DEVICE_TOKENS", "") or "").strip()
MAX_DEVICE_COUNT = 4
DEVICE_TOKEN_ERROR: str | None = None
DEVICE_TOKEN_MAP: dict[str, AuthContext] = {}
DEVICE_ALLOWLIST: dict[str, DeviceEntry] = {}
try:
    DEVICE_TOKEN_MAP, DEVICE_ALLOWLIST = parse_device_tokens(
        BOT_DEVICE_TOKENS, DEFAULT_USER_ID, max_devices=MAX_DEVICE_COUNT
    )
    if BOT_DEVICE_TOKENS and not DEVICE_ALLOWLIST:
        raise ValueError("BOT_DEVICE_TOKENS 为空")
except ValueError as exc:
    DEVICE_TOKEN_ERROR = str(exc)

DEBOUNCE_BASE = 8.0
DEBOUNCE_EXTENDED = 20.0
DEBOUNCE_EXTEND_WINDOW = 40.0
TAIL_DEBOUNCE = 1.0

HISTORY_SEPARATOR = "──── 新的一天 ────"

GEMINI_TIMEOUT = 30

VISION_DESCRIPTION_PROMPT = (
    "你是一个图片描述助手。请客观、准确地描述用户图片中的内容，供后续文本模型参考。\n"
    "要求：\n"
    "1. 输出纯事实描述：画面主体、环境、动作、人物表情、画面中的文字（如有）等。\n"
    "2. 严禁代入任何人设或角色，严禁使用对话语气，严禁评价图片好坏。\n"
    "3. 如果画面中有可辨认的文字，请逐字引用。\n"
    "4. 如果图片内容不清晰或无法辨认，直接回答：图片内容无法辨认。\n"
    "5. 200 字以内，直接输出描述正文，不要加标题或前缀。"
)

VISION_CONTEXT_TEMPLATE = (
    "【用户发来了一张图片，识图结果】：\n"
    "{vision_description}\n\n"
    "【用户对图片的留言】：\n"
    "{user_text}"
)

TIMELINE_FILE = os.path.join(_base_dir, "chat_timeline.json")
TIMELINE_LOCK = asyncio.Lock()
MEMORY_TIMELINE_FILE = os.path.join(_base_dir, "memory_timeline.json")
MEMORY_TIMELINE_LOCK = asyncio.Lock()

logging.basicConfig(format="%(asctime)s - %(levelname)s - %(message)s", level=logging.INFO)
logger = logging.getLogger(__name__)

state = AppState(history_file=os.path.join(_base_dir, "chat_history.json"))
db = MemoryService(_base_dir)
weather_service = WeatherService(_base_dir, QWEATHER_API_KEY, MY_LAT, MY_LON)
history_store = HistoryStore(state.history_file)
state.user_chat_history.update(history_store.load())

timeline_store = SecureJsonStore(TIMELINE_FILE, logger)
memory_timeline_store = SecureJsonStore(MEMORY_TIMELINE_FILE, logger)

client = AsyncOpenAI(
    api_key=DEEPSEEK_API_KEY,
    base_url="https://api.deepseek.com",
    timeout=httpx.Timeout(120.0, connect=60.0),
)

prompt_service = PromptService(
    client=client,
    memory_service=db,
    notify_owner=lambda *_args, **_kwargs: None,
    scene_cache=state.scene_cache,
    user_memo=USER_MEMO,
    logger=logger,
)

if GEMINI_API_KEY:
    genai.configure(api_key=GEMINI_API_KEY)

app = FastAPI(title="TakiShiina Bot WebSocket API")

ACTIVE_CONNECTIONS: dict[str, set[WebSocket]] = {}
message_buffer: dict[str, list[dict]] = {}
debounce_jobs: dict[str, asyncio.Task] = {}
message_events: dict[str, asyncio.Event] = {}
last_message_at: dict[str, datetime] = {}
is_processing: dict[str, bool] = {}
pending_flush: dict[str, bool] = {}

WORKER_IDLE_TIMEOUT = 600  # 秒：worker 无任何新消息超过该时长则自退出,避免断线后泄漏


def _missing_required_tokens() -> list[str]:
    if DEVICE_TOKEN_ERROR:
        return [f"BOT_DEVICE_TOKENS({DEVICE_TOKEN_ERROR})"]
    missing: list[str] = []
    if not AUTH_JWT_SECRET:
        missing.append("AUTH_JWT_SECRET")
    return missing


@app.on_event("startup")
async def validate_security_config():
    missing = _missing_required_tokens()
    if missing:
        joined = ", ".join(missing)
        logger.critical(f"[SECURITY] 缺少必填鉴权配置: {joined}，服务拒绝启动")
        raise RuntimeError(f"Missing required token env(s): {joined}")


def _resolve_ws_context(token: str) -> AuthContext | None:
    return resolve_auth_context(
        token=token,
        jwt_secret=AUTH_JWT_SECRET,
        device_allowlist=DEVICE_ALLOWLIST,
        legacy_token_map=DEVICE_TOKEN_MAP,
        fallback_tokens=[BOT_WS_TOKEN, BOT_HTTP_TOKEN],
        default_user_id=DEFAULT_USER_ID,
    )


def _extract_ws_auth(websocket: WebSocket) -> tuple[str, str | None]:
    auth_header = websocket.headers.get("authorization")
    if auth_header:
        return auth_header, None

    protocol_header = websocket.headers.get("sec-websocket-protocol") or ""
    if protocol_header:
        protocols = [item.strip() for item in protocol_header.split(",") if item.strip()]
        for item in protocols:
            if item.lower().startswith("auth."):
                return item[5:], item
            if item.lower().startswith("bearer "):
                return item, item

    query_token = websocket.query_params.get("token")
    if query_token:
        logger.warning("[WS] token 出现在 URL query，已拒绝该连接")
    return "", None


def _trace_text(label: str, user_id: str, text: str):
    if not DEBUG_REPLY_TRACE:
        return
    compact = (text or "").replace("\n", "\\n")
    if len(compact) > 280:
        compact = compact[:280] + "...(truncated)"
    logger.info(f"[TRACE][{user_id}] {label}: {compact}")


def parse_timer_instruction(raw_reply: str):
    import re

    timer_pattern = r"\[\[TIMER:(\d{1,2}:\d{2})\|(.*?)\]\]"
    match = re.search(timer_pattern, raw_reply or "")
    if not match:
        return raw_reply, None, None
    target_time_str = match.group(1)
    reminder_text = match.group(2)
    cleaned_reply = re.sub(timer_pattern, "", raw_reply).strip()
    return cleaned_reply, target_time_str, reminder_text


def calc_debounce_window(user_id: str):
    bot_last = state.last_bot_response_time.get(user_id)
    if not bot_last:
        return DEBOUNCE_BASE
    seconds_since_response = (datetime.now(timezone.utc) - bot_last).total_seconds()
    return DEBOUNCE_EXTENDED if seconds_since_response < DEBOUNCE_EXTEND_WINDOW else DEBOUNCE_BASE


def _load_timeline() -> list[dict]:
    try:
        return timeline_store.load([])
    except Exception as e:
        logger.error(f"[WS] 读取时间线失败: {e}")
        return []


def _load_memory_timeline() -> list[dict]:
    try:
        return memory_timeline_store.load([])
    except Exception as e:
        logger.error(f"[WS] 读取记忆时间线失败: {e}")
        return []


EMPTY_REPLY_FALLBACK = "……"


async def _resolve_empty_reply(client, cleaned_reply: str, messages: list[dict]) -> str:
    """空回复兜底：重采样一次，仍为空则用占位文案，禁止空串写入时间线。"""
    retry_messages = list(messages)
    retry_messages.append(
        {
            "role": "system",
            "content": "【空回复兜底】：你刚才的回复经清洗后为空。请用符合人设的自然语气重新回复一句，禁止输出空内容或纯动作描述。",
        }
    )
    try:
        resp = await client.chat.completions.create(
            model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
            messages=retry_messages,
            temperature=1.0,
        )
        retry_raw = (resp.choices[0].message.content or "").strip()
        if retry_raw:
            retry_cleaned, _, _ = parse_timer_instruction(retry_raw)
            retry_final = inject_emojis(sanitize_taki_reply(retry_cleaned))
            if retry_final.strip():
                return retry_final
    except Exception as e:
        logger.warning(f"[WS] 空回复兜底重采样失败: {e}")
    return EMPTY_REPLY_FALLBACK


async def append_timeline(items: list[dict]):
    async with TIMELINE_LOCK:
        data = _load_timeline()
        data.extend(items)
        if len(data) > 3000:
            data = data[-3000:]
        try:
            timeline_store.save(data)
        except Exception as e:
            logger.error(f"[WS] 保存时间线失败: {e}")


async def append_memory_timeline(items: list[dict]):
    async with MEMORY_TIMELINE_LOCK:
        data = _load_memory_timeline()
        data.extend(items)
        if len(data) > 3000:
            data = data[-3000:]
        try:
            memory_timeline_store.save(data)
        except Exception as e:
            logger.error(f"[WS] 保存记忆时间线失败: {e}")


async def extract_user_facts(user_id: str, message: str):
    extraction_prompt = (
        "你是一个信息提取助手。从用户消息中提取关于用户自身的客观事实（如习惯、计划、状态、偏好等）。\n"
        "规则：\n"
        "1. 只提取关于「用户」的事实，忽略闲聊、问候、对他人的描述\n"
        "2. 每条事实以「用户」开头，简洁表述\n"
        "3. 若无可提取事实，返回空数组\n"
        "4. 严格返回JSON数组格式，如：[\"用户今天感冒了\", \"用户计划明天出门\"]\n\n"
        f"用户消息：{message}"
    )

    try:
        resp = await client.chat.completions.create(
            model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
            messages=[{"role": "user", "content": extraction_prompt}],
            temperature=0.3,
        )
        raw = (resp.choices[0].message.content or "[]").strip()
        if raw.startswith("```"):
            raw = raw.split("\n", 1)[-1].rsplit("```", 1)[0].strip()

        facts = json.loads(raw)
        if not isinstance(facts, list):
            return

        timestamp = int(datetime.now(timezone.utc).timestamp() * 1000)
        memory_items = []
        for fact in facts:
            if not isinstance(fact, str):
                continue
            clean_fact = fact.strip()
            if not clean_fact:
                continue

            db.update_profile(user_id, clean_fact)
            fact_item = {
                "factId": f"fact_{uuid.uuid4().hex}",
                "userId": user_id,
                "fact": clean_fact,
                "timestamp": timestamp,
            }
            memory_items.append(fact_item)
            await broadcast_json(
                user_id,
                {"type": "memory.fact.created", "payload": fact_item},
            )

        if memory_items:
            await append_memory_timeline(memory_items)
            logger.info(f"[WS] 已提取并记录 {len(memory_items)} 条用户事实: user={user_id}")
    except json.JSONDecodeError as e:
        logger.warning(f"[WS] 事实提取 JSON 解析失败: {e}")
    except Exception as e:
        logger.error(f"[WS] 事实提取失败: {e}")


async def broadcast_json(user_id: str, data: dict):
    payload = json.dumps(data, ensure_ascii=False)
    dead = []
    for ws in ACTIVE_CONNECTIONS.get(user_id, set()):
        try:
            await ws.send_text(payload)
        except Exception:
            dead.append(ws)
    if dead and user_id in ACTIVE_CONNECTIONS:
        for ws in dead:
            ACTIVE_CONNECTIONS[user_id].discard(ws)


async def send_error(
    user_id: str,
    error_code: str,
    message: str,
    request_id: str | None = None,
    request_ids: list[str] | None = None,
):
    await broadcast_json(
        user_id,
        {
            "type": "bot.error",
            "requestId": request_id,
            "payload": {
                "errorCode": error_code,
                "message": message,
                "requestIds": request_ids or [],
                "timestamp": int(datetime.now(timezone.utc).timestamp() * 1000),
            },
        },
    )


# ================= 互动礼物 · 积分机制 · 等级体系 接入 =================
# 说明：积分/等级数据由 ws_api 进程独占持有——用户在 App 内的一切对话都经 WebSocket
# 抵达本进程，互动回复又必须通过本进程的 ACTIVE_CONNECTIONS 推送，
# 因此把「积分/等级/补签/互动」全部收敛到这一个进程，避免多进程双写同一份账本。
# REST 路由挂在同一 FastAPI app 上（反向代理需把 /api/v1/interaction、/api/v1/points、
# /api/v1/level、/api/v1/admin 指向本服务，见 README 部署说明）。

gamification = GamificationService(
    base_dir=_base_dir,
    logger=logger,
    default_user_id=DEFAULT_USER_ID,
)

configure_auth(
    AuthRuntime(
        jwt_secret=AUTH_JWT_SECRET,
        device_allowlist=DEVICE_ALLOWLIST,
        legacy_token_map=DEVICE_TOKEN_MAP,
        fallback_tokens=[BOT_WS_TOKEN, BOT_HTTP_TOKEN],
        default_user_id=DEFAULT_USER_ID,
    )
)
register_gamification(gamification)

app.include_router(interaction_router, prefix="/api/v1", tags=["interaction"])
app.include_router(points_router, prefix="/api/v1", tags=["points"])
app.include_router(level_router, prefix="/api/v1", tags=["level"])
app.include_router(admin_router, prefix="/api/v1", tags=["admin"])


def _load_alert_sender():
    """飞书告警为可选依赖（PRD 九·可观测性：互动/积分异常接入既有告警）。"""
    try:
        from alert_sender import send_alert

        return send_alert
    except Exception as exc:  # pragma: no cover - 取决于部署环境
        logger.warning(f"[积分] 飞书告警不可用，已降级为仅日志: {exc}")
        return None


ALERT_SENDER = _load_alert_sender()

# B 方案：用户「先发文字、防抖没到就送礼物」时，等静默窗口结束并把那批消息摘走一起回答，
# 使整轮只产生一条回复（被摘消息的 requestId 会随回复下发，客户端据此把气泡标记为已发送）。
MAX_INTERACTION_MERGE_WAIT = 25.0


async def wait_and_collect_pending_texts(user_id: str) -> list[dict]:
    return await collect_pending_after_quiet_window(
        user_id,
        buffer=message_buffer,
        last_message_at=last_message_at,
        window_seconds=calc_debounce_window,
        max_wait_seconds=MAX_INTERACTION_MERGE_WAIT,
        logger=logger,
    )


gamification.interaction = InteractionHandler(
    config_service=gamification.config,
    points_service=gamification.points,
    makeup_card_service=gamification.makeup,
    level_service=gamification.level,
    client=client,
    prompt_service=prompt_service,
    weather_service=weather_service,
    state=state,
    history_store=history_store,
    append_timeline=append_timeline,
    broadcast_json=broadcast_json,
    # B 方案：用户刚发文字就送礼时，等静默窗口结束并把那些消息摘过来一起回答（只回一条）
    pending_flush_waiter=wait_and_collect_pending_texts,
    # 兜底：万一缓冲没被摘走（例如聊天 worker 正在处理），也让礼物回复看到那句文字
    pending_text_provider=lambda uid: [
        msg.get("content", "") for msg in message_buffer.get(uid, []) if msg.get("content")
    ],
    logger=logger,
    alert_sender=ALERT_SENDER,
)

points_jobs = PointsJobs(
    gamification=gamification,
    logger=logger,
    broadcast_json=broadcast_json,
    alert_sender=ALERT_SENDER,
)


async def record_chat_activity(user_id: str) -> None:
    """用户主动发起的对话 → 每日有效对话 + 积分结算（PRD FR-10 / 4.3）。

    早安/晚安等系统推送走 ``send_greeting``，不经过本函数，因此天然不计入积分。
    """
    try:
        gamification.touch_user(user_id)
        result = gamification.points.record_user_activity(user_id, source=SOURCE_CHAT)
    except Exception as exc:
        logger.exception(f"[积分] 记录有效对话失败: user={user_id}, err={exc}")
        return

    try:
        for entry in result.rewards:
            await broadcast_json(user_id, points_changed_event(entry))
        if result.level is not None and result.level.level_changed:
            await broadcast_json(user_id, level_changed_event(result.level))
        granted = gamification.makeup.ensure_initial_grant(user_id)
        if granted:
            await broadcast_json(
                user_id,
                makeup_card_changed_event(gamification.makeup.get_summary(user_id), "MONTHLY_GRANT"),
            )
    except Exception as exc:
        logger.warning(f"[积分] 事件推送失败: user={user_id}, err={exc}")


async def process_buffered_messages(user_id: str):
    if is_processing.get(user_id):
        pending_flush[user_id] = True
        return

    buffered = message_buffer.get(user_id, [])
    if not buffered:
        return
    message_buffer[user_id] = []
    is_processing[user_id] = True

    try:
        request_ids = [m["requestId"] for m in buffered]
        merged_text = "\n".join(m["content"] for m in buffered if m["content"])
        merged_request_id = request_ids[-1] if request_ids else str(uuid.uuid4())
        merged_images = []
        for m in buffered:
            merged_images.extend(m.get("images", []))
        has_images = len(merged_images) > 0

        await broadcast_json(user_id, {"type": "chat.typing", "payload": {"typing": True}})

        history = state.user_chat_history.setdefault(user_id, [])
        weather_info = await weather_service.get_weather_str(force=True)

        utc_now = datetime.now(timezone.utc)
        beijing_now = utc_now + timedelta(hours=8)
        # 服务端判定时段后注入（避免模型自己按 HH:MM 猜时段）
        time_str = build_time_block(beijing_now)

        time_enforcement = "\n".join((
            "1. **时间锚定**：你必须无条件信任上述【当前北京时间】，绝不能凭训练数据或常识猜测当前时间。",
            "2. **全天候禁止催睡**：无论现在是凌晨几点，绝对禁止主动建议用户去睡觉或休息。",
            "3. **深夜默契**：如果用户深夜还在发消息，表示他想通宵，你要配合而不是扫兴。",
        ))

        system_prompt = await prompt_service.get_system_prompt(user_id)
        system_content = f"{system_prompt}\n{time_str}\n{weather_info}\n\n{time_enforcement}"
        _trace_text("SYS_PROMPT", user_id, system_prompt)

        emotional_prompt = ""
        for keyword, instruction in EMOTIONAL_TRIGGERS.items():
            if keyword in merged_text:
                emotional_prompt = f"\n\n💝 {instruction}"
                break

        trigger_prompt = ""
        for keyword, (reaction, _intensity) in LORE_TRIGGERS.items():
            if keyword.lower() in merged_text.lower():
                trigger_prompt = f"\n\n🛑【突发状态】：{reaction}"
                break

        anchor_prompt = (
            "【强制提醒】：保持“酷但笨拙”人设；禁止动作叙事；回复长短跟随内容。"
            "\n【注意力锚定】：只针对下一条用户消息回复。历史对话仅作为背景参考，不要回应历史中已结束的话题。"
            "\n【去重要求】：避免复用最近10次回复的开头词、句式骨架和结尾口头禅。"
            "\n【禁止时间戳】：历史消息中的【时间】标记仅供你理解时间线，绝对禁止在回复中输出时间戳或时刻标记（如 [8-22 17:22]、【14:32】）。"
            f"{emotional_prompt}{trigger_prompt}"
        )

        # 取分隔标记之后的历史，最多 10 条
        sep_idx = -1
        for i in range(len(history) - 1, -1, -1):
            if history[i].get("role") == "system" and history[i].get("content") == HISTORY_SEPARATOR:
                sep_idx = i
                break
        if sep_idx >= 0:
            recent_history = history[sep_idx + 1:]
        else:
            recent_history = history
        recent_history = [
            m for m in recent_history
            if not (m.get("role") == "system" and m.get("content") == HISTORY_SEPARATOR)
        ]
        recent_history = recent_history[-10:]
        recent_history, polluted_tails = clean_short_term_history(recent_history, min_repeat=3)
        if DEBUG_REPLY_TRACE:
            recent_assistant_count = sum(1 for m in recent_history if m.get("role") == "assistant")
            logger.info(
                f"[TRACE][{user_id}] SHORT_HISTORY size={len(recent_history)} assistant={recent_assistant_count} tails={len(polluted_tails)}"
            )

        full_content = ""
        bot_message_id = str(uuid.uuid4())
        user_message_id = merged_request_id

        user_message = {"role": "user", "content": merged_text}

        if has_images:
            if not GEMINI_API_KEY:
                await send_error(user_id, "GEMINI_NOT_CONFIGURED", "服务端未配置 Gemini Key", merged_request_id, request_ids)
                return

            # ---- Stage 1: Gemini 识图（结果隐式，不直接回给用户）----
            await broadcast_json(
                user_id,
                {
                    "type": "chat.typing",
                    "payload": {"typing": True, "stage": "vision"},
                },
            )
            model = genai.GenerativeModel(GEMINI_MODEL)
            vision_prompt = VISION_DESCRIPTION_PROMPT
            if merged_text:
                vision_prompt += f"\n\n用户留言：{merged_text}"
            parts = [vision_prompt]
            for img in merged_images[:VISION_MAX_IMAGE_COUNT]:
                parts.append(
                    {
                        "mime_type": img["mimeType"],
                        "data": img["raw"],
                    }
                )
            try:
                resp = await asyncio.wait_for(
                    asyncio.to_thread(model.generate_content, parts),
                    timeout=GEMINI_TIMEOUT,
                )
                vision_description = (resp.text or "").strip()
            except asyncio.TimeoutError:
                logger.warning(f"[WS] 识图超时，使用降级描述: user={user_id}")
                vision_description = "图片内容无法辨认（识图超时）。"
            except Exception as e:
                logger.exception(f"[WS] 识图失败，使用降级描述: {e}")
                vision_description = "图片内容无法辨认（识图失败）。"
            _trace_text("VISION_DESC", user_id, vision_description)

            user_message = {
                "role": "user",
                "content": VISION_CONTEXT_TEMPLATE.format(
                    vision_description=vision_description,
                    user_text=merged_text,
                ),
            }

        messages = [{"role": "system", "content": system_content}]
        messages.extend(with_history_timestamp(m) for m in recent_history)
        messages.append({"role": "system", "content": anchor_prompt})
        messages.append(user_message)

        # ---- Stage 2: DeepSeek 流式生成 ----
        if has_images:
            await broadcast_json(
                user_id,
                {
                    "type": "chat.typing",
                    "payload": {"typing": True, "stage": "generating"},
                },
            )
        response = await asyncio.wait_for(
            client.chat.completions.create(
                model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
                messages=messages,
                temperature=0.75,
                stream=True,
            ),
            timeout=90.0,
        )

        async for chunk in response:
            delta = chunk.choices[0].delta.content or ""
            if delta:
                full_content += delta
                await broadcast_json(
                    user_id,
                    {
                        "type": "chat.reply.stream",
                        "requestId": merged_request_id,
                        "payload": {
                            "delta": delta,
                            "done": False,
                            "contentType": "mixed" if has_images else "text",
                            "modelProvider": "deepseek",
                        },
                    },
                )

        _trace_text("A_RAW", user_id, full_content)
        cleaned_reply, timer_at, timer_text = parse_timer_instruction(full_content)
        _trace_text("B_PARSED", user_id, cleaned_reply)
        cleaned_reply = strip_polluted_tail(cleaned_reply, polluted_tails)
        _trace_text("C_STRIPPED", user_id, cleaned_reply)
        recent_assistant_replies = [m.get("content", "") for m in recent_history if m.get("role") == "assistant"]
        if is_repetitive_reply(cleaned_reply, recent_assistant_replies, threshold=0.88):
            logger.info(f"[去重] 命中重复回复，触发重采样: user={user_id}")
            dedupe_messages = list(messages)
            dedupe_messages.append(
                {
                    "role": "system",
                    "content": "【去重重采样】：禁止复用最近10条回复的开头、句式和收尾，保持人设但换一种自然表达。",
                }
            )
            retry_resp = await client.chat.completions.create(
                model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
                messages=dedupe_messages,
                temperature=1.0,
            )
            retry_raw = retry_resp.choices[0].message.content or cleaned_reply
            _trace_text("A_RETRY_RAW", user_id, retry_raw)
            cleaned_retry, timer_at_retry, timer_text_retry = parse_timer_instruction(retry_raw)
            cleaned_reply = strip_polluted_tail(cleaned_retry or cleaned_reply, polluted_tails)
            _trace_text("C_RETRY_STRIPPED", user_id, cleaned_reply)
            if timer_at_retry and timer_text_retry:
                timer_at, timer_text = timer_at_retry, timer_text_retry
        final_reply = inject_emojis(sanitize_taki_reply(cleaned_reply))
        if not final_reply.strip():
            logger.warning(f"[WS] 空回复兜底重采样: user={user_id}")
            final_reply = await _resolve_empty_reply(client, cleaned_reply, messages)
            _trace_text("D_FINAL_FALLBACK", user_id, final_reply)
        _trace_text("D_FINAL", user_id, final_reply)

        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        merged_record = merged_text
        if has_images:
            merged_record = (merged_text + "\n\n" if merged_text else "") + f"[ImageCount={len(merged_images)}]"
        history.append({"role": "user", "content": merged_record, "ts": now_ms - 1})
        history.append({"role": "assistant", "content": final_reply, "ts": now_ms})
        if len(history) > 300:
            state.user_chat_history[user_id] = history[-300:]
        history_store.save(state.user_chat_history)

        await append_timeline(
            [
                {
                    "messageId": user_message_id,
                    "userId": user_id,
                    "role": "user",
                    "content": merged_record,
                    "timestamp": now_ms - 1,
                },
                {
                    "messageId": bot_message_id,
                    "userId": user_id,
                    "role": "bot",
                    "content": final_reply,
                    "timestamp": now_ms,
                },
            ]
        )

        state.last_activity[user_id] = datetime.now(timezone.utc)
        state.last_bot_response_time[user_id] = datetime.now(timezone.utc)
        asyncio.create_task(extract_user_facts(user_id, merged_text))

        await broadcast_json(
            user_id,
            {
                "type": "chat.reply.stream",
                "requestId": merged_request_id,
                "payload": {
                    "delta": "",
                    "done": True,
                    "messageId": bot_message_id,
                    "finalContent": final_reply,
                    "timestamp": now_ms,
                    "contentType": "mixed" if has_images and merged_text else ("image" if has_images else "text"),
                    "modelProvider": "deepseek",
                    "timerInstruction": {"target": timer_at, "text": timer_text} if timer_at else None,
                    "requestIds": request_ids,
                },
            },
        )
        await broadcast_json(user_id, {"type": "chat.typing", "payload": {"typing": False}})
    except asyncio.TimeoutError:
        await broadcast_json(user_id, {"type": "chat.typing", "payload": {"typing": False}})
        await send_error(user_id, "AI_TIMEOUT", "AI 响应超时，请重试", merged_request_id, request_ids)
    except Exception as e:
        logger.exception(f"[WS] 处理消息失败: {e}")
        await broadcast_json(user_id, {"type": "chat.typing", "payload": {"typing": False}})
        await send_error(user_id, "INTERNAL_ERROR", f"处理失败: {str(e)}", merged_request_id, request_ids)
    finally:
        is_processing[user_id] = False
        # 处理期间有新消息到达，给一小段尾批窗口再触发下一轮合并。
        need_tail_flush = pending_flush.pop(user_id, False) or bool(message_buffer.get(user_id))
        if need_tail_flush:
            asyncio.create_task(_tail_flush_wakeup(user_id))


async def _tail_flush_wakeup(user_id: str):
    await asyncio.sleep(TAIL_DEBOUNCE)
    event = message_events.get(user_id)
    if event:
        event.set()


def _ensure_user_worker(user_id: str):
    old_worker = debounce_jobs.get(user_id)
    if old_worker and not old_worker.done():
        return

    message_events.setdefault(user_id, asyncio.Event())

    async def _worker():
        while True:
            event = message_events[user_id]
            try:
                await asyncio.wait_for(event.wait(), timeout=WORKER_IDLE_TIMEOUT)
            except asyncio.TimeoutError:
                # 长时间无消息：worker 自退出并清理状态,避免连接断开后协程泄漏
                logger.info(f"[防抖] worker 空闲超时退出: user={user_id}")
                debounce_jobs.pop(user_id, None)
                message_events.pop(user_id, None)
                last_message_at.pop(user_id, None)
                is_processing.pop(user_id, None)
                pending_flush.pop(user_id, None)
                message_buffer.pop(user_id, None)
                return
            event.clear()

            # 等待“静默窗口”：在窗口内若收到新消息则重置等待。
            while True:
                last_at = last_message_at.get(user_id)
                if not last_at:
                    break
                window = calc_debounce_window(user_id)
                elapsed = (datetime.now(timezone.utc) - last_at).total_seconds()
                remaining = window - elapsed
                if remaining <= 0:
                    break

                try:
                    await asyncio.wait_for(event.wait(), timeout=remaining)
                    event.clear()
                    continue
                except asyncio.TimeoutError:
                    break

            if is_processing.get(user_id):
                pending_flush[user_id] = True
                continue

            await process_buffered_messages(user_id)

    debounce_jobs[user_id] = asyncio.create_task(_worker())
    logger.info(f"[防抖] 启动用户 worker: user={user_id}")


def schedule_debounce(user_id: str):
    _ensure_user_worker(user_id)
    message_events[user_id].set()
    buffered_count = len(message_buffer.get(user_id, []))
    logger.info(
        f"[防抖] 收到消息，等待静默窗口: user={user_id}, window={calc_debounce_window(user_id)}s, buffered={buffered_count}"
    )


@app.websocket("/ws/chat")
async def websocket_chat(websocket: WebSocket):
    token, subprotocol = _extract_ws_auth(websocket)
    auth = _resolve_ws_context(token)
    if not auth:
        # 先 accept 再发送 auth.expired，让客户端区分「token 过期/非法」与网络层失败，
        # 避免 accept 前 close 被呈现为不透明的 HTTP 403 握手错误。
        if subprotocol:
            await websocket.accept(subprotocol=subprotocol)
        else:
            await websocket.accept()
        await websocket.send_text(
            json.dumps(
                {
                    "type": "auth.expired",
                    "payload": {
                        "reason": "invalid_token",
                        "timestamp": int(datetime.now(timezone.utc).timestamp() * 1000),
                    },
                },
                ensure_ascii=False,
            )
        )
        await websocket.close(code=4001, reason="Invalid token")
        logger.warning("[WS] 连接被拒绝: token 无效")
        return

    if subprotocol:
        await websocket.accept(subprotocol=subprotocol)
    else:
        await websocket.accept()
    user_id = auth.user_id
    device_id = auth.device_id
    ACTIVE_CONNECTIONS.setdefault(user_id, set()).add(websocket)
    logger.info(f"[WS] 连接建立: user={user_id}, device={device_id}")

    try:
        while True:
            raw_message = await websocket.receive_text()
            try:
                message = json.loads(raw_message)
            except json.JSONDecodeError:
                await send_error(user_id, "INVALID_JSON", "无效的 JSON 格式")
                continue

            msg_type = message.get("type", "")
            request_id = message.get("requestId") or str(uuid.uuid4())

            if msg_type == "ping":
                await broadcast_json(
                    user_id,
                    {"type": "pong", "timestamp": int(datetime.now(timezone.utc).timestamp() * 1000)},
                )
                continue

            if msg_type == "chat.message":
                payload = message.get("payload", {})
                content = (payload.get("content") or "").strip()
                raw_images = payload.get("images") or []
                logger.info(f"[WS] 收到消息: user={user_id}, content={content[:50]}...")
                if not content and not raw_images:
                    await send_error(user_id, "EMPTY_MESSAGE", "消息内容不能为空", request_id)
                    continue
                parsed_images = []
                if raw_images:
                    if len(raw_images) > VISION_MAX_IMAGE_COUNT:
                        await send_error(user_id, "VISION_IMAGE_COUNT_EXCEEDED", f"单次最多 {VISION_MAX_IMAGE_COUNT} 张图片", request_id)
                        continue
                    for idx, item in enumerate(raw_images):
                        mime_type = (item or {}).get("mimeType", "").strip().lower()
                        data_base64 = (item or {}).get("dataBase64", "")
                        if mime_type not in VISION_ALLOWED_MIME:
                            await send_error(user_id, "VISION_INVALID_MIME", f"第 {idx + 1} 张图片格式不支持", request_id)
                            parsed_images = []
                            break
                        try:
                            raw = base64.b64decode(data_base64)
                        except Exception:
                            await send_error(user_id, "VISION_INVALID_BASE64", f"第 {idx + 1} 张图片解析失败", request_id)
                            parsed_images = []
                            break
                        if len(raw) > VISION_MAX_IMAGE_MB * 1024 * 1024:
                            await send_error(user_id, "VISION_IMAGE_TOO_LARGE", f"第 {idx + 1} 张图片超过 {VISION_MAX_IMAGE_MB}MB", request_id)
                            parsed_images = []
                            break
                        parsed_images.append({"mimeType": mime_type, "raw": raw})
                    if raw_images and not parsed_images:
                        continue
                message_buffer.setdefault(user_id, []).append({"requestId": request_id, "content": content, "images": parsed_images})
                last_message_at[user_id] = datetime.now(timezone.utc)
                # 多设备回声：该用户的其他在线设备实时看到这条发言；发送端按 requestId 幂等去重
                await broadcast_json(
                    user_id,
                    {
                        "type": "chat.message.echo",
                        "requestId": request_id,
                        "payload": {
                            "content": content,
                            "imageCount": len(parsed_images),
                            "timestamp": int(datetime.now(timezone.utc).timestamp() * 1000),
                            "originDeviceId": device_id,
                        },
                    },
                )
                await broadcast_json(
                    user_id,
                    {
                        "type": "chat.queued",
                        "requestId": request_id,
                        "payload": {"debounceWindowSec": calc_debounce_window(user_id)},
                    },
                )
                # PRD FR-10 / 4.3：用户主动发起的对话计为当日有效对话并结算积分（幂等）
                await record_chat_activity(user_id)
                schedule_debounce(user_id)
                continue

            await send_error(user_id, "UNKNOWN_TYPE", f"未知的消息类型: {msg_type}", request_id)
    except WebSocketDisconnect:
        logger.info(f"[WS] 连接断开: user={user_id}, device={device_id}")
    except Exception as e:
        logger.exception(f"[WS] 连接异常: {e}")
    finally:
        ACTIVE_CONNECTIONS.get(user_id, set()).discard(websocket)
        # 注意：不取消 worker、不清理状态。断线后防抖/生成流程继续跑完并写入 timeline,
        # 消息不丢失,App 重连后通过 full sync 拉回。worker 空闲超时后自退出(见 _ensure_user_worker)。


@app.get("/healthz")
async def healthz():
    return {"status": "ok", "service": "ws_api"}


# ================= 心情（立希当前状态）整点刷新 =================
# 背景：原机制只把 expires_at 写进缓存、从未比对，且整点刷新任务只挂在 bot.py（Telegram）上，
# 所以在 App 这条链路上，心情从 ws_api 进程启动后就不再变化。这里补上：
#   S1 进程内整点任务：整点后随机 0~5 分钟主动刷新（用户请求永远命中热缓存，不额外等 LLM）
#   E5 过期兜底：get_random_scene 发现过期时先返回旧心情、后台异步重建（PromptService 内实现）
#   S3 内部接口：POST /internal/scene/refresh 供运维手动触发/验证

SCENE_REFRESH_MAX_OFFSET_SECONDS = 300


def _scene_target_user_ids() -> list[str]:
    users = set(gamification.known_user_ids())
    users.update(str(uid) for uid in state.user_chat_history.keys())
    users.add(DEFAULT_USER_ID)
    return sorted(uid for uid in users if uid)


async def refresh_all_scenes() -> int:
    """刷新所有相关用户的「立希当前状态」，返回成功数。"""
    targets = _scene_target_user_ids()
    succeeded = 0
    for uid in targets:
        try:
            scene = await prompt_service.refresh_scene(uid)
            if scene:
                succeeded += 1
        except Exception as exc:
            logger.warning(f"[心情] 刷新失败: user={uid}, err={exc}")
    if succeeded:
        logger.info(
            f"[心情] 已刷新 {succeeded}/{len(targets)} 个用户（当前时段：{describe_business_period()}）"
        )
    return succeeded


async def scene_refresh_scheduler():
    """S1：每个自然小时刷新一次心情（整点后随机 0~5 分钟触发，避免整点打 API）。"""
    current_hour: str | None = None
    pending_offset: int | None = None
    while True:
        try:
            beijing_now = datetime.now(timezone.utc) + timedelta(hours=8)
            hour_key = beijing_now.strftime("%Y-%m-%dT%H")
            if hour_key != current_hour:
                current_hour = hour_key
                pending_offset = random.randint(0, SCENE_REFRESH_MAX_OFFSET_SECONDS)
                logger.info(
                    f"[心情] {beijing_now.strftime('%H:%M')} 进入新整点，"
                    f"{pending_offset}s 后刷新立希当前状态"
                )
            if pending_offset is not None:
                elapsed = beijing_now.minute * 60 + beijing_now.second
                if elapsed >= pending_offset:
                    pending_offset = None
                    await refresh_all_scenes()
        except Exception as exc:
            logger.exception(f"[心情] 整点刷新调度异常: {exc}")
            if ALERT_SENDER:
                try:
                    ALERT_SENDER(f"❗ 心情整点刷新异常: {exc}")
                except Exception:
                    pass
        await asyncio.sleep(30)


@app.post("/internal/scene/refresh")
async def internal_scene_refresh(
    request: Request,
    x_internal_token: str | None = Header(default=None),
    userId: str | None = None,
):
    """S3：手动触发心情刷新（仅本机 + 内部 token）。

    例：curl -X POST "http://127.0.0.1:8001/internal/scene/refresh?userId=kris" \\
         -H "X-Internal-Token: $BOT_WS_TOKEN"
    """
    client_host = (request.client.host if request.client else "") or ""
    if client_host not in ("127.0.0.1", "::1", "localhost"):
        raise HTTPException(status_code=403, detail="仅允许本机调用")
    allowed = {token for token in (BOT_WS_TOKEN, BOT_HTTP_TOKEN) if token}
    if not allowed or (x_internal_token or "").strip() not in allowed:
        raise HTTPException(status_code=401, detail="内部 token 无效")

    if userId:
        scene = await prompt_service.refresh_scene(userId)
        return {
            "code": 0,
            "message": "ok",
            "data": {"userId": userId, "scene": scene, "period": describe_business_period()},
        }
    count = await refresh_all_scenes()
    return {
        "code": 0,
        "message": "ok",
        "data": {
            "refreshedUsers": count,
            "userIds": _scene_target_user_ids(),
            "period": describe_business_period(),
            "scenes": {uid: state.scene_cache.get(uid, {}).get("scene") for uid in _scene_target_user_ids()},
        },
    }


# ================= 定时问候 =================

MORNING_SCRIPTS = [
    "【情境】：昨晚没睡好，有严重的起床气，说话很冲，但其实是想让用户哄。",
    "【情境】：起得很早，正在喝咖啡/抹茶，心情意外地不错，稍微有点温柔。",
    "【情境】：睡过头了！！非常慌张，发消息的时候嘴里好像还叼着面包。",
    "【情境】：不想起床，想赖床，发消息撒娇说'能不能再睡五分钟'。",
    "【情境】：外面下雨/天气不好，心情低落，嘟囔着不想出门。",
    "【情境】：只是单纯地想念用户了，醒来第一件事就是想确认他在不在。",
]

NIGHT_SCRIPTS = [
    "【情境】：正戴着耳机专注于写代码/写歌词，发现用户发消息，摘下一只耳机随口回应，完全没有要睡的意思。",
    "【情境】：刚刚开了一罐新的能量饮料，眼神死死盯着屏幕，漫不经心地问用户'你那边进度怎么样'。",
    "【情境】：因为卡在某个Bug/乐段上很烦躁，看到用户还在，稍微得到了一点安慰，嘟囔着'既然醒着就陪我再耗一会儿'。",
    "【情境】：看了一眼现在的确切时间，冷笑一声'呵，这个点了还没倒下吗？体力不错嘛'。",
    "【情境】：突然感到饿了，问用户'喂，便利店还开着吗'，企图拉用户一起吃夜宵。",
    "【情境】：只有在深夜才展露出的坦率，安静地打字说'只有这个时候世界才安静点...你不睡挺好的'。",
]


async def send_greeting(user_id: str, scenario_type: str):
    """生成并推送定时问候"""
    # 检查用户近 2 小时是否活跃
    user_last = state.last_activity.get(user_id)
    if user_last:
        hours_since = (datetime.now(timezone.utc) - user_last).total_seconds() / 3600
        if hours_since < 2:
            logger.info(f"[问候] [{scenario_type}] 用户 {hours_since:.1f}h 前有活动，跳过")
            return

    # 20% 概率跳过
    if random.random() < 0.2:
        logger.info(f"[问候] [{scenario_type}] 立希偷懒，跳过本次问候")
        return

    beijing_now = datetime.now(timezone.utc) + timedelta(hours=8)
    current_time_str = beijing_now.strftime("%H:%M")
    current_period = describe_business_period(beijing_now)
    time_block = build_time_block(beijing_now)

    if scenario_type == "morning":
        selected_script = random.choice(MORNING_SCRIPTS)
        base_instruction = f"现在是北京时间 {current_time_str}（{current_period}）。作为立希给用户发早安。"
    else:
        selected_script = random.choice(NIGHT_SCRIPTS)
        base_instruction = (
            f"现在是北京时间 {current_time_str}（{current_period}）。"
            "用户还没睡。作为立希，不要发\"晚安\"（因为发了晚安话题就结束了）。"
            "你要发一条消息确认他在干什么，或者吐槽他怎么还醒着，并表示你也还醒着，可以继续陪他。"
        )

    scenario_label = "早安" if scenario_type == "morning" else "深夜问候"
    final_instruction = (
        f"{time_block}\n\n"
        f"{base_instruction}\n\n"
        f"本次随机到的灵感剧本：\n{selected_script}\n\n"
        f"【强制逻辑修正】：\n"
        f"1. 时间一致性：结合【当前时段：{current_period}】与【当前北京时间 {current_time_str}】来生成台词，不要自己重新判断时段。\n"
        f"2. 本任务固定为「{scenario_label}」，若时段标签与任务不完全一致（例如延迟到午后才触发早安），以任务为准。\n"
        f"3. 不要暴露你在扮演，直接进入角色说话。\n"
        f"4. 语气要符合剧本的情境，且如果情境是匆忙或困倦，句子要短、碎！"
    )

    try:
        system_prompt = await prompt_service.get_system_prompt(user_id)
        weather_info = await weather_service.get_weather_str(force=True)
        response = await client.chat.completions.create(
            model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
            messages=[
                {
                    "role": "system",
                    "content": f"{system_prompt}\n\n{weather_info}\n\n🎯 当前任务: {final_instruction}",
                }
            ],
            temperature=0.85,
        )
        raw_reply = response.choices[0].message.content or ""
        final_reply = inject_emojis(sanitize_taki_reply(raw_reply))
        if not final_reply.strip():
            logger.warning(f"[问候] 空回复，使用占位文案: user={user_id}")
            final_reply = EMPTY_REPLY_FALLBACK

        greeting_id = f"greeting_{uuid.uuid4().hex}"
        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)

        # 写入历史
        history = state.user_chat_history.setdefault(user_id, [])
        history.append({"role": "assistant", "content": final_reply, "ts": now_ms})
        if len(history) > 300:
            state.user_chat_history[user_id] = history[-300:]
        history_store.save(state.user_chat_history)

        await append_timeline(
            [{"messageId": greeting_id, "userId": user_id, "role": "bot", "content": final_reply, "timestamp": now_ms}]
        )

        # 推送给 app（与普通回复格式一致，附加问候标记字段，旧版 app 会忽略未知字段）
        await broadcast_json(
            user_id,
            {
                "type": "chat.reply.stream",
                "requestId": greeting_id,
                "payload": {
                    "delta": "",
                    "done": True,
                    "messageId": greeting_id,
                    "finalContent": final_reply,
                    "timestamp": now_ms,
                    "requestIds": [greeting_id],
                    "messageKind": "greeting",
                    "greetingScenario": scenario_type,
                },
            },
        )

        state.last_bot_response_time[user_id] = datetime.now(timezone.utc)
        logger.info(f"[问候] [{scenario_type}] 发送成功: {final_reply[:50]}...")
    except Exception as e:
        logger.exception(f"[问候] [{scenario_type}] 发送失败: {e}")


async def greeting_scheduler():
    """每日定时问候调度器"""
    triggered_morning = None
    triggered_night = None

    while True:
        try:
            now_utc = datetime.now(timezone.utc)
            beijing_now = now_utc + timedelta(hours=8)
            today_str = beijing_now.strftime("%Y-%m-%d")
            hour, minute = beijing_now.hour, beijing_now.minute

            # 早安窗口：09:00 触发，随机延迟 0~180 分钟
            if hour == 9 and minute == 0 and triggered_morning != today_str:
                triggered_morning = today_str
                delay = random.randint(0, 180 * 60)
                logger.info(f"[问候] 早安已安排，{delay // 60} 分钟后发送")
                asyncio.create_task(_delayed_greeting(DEFAULT_USER_ID, "morning", delay))

            # 晚安窗口：23:00 触发，随机延迟 0~120 分钟
            if hour == 23 and minute == 0 and triggered_night != today_str:
                triggered_night = today_str
                delay = random.randint(0, 120 * 60)
                logger.info(f"[问候] 晚安已安排，{delay // 60} 分钟后发送")
                asyncio.create_task(_delayed_greeting(DEFAULT_USER_ID, "night", delay))

        except Exception as e:
            logger.exception(f"[问候] 调度器异常: {e}")

        await asyncio.sleep(60)


async def _delayed_greeting(user_id: str, scenario_type: str, delay_seconds: int):
    """延迟后执行问候"""
    await asyncio.sleep(delay_seconds)
    await send_greeting(user_id, scenario_type)


@app.on_event("startup")
async def on_startup():
    asyncio.create_task(greeting_scheduler())
    asyncio.create_task(history_separator_scheduler())
    # 积分/等级定时任务：断签扫描与提前提醒、月度补签卡发放、纪念日奖励（PRD §3.2 jobs/）
    asyncio.create_task(points_jobs.run_forever())
    # 心情整点刷新（S1）：让「立希当前状态」真正每小时变化
    asyncio.create_task(scene_refresh_scheduler())
    logger.info("[启动] 定时问候调度器已启动")
    logger.info("[启动] 历史分隔调度器已启动")
    logger.info("[启动] 积分/等级定时任务已启动")
    logger.info("[启动] 心情整点刷新任务已启动")


# ================= 每日历史软切割 =================

def insert_daily_separator(user_id: str):
    """向 history 中插入每日分隔标记"""
    history = state.user_chat_history.setdefault(user_id, [])
    # 防止重复插入
    if history and history[-1].get("role") == "system" and history[-1].get("content") == HISTORY_SEPARATOR:
        logger.info(f"[分隔] 已存在分隔标记，跳过: user={user_id}")
        return
    history.append({"role": "system", "content": HISTORY_SEPARATOR})
    if len(history) > 300:
        state.user_chat_history[user_id] = history[-300:]
    history_store.save(state.user_chat_history)
    logger.info(f"[分隔] 已插入每日分隔标记: user={user_id}")


async def history_separator_scheduler():
    """每日北京时间 06:00 插入历史分隔标记"""
    triggered_date = None

    while True:
        try:
            now_utc = datetime.now(timezone.utc)
            beijing_now = now_utc + timedelta(hours=8)
            today_str = beijing_now.strftime("%Y-%m-%d")
            hour, minute = beijing_now.hour, beijing_now.minute

            if hour == 6 and minute == 0 and triggered_date != today_str:
                user_id = DEFAULT_USER_ID
                user_last = state.last_activity.get(user_id)

                # 如果用户 30 分钟内有活动，延迟 30 分钟后重试
                if user_last:
                    minutes_since = (now_utc - user_last).total_seconds() / 60
                    if minutes_since < 30:
                        logger.info(f"[分隔] 用户 {minutes_since:.0f} 分钟前有活动，延迟 30 分钟")
                        await asyncio.sleep(30 * 60)
                        # 延迟后再次检查
                        now_utc = datetime.now(timezone.utc)
                        user_last = state.last_activity.get(user_id)
                        if user_last:
                            minutes_since = (now_utc - user_last).total_seconds() / 60
                            if minutes_since < 30:
                                logger.info(f"[分隔] 用户仍活跃，强制插入分隔标记")

                triggered_date = today_str
                insert_daily_separator(user_id)

        except Exception as e:
            logger.exception(f"[分隔] 调度器异常: {e}")

        await asyncio.sleep(60)


if __name__ == "__main__":
    import uvicorn

    host = os.getenv("BOT_WS_HOST", "0.0.0.0")
    port = int(os.getenv("BOT_WS_PORT", "8001"))
    uvicorn.run("ws_api:app", host=host, port=port, reload=False)
