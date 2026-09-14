import asyncio
import os
import uuid
import logging
import json
from datetime import datetime, timezone, timedelta

import httpx
from dotenv import load_dotenv
from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.responses import JSONResponse
from openai import AsyncOpenAI
from pydantic import BaseModel

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
from time_utils import build_time_block

_base_dir = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_base_dir, ".env"))

DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY")
QWEATHER_API_KEY = os.getenv("QWEATHER_API_KEY")
MY_LAT = float(os.getenv("MY_LAT", "0"))
MY_LON = float(os.getenv("MY_LON", "0"))
BOT_HTTP_TOKEN = (os.getenv("BOT_HTTP_TOKEN", "") or "").strip()
BOT_WS_TOKEN = (os.getenv("BOT_WS_TOKEN", "") or "").strip()
APP_USER_ID = os.getenv("APP_USER_ID", "default-user").strip() or "default-user"
AUTH_USERNAME = (os.getenv("AUTH_USERNAME", "") or "").strip()
AUTH_USER_ID = (os.getenv("AUTH_USER_ID", "") or "").strip()
DEFAULT_USER_ID = AUTH_USER_ID or AUTH_USERNAME or APP_USER_ID
DEBUG_REPLY_TRACE = os.getenv("DEBUG_REPLY_TRACE", "0") == "1"
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

logging.basicConfig(format="%(asctime)s - %(levelname)s - %(message)s", level=logging.INFO)
logger = logging.getLogger(__name__)

state = AppState(history_file=os.path.join(_base_dir, "chat_history.json"))
db = MemoryService(_base_dir)
weather_service = WeatherService(_base_dir, QWEATHER_API_KEY, MY_LAT, MY_LON)
history_store = HistoryStore(state.history_file)
state.user_chat_history.update(history_store.load())

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

app = FastAPI(title="TakiShiina Bot HTTP API")
TIMELINE_FILE = os.path.join(_base_dir, "chat_timeline.json")
MEMORY_TIMELINE_FILE = os.path.join(_base_dir, "memory_timeline.json")
TIMELINE_LOCK = asyncio.Lock()
MEMORY_TIMELINE_LOCK = asyncio.Lock()

timeline_store = SecureJsonStore(TIMELINE_FILE, logger)
memory_timeline_store = SecureJsonStore(MEMORY_TIMELINE_FILE, logger)


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


def _trace_text(label: str, user_id: str, text: str):
    if not DEBUG_REPLY_TRACE:
        return
    compact = (text or "").replace("\n", "\\n")
    if len(compact) > 280:
        compact = compact[:280] + "...(truncated)"
    logger.info(f"[TRACE][{user_id}] {label}: {compact}")


def _load_timeline() -> list[dict]:
    """读取时间线。⚠️ **只在「文件不存在」时**返回空列表。

    文件存在但读不出来必须抛出：调用方是「load → extend → save 整份列表」，
    拿到 [] 之后一 save 就会把整份历史覆盖掉。

    注意 `SecureJsonStore.load()` 有**两条**吞异常的路径，必须一并挡住：
      ① 加密文件但 `DATA_ENC_KEY` 不匹配 → 抛 DataDecryptError；
      ② 文件被写坏 / 截断 / json 非法 → `load()` 内部 catch 后返回默认值**而不抛**。
    上一版只判了 ①（手工 exists 判断 + `load()`），因此 ② 仍会静默覆盖历史。
    `load_strict` 对两者都抛，是 append 路径唯一正确的读取方式。
    """
    return timeline_store.load_strict([])


async def append_timeline(items: list[dict]):
    """把消息写入 chat_timeline.json —— 客户端 GET /chat/history 读的就是这个文件。

    ⚠️ 必须与 ws_api.append_timeline 行为一致：用户项的 messageId 用客户端传来的
    requestId，客户端才能按 messageId 把本地那条 `error` 记录修复成 `sent`。
    早前 HTTP 兜底通道漏写此处，导致走 REST 发出的消息永远进不了历史，
    任何次数的同步都无法把它从「发送失败」修复回来。

    注：ws_api 与 http_api 是两个进程，这把锁只保证进程内互斥；
    SecureJsonStore 的原子写保证文件不会损坏，但两进程并发追加仍可能丢写。
    REST 是低频兜底通道，暂接受该残余风险。
    """
    async with TIMELINE_LOCK:
        try:
            data = _load_timeline()
        except Exception as e:
            # 读不出来就绝不写回：宁可这条消息进不了历史，也不能覆盖整份历史
            logger.error(f"读取时间线失败，放弃本次追加以避免覆盖历史: {e}")
            return
        data.extend(items)
        if len(data) > 3000:
            data = data[-3000:]
        try:
            timeline_store.save_strict(data)
        except Exception as e:
            logger.error(f"保存时间线失败: {e}")


