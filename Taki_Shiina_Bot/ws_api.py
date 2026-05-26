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
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
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
)

_base_dir = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_base_dir, ".env"))

DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY")
OPENWEATHER_API_KEY = os.getenv("OPENWEATHER_API_KEY")
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
VISION_MAX_IMAGE_MB = int(os.getenv("VISION_MAX_IMAGE_MB", "3"))
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

TIMELINE_FILE = os.path.join(_base_dir, "chat_timeline.json")
TIMELINE_LOCK = asyncio.Lock()
MEMORY_TIMELINE_FILE = os.path.join(_base_dir, "memory_timeline.json")
MEMORY_TIMELINE_LOCK = asyncio.Lock()

logging.basicConfig(format="%(asctime)s - %(levelname)s - %(message)s", level=logging.INFO)
logger = logging.getLogger(__name__)

state = AppState(history_file=os.path.join(_base_dir, "chat_history.json"))
db = MemoryService(_base_dir)
weather_service = WeatherService(_base_dir, OPENWEATHER_API_KEY, MY_LAT, MY_LON)
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
            model="deepseek-chat",
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
        weather_info = await weather_service.get_weather_str()
        system_prompt = await prompt_service.get_system_prompt(user_id)
        system_content = f"{system_prompt}\n{weather_info}"
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

        messages = [{"role": "system", "content": system_content}]
        messages.extend(recent_history)
        messages.append({"role": "system", "content": anchor_prompt})
        messages.append({"role": "user", "content": merged_text})

        full_content = ""
        bot_message_id = str(uuid.uuid4())
        user_message_id = merged_request_id

        if has_images:
            if not GEMINI_API_KEY:
                await send_error(user_id, "GEMINI_NOT_CONFIGURED", "服务端未配置 Gemini Key", merged_request_id, request_ids)
                return
            prompt_text = merged_text or "请描述图片中的关键信息。"
            model = genai.GenerativeModel(GEMINI_MODEL)
            parts = [prompt_text]
            for img in merged_images[:VISION_MAX_IMAGE_COUNT]:
                parts.append(
                    {
                        "mime_type": img["mimeType"],
                        "data": img["raw"],
                    }
                )
            resp = await asyncio.to_thread(model.generate_content, parts)
            full_content = (resp.text or "").strip()
            await broadcast_json(
                user_id,
                {
                    "type": "chat.reply.stream",
                    "requestId": merged_request_id,
                    "payload": {"delta": full_content, "done": False, "contentType": "mixed", "modelProvider": "gemini"},
                },
            )
        else:
            response = await asyncio.wait_for(
                client.chat.completions.create(
                    model="deepseek-chat",
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
                            "payload": {"delta": delta, "done": False, "contentType": "text", "modelProvider": "deepseek"},
                        },
                    )

        _trace_text("A_RAW", user_id, full_content)
        cleaned_reply, timer_at, timer_text = parse_timer_instruction(full_content)
        _trace_text("B_PARSED", user_id, cleaned_reply)
        cleaned_reply = strip_polluted_tail(cleaned_reply, polluted_tails)
        _trace_text("C_STRIPPED", user_id, cleaned_reply)
        recent_assistant_replies = [m.get("content", "") for m in recent_history if m.get("role") == "assistant"]
        if not has_images and is_repetitive_reply(cleaned_reply, recent_assistant_replies, threshold=0.88):
            logger.info(f"[去重] 命中重复回复，触发重采样: user={user_id}")
            dedupe_messages = list(messages)
            dedupe_messages.append(
                {
                    "role": "system",
                    "content": "【去重重采样】：禁止复用最近10条回复的开头、句式和收尾，保持人设但换一种自然表达。",
                }
            )
            retry_resp = await client.chat.completions.create(
                model="deepseek-chat",
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
        _trace_text("D_FINAL", user_id, final_reply)

        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        merged_record = merged_text
        if has_images:
            merged_record = (merged_text + "\n\n" if merged_text else "") + f"[ImageCount={len(merged_images)}]"
        history.append({"role": "user", "content": merged_record})
        history.append({"role": "assistant", "content": final_reply})
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
                    "modelProvider": "gemini" if has_images else "deepseek",
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
            await event.wait()
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
        await websocket.close(code=4011, reason="Invalid token")
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
                await broadcast_json(
                    user_id,
                    {
                        "type": "chat.queued",
                        "requestId": request_id,
                        "payload": {"debounceWindowSec": calc_debounce_window(user_id)},
                    },
                )
                schedule_debounce(user_id)
                continue

            await send_error(user_id, "UNKNOWN_TYPE", f"未知的消息类型: {msg_type}", request_id)
    except WebSocketDisconnect:
        logger.info(f"[WS] 连接断开: user={user_id}, device={device_id}")
    except Exception as e:
        logger.exception(f"[WS] 连接异常: {e}")
    finally:
        ACTIVE_CONNECTIONS.get(user_id, set()).discard(websocket)
        if not ACTIVE_CONNECTIONS.get(user_id):
            worker = debounce_jobs.pop(user_id, None)
            if worker and not worker.done():
                worker.cancel()
            message_events.pop(user_id, None)
            last_message_at.pop(user_id, None)
            is_processing.pop(user_id, None)
            pending_flush.pop(user_id, None)


@app.get("/healthz")
async def healthz():
    return {"status": "ok", "service": "ws_api"}


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

    if scenario_type == "morning":
        selected_script = random.choice(MORNING_SCRIPTS)
        base_instruction = f"现在是北京时间 {current_time_str}。作为立希给用户发早安。"
    else:
        selected_script = random.choice(NIGHT_SCRIPTS)
        base_instruction = (
            f"现在是北京时间 {current_time_str} (深夜)。"
            "用户还没睡。作为立希，不要发\"晚安\"（因为发了晚安话题就结束了）。"
            "你要发一条消息确认他在干什么，或者吐槽他怎么还醒着，并表示你也还醒着，可以继续陪他。"
        )

    final_instruction = (
        f"{base_instruction}\n\n"
        f"本次随机到的灵感剧本：\n{selected_script}\n\n"
        f"【强制逻辑修正】：\n"
        f"1. 时间一致性：结合【当前北京时间 {current_time_str}】来生成台词。\n"
        f"2. 不要暴露你在扮演，直接进入角色说话。\n"
        f"3. 语气要符合剧本的情境，且如果情境是匆忙或困倦，句子要短、碎！"
    )

    try:
        system_prompt = await prompt_service.get_system_prompt(user_id)
        response = await client.chat.completions.create(
            model="deepseek-chat",
            messages=[
                {"role": "system", "content": f"{system_prompt}\n\n🎯 当前任务: {final_instruction}"}
            ],
            temperature=0.85,
        )
        raw_reply = response.choices[0].message.content or ""
        final_reply = inject_emojis(sanitize_taki_reply(raw_reply))

        greeting_id = f"greeting_{uuid.uuid4().hex}"
        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)

        # 写入历史
        history = state.user_chat_history.setdefault(user_id, [])
        history.append({"role": "assistant", "content": final_reply})
        if len(history) > 300:
            state.user_chat_history[user_id] = history[-300:]
        history_store.save(state.user_chat_history)

        await append_timeline(
            [{"messageId": greeting_id, "userId": user_id, "role": "bot", "content": final_reply, "timestamp": now_ms}]
        )

        # 推送给 app（与普通回复格式一致）
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
    logger.info("[启动] 定时问候调度器已启动")
    logger.info("[启动] 历史分隔调度器已启动")


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