def _load_memory_timeline() -> list[dict]:
    """同上：记忆时间线的严格读取（读失败必须让调用方感知）。"""
    return memory_timeline_store.load_strict([])


async def append_memory_timeline(items: list[dict]):
    async with MEMORY_TIMELINE_LOCK:
        try:
            data = _load_memory_timeline()
        except Exception as e:
            logger.error(f"读取记忆时间线失败，放弃本次追加以避免覆盖历史: {e}")
            return
        data.extend(items)
        if len(data) > 3000:
            data = data[-3000:]
        try:
            memory_timeline_store.save_strict(data)
        except Exception as e:
            logger.error(f"保存记忆时间线失败: {e}")


async def extract_user_facts(user_id: str, message: str):
    """异步提取用户消息中的事实并存入 user_memories.json"""
    logger.info(f"🔍 开始提取用户事实: user_id={user_id}, message={message[:50]}...")
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
        raw = resp.choices[0].message.content or "[]"
        logger.info(f"🔍 LLM返回: {raw[:200]}")
        raw = raw.strip()
        if raw.startswith("```"):
            raw = raw.split("\n", 1)[-1].rsplit("```", 1)[0].strip()
        facts = json.loads(raw)
        logger.info(f"🔍 解析到 {len(facts)} 条事实: {facts}")
        if isinstance(facts, list):
            timestamp = int(datetime.now(timezone.utc).timestamp() * 1000)
            memory_items = []
            for fact in facts:
                if isinstance(fact, str) and fact.strip():
                    clean_fact = fact.strip()
                    db.update_profile(user_id, clean_fact)
                    logger.info(f"✅ 已写入事实: {clean_fact}")
                    memory_items.append(
                        {
                            "factId": f"fact_{uuid.uuid4().hex}",
                            "userId": user_id,
                            "fact": clean_fact,
                            "timestamp": timestamp,
                        }
                    )
            if memory_items:
                await append_memory_timeline(memory_items)
    except json.JSONDecodeError as e:
        logger.warning(f"事实提取JSON解析失败: {e}, raw={raw[:200]}")
    except Exception as e:
        logger.error(f"事实提取异常: {e}")


class ChatRequest(BaseModel):
    requestId: str | None = None
    conversationId: str | None = None
    userId: str | None = None
    message: str
    stream: bool | None = False
    meta: dict | None = None
    traceId: str | None = None


class CityRequest(BaseModel):
    city: str


def response_body(code: int, message: str, data: dict | None, trace_id: str):
    return {"code": code, "message": message, "data": data, "traceId": trace_id}


def _require_http_auth(authorization: str | None, trace_id: str) -> AuthContext:
    auth = resolve_auth_context(
        token=authorization or "",
        jwt_secret=AUTH_JWT_SECRET,
        device_allowlist=DEVICE_ALLOWLIST,
        legacy_token_map=DEVICE_TOKEN_MAP,
        fallback_tokens=[BOT_HTTP_TOKEN, BOT_WS_TOKEN],
        default_user_id=DEFAULT_USER_ID,
    )
    if not auth:
        raise HTTPException(status_code=401, detail=response_body(40101, "鉴权失败", None, trace_id))
    return auth


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
        return 8.0
    seconds_since_response = (datetime.now(timezone.utc) - bot_last).total_seconds()
    return 20.0 if seconds_since_response < 40.0 else 8.0


@app.middleware("http")
async def trace_middleware(request: Request, call_next):
    trace_id = request.headers.get("x-trace-id") or f"trace_{uuid.uuid4().hex}"
    request.state.trace_id = trace_id
    response = await call_next(request)
    response.headers["x-trace-id"] = trace_id
    return response


@app.get("/healthz")
async def healthz(request: Request):
    trace_id = request.state.trace_id
    return response_body(0, "ok", {"status": "up"}, trace_id)


@app.get("/api/v1/chat/history")
async def chat_history(
    request: Request,
    since: int = 0,
    limit: int = 200,
    authorization: str | None = Header(default=None),
):
    trace_id = request.state.trace_id
    auth = _require_http_auth(authorization, trace_id)

    try:
        items = _load_timeline()
        filtered = [
            x
            for x in items
            if int(x.get("timestamp", 0)) > since and str(x.get("userId", "")) == auth.user_id
        ]
        filtered = filtered[-max(1, min(limit, 500)) :]
        return response_body(0, "ok", {"items": filtered}, trace_id)
    except Exception as error:
        logger.exception("history failed")
        return JSONResponse(
            status_code=500,
            content=response_body(5000, f"系统异常: {error}", None, trace_id),
        )


@app.get("/api/v1/memory/facts")
async def memory_facts(
    request: Request,
    since: int = 0,
    limit: int = 200,
    userId: str | None = None,
    authorization: str | None = Header(default=None),
):
    trace_id = request.state.trace_id
    auth = _require_http_auth(authorization, trace_id)

    try:
        items = _load_memory_timeline()
        target_user = auth.user_id
        filtered = [
            x for x in items
            if int(x.get("timestamp", 0)) > since and str(x.get("userId", "")) == target_user
        ]
        filtered = filtered[-max(1, min(limit, 500)) :]
        return response_body(0, "ok", {"items": filtered}, trace_id)
    except Exception as error:
        logger.exception("memory facts failed")
        return JSONResponse(
            status_code=500,
            content=response_body(5000, f"系统异常: {error}", None, trace_id),
        )


@app.get("/api/v1/settings/city")
async def get_city(
    request: Request,
    authorization: str | None = Header(default=None),
):
    trace_id = request.state.trace_id
    _require_http_auth(authorization, trace_id)

    try:
        city = weather_service.get_current_city()
        return response_body(0, "ok", {"city": city}, trace_id)
    except Exception as error:
        logger.exception("get city failed")
        return JSONResponse(
            status_code=500,
            content=response_body(5000, f"系统异常: {error}", None, trace_id),
        )


@app.put("/api/v1/settings/city")
async def set_city(
    payload: CityRequest,
    request: Request,
    authorization: str | None = Header(default=None),
):
    trace_id = request.state.trace_id
    _require_http_auth(authorization, trace_id)

    city = (payload.city or "").strip()
    if not city:
        return JSONResponse(
            status_code=400,
            content=response_body(40002, "city 不能为空", None, trace_id),
        )

    try:
        weather_service.set_city(city)
        return response_body(0, "ok", {"city": city}, trace_id)
    except Exception as error:
        logger.exception("set city failed")
        return JSONResponse(
            status_code=500,
            content=response_body(5000, f"系统异常: {error}", None, trace_id),
        )


@app.post("/api/v1/chat")
async def chat(
    payload: ChatRequest,
    request: Request,
    authorization: str | None = Header(default=None),
):
    trace_id = payload.traceId or request.state.trace_id

    auth = _require_http_auth(authorization, trace_id)

    if not payload.message or not payload.message.strip():
        return JSONResponse(
            status_code=400,
            content=response_body(40001, "message 不能为空", None, trace_id),
        )

    user_id = auth.user_id
    message_text = payload.message.strip()

    try:
        history = state.user_chat_history.setdefault(user_id, [])
        weather_info = await weather_service.get_weather_str(force=True)

        utc_now = datetime.now(timezone.utc)
        beijing_now = utc_now + timedelta(hours=8)
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
            if keyword in message_text:
                emotional_prompt = f"\n\n💝 {instruction}"
                break

        trigger_prompt = ""
        for keyword, (reaction, _intensity) in LORE_TRIGGERS.items():
            if keyword.lower() in message_text.lower():
                trigger_prompt = f"\n\n🛑【突发状态】：{reaction}"
                break

        anchor_prompt = (
            "【强制提醒】：保持“酷但笨拙”人设；禁止动作叙事；回复长短跟随内容。"
            "\n【去重要求】：避免复用最近10次回复的开头词、句式骨架和结尾口头禅。"
            "\n【禁止时间戳】：历史消息中的【时间】标记仅供你理解时间线，绝对禁止在回复中输出时间戳或时刻标记（如 [8-22 17:22]、【14:32】）。"
            f"{emotional_prompt}{trigger_prompt}"
        )

        short_history = history[-20:]
        short_history, polluted_tails = clean_short_term_history(short_history, min_repeat=3)
        if DEBUG_REPLY_TRACE:
            recent_assistant_count = sum(1 for m in short_history if m.get("role") == "assistant")
            logger.info(
                f"[TRACE][{user_id}] SHORT_HISTORY size={len(short_history)} assistant={recent_assistant_count} tails={len(polluted_tails)}"
            )

        messages = [{"role": "system", "content": system_content}]
        messages.extend(with_history_timestamp(m) for m in short_history)
        messages.append({"role": "system", "content": anchor_prompt})
        messages.append({"role": "user", "content": message_text})

        response = await client.chat.completions.create(
            model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
            messages=messages,
            temperature=0.75,
        )
        raw_reply = response.choices[0].message.content or ""
        _trace_text("A_RAW", user_id, raw_reply)
        cleaned_reply, timer_at, timer_text = parse_timer_instruction(raw_reply)
        _trace_text("B_PARSED", user_id, cleaned_reply)
        cleaned_reply = strip_polluted_tail(cleaned_reply, polluted_tails)
        _trace_text("C_STRIPPED", user_id, cleaned_reply)
        recent_assistant_replies = [m.get("content", "") for m in short_history if m.get("role") == "assistant"]
        if is_repetitive_reply(cleaned_reply, recent_assistant_replies, threshold=0.88):
            logger.info(f"[去重] HTTP命中重复回复，触发重采样: user={user_id}")
            dedupe_messages = list(messages)
            dedupe_messages.append(
                {
                    "role": "system",
                    "content": "【去重重采样】：禁止复用最近10条回复的开头、句式和收尾，保持人设但换一种自然表达。",
                }
            )
            retry_response = await client.chat.completions.create(
                model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
                messages=dedupe_messages,
                temperature=1.0,
            )
            retry_raw = retry_response.choices[0].message.content or cleaned_reply
            _trace_text("A_RETRY_RAW", user_id, retry_raw)
            cleaned_retry, timer_at_retry, timer_text_retry = parse_timer_instruction(retry_raw)
            cleaned_reply = strip_polluted_tail(cleaned_retry or cleaned_reply, polluted_tails)
            _trace_text("C_RETRY_STRIPPED", user_id, cleaned_reply)
            if timer_at_retry and timer_text_retry:
                timer_at, timer_text = timer_at_retry, timer_text_retry

        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        history.append({"role": "user", "content": message_text, "ts": now_ms - 1})
        history.append({"role": "assistant", "content": cleaned_reply, "ts": now_ms})
        if len(history) > 300:
            state.user_chat_history[user_id] = history[-300:]
        history_store.save(state.user_chat_history)

        state.last_activity[user_id] = datetime.now(timezone.utc)
        state.last_bot_response_time[user_id] = datetime.now(timezone.utc)

        asyncio.create_task(extract_user_facts(user_id, message_text))

        final_reply = inject_emojis(sanitize_taki_reply(cleaned_reply))
        _trace_text("D_FINAL", user_id, final_reply)

        # 写入时间线，客户端才能通过 GET /chat/history 把本地状态修复为 sent。
        # 用户项的 messageId 必须是客户端的 requestId（与 ws_api 一致）。
        user_message_id = payload.requestId or f"http_{uuid.uuid4().hex}"
        # ⚠️ Bot 回复的 messageId 必须**回给客户端**（见下方 data["messageId"]）：
        #    客户端本地插入 bot 气泡时用的是这个 id，两边主键口径必须一致，
        #    否则下一次 GET /chat/history 会因为 `delivered_bot_messages` 里没有这个 id
        #    而把同一条回复再插一遍 —— 界面出现重复气泡，且是永久性的。
        bot_message_id = str(uuid.uuid4())
        await append_timeline(
            [
                {
                    "messageId": user_message_id,
                    "userId": user_id,
                    "role": "user",
                    "content": message_text,
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

        usage = response.usage
        usage_data = {
            "promptTokens": getattr(usage, "prompt_tokens", 0),
            "completionTokens": getattr(usage, "completion_tokens", 0),
            "totalTokens": getattr(usage, "total_tokens", 0),
        }
        data = {
            "conversationId": payload.conversationId or f"conv_{user_id}",
            "reply": final_reply,
            # 与时间线行同源的 bot messageId（客户端据此落库，避免同步时重复插入）
            "messageId": bot_message_id,
            "model": os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
            "usage": usage_data,
            "debounceWindowSec": calc_debounce_window(user_id),
        }
        if timer_at and timer_text:
            beijing_now = datetime.now(timezone.utc) + timedelta(hours=8)
            data["timerInstruction"] = {"target": timer_at, "text": timer_text, "at": beijing_now.isoformat()}

        return JSONResponse(status_code=200, content=response_body(0, "ok", data, trace_id))
    except HTTPException:
        raise
    except Exception as error:
        logger.exception("chat failed")
        return JSONResponse(
            status_code=500,
            content=response_body(5000, f"系统异常: {error}", None, trace_id),
        )


if __name__ == "__main__":
    import uvicorn

    host = os.getenv("BOT_HTTP_HOST", "0.0.0.0")
    port = int(os.getenv("BOT_HTTP_PORT", "8000"))
    uvicorn.run("http_api:app", host=host, port=port, reload=False)
