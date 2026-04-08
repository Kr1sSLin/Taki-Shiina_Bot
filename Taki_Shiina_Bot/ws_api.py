"""
WebSocket API for TakiShiina Bot
实现防抖合并、断线继续处理、在线实时推送
"""

import asyncio
import json
import logging
import os
import uuid
from datetime import datetime, timezone

import httpx
from dotenv import load_dotenv
from fastapi import FastAPI, Query, WebSocket, WebSocketDisconnect
from openai import AsyncOpenAI

from app_constants import EMOTIONAL_TRIGGERS, LORE_TRIGGERS, USER_MEMO
from app_state import AppState
from services.history_store import HistoryStore
from services.memory_service import MemoryService
from services.prompt_service import PromptService
from services.weather_service import WeatherService
from text_utils import inject_emojis, sanitize_taki_reply

_base_dir = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_base_dir, ".env"))

DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY")
OPENWEATHER_API_KEY = os.getenv("OPENWEATHER_API_KEY")
MY_LAT = float(os.getenv("MY_LAT", "0"))
MY_LON = float(os.getenv("MY_LON", "0"))
BOT_WS_TOKEN = os.getenv("BOT_WS_TOKEN", "")

DEBOUNCE_BASE = 8.0
DEBOUNCE_EXTENDED = 20.0
DEBOUNCE_EXTEND_WINDOW = 40.0

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

app = FastAPI(title="TakiShiina Bot WebSocket API")

ACTIVE_CONNECTIONS: dict[str, set[WebSocket]] = {}
message_buffer: dict[str, list[dict]] = {}
debounce_jobs: dict[str, asyncio.Task] = {}


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
    if not os.path.exists(TIMELINE_FILE):
        return []
    try:
        with open(TIMELINE_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as e:
        logger.error(f"[WS] 读取时间线失败: {e}")
        return []


def _load_memory_timeline() -> list[dict]:
    if not os.path.exists(MEMORY_TIMELINE_FILE):
        return []
    try:
        with open(MEMORY_TIMELINE_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as e:
        logger.error(f"[WS] 读取记忆时间线失败: {e}")
        return []


async def append_timeline(items: list[dict]):
    async with TIMELINE_LOCK:
        data = _load_timeline()
        data.extend(items)
        if len(data) > 3000:
            data = data[-3000:]
        with open(TIMELINE_FILE, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)


async def append_memory_timeline(items: list[dict]):
    async with MEMORY_TIMELINE_LOCK:
        data = _load_memory_timeline()
        data.extend(items)
        if len(data) > 3000:
            data = data[-3000:]
        with open(MEMORY_TIMELINE_FILE, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)


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
    buffered = message_buffer.get(user_id, [])
    if not buffered:
        return
    message_buffer[user_id] = []

    request_ids = [m["requestId"] for m in buffered]
    merged_text = "\n".join(m["content"] for m in buffered if m["content"])
    merged_request_id = request_ids[-1] if request_ids else str(uuid.uuid4())

    try:
        await broadcast_json(user_id, {"type": "chat.typing", "payload": {"typing": True}})

        history = state.user_chat_history.setdefault(user_id, [])
        weather_info = await weather_service.get_weather_str()
        system_content = f"{await prompt_service.get_system_prompt(user_id)}\n{weather_info}"

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
            f"{emotional_prompt}{trigger_prompt}"
        )

        messages = [{"role": "system", "content": system_content}]
        messages.extend(history[-20:])
        messages.append({"role": "system", "content": anchor_prompt})
        messages.append({"role": "user", "content": merged_text})

        full_content = ""
        bot_message_id = str(uuid.uuid4())
        user_message_id = merged_request_id

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
                        "payload": {"delta": delta, "done": False},
                    },
                )

        cleaned_reply, timer_at, timer_text = parse_timer_instruction(full_content)
        final_reply = inject_emojis(sanitize_taki_reply(cleaned_reply))

        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        history.append({"role": "user", "content": merged_text})
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
                    "content": merged_text,
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


def schedule_debounce(user_id: str):
    window = calc_debounce_window(user_id)
    old_job = debounce_jobs.get(user_id)
    if old_job and not old_job.done():
        old_job.cancel()
        logger.info(f"[防抖] 重置计时器: user={user_id}, window={window}s")
    else:
        logger.info(f"[防抖] 启动计时器: user={user_id}, window={window}s")

    async def _debounce_worker():
        try:
            await asyncio.sleep(window)
            buffered_count = len(message_buffer.get(user_id, []))
            logger.info(f"[防抖] 计时结束，处理 {buffered_count} 条消息: user={user_id}")
            await process_buffered_messages(user_id)
        except asyncio.CancelledError:
            logger.info(f"[防抖] 计时器被取消: user={user_id}")
            return

    debounce_jobs[user_id] = asyncio.create_task(_debounce_worker())


@app.websocket("/ws/chat")
async def websocket_chat(websocket: WebSocket, token: str = Query(default="")):
    if BOT_WS_TOKEN and token != BOT_WS_TOKEN:
        await websocket.close(code=4011, reason="Invalid token")
        logger.warning("[WS] 连接被拒绝: token 无效")
        return

    await websocket.accept()
    user_id = "default-user"
    ACTIVE_CONNECTIONS.setdefault(user_id, set()).add(websocket)
    logger.info(f"[WS] 连接建立: user={user_id}")

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
                logger.info(f"[WS] 收到消息: user={user_id}, content={content[:50]}...")
                if not content:
                    await send_error(user_id, "EMPTY_MESSAGE", "消息内容不能为空", request_id)
                    continue
                message_buffer.setdefault(user_id, []).append({"requestId": request_id, "content": content})
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
        logger.info(f"[WS] 连接断开: user={user_id}")
    except Exception as e:
        logger.exception(f"[WS] 连接异常: {e}")
    finally:
        ACTIVE_CONNECTIONS.get(user_id, set()).discard(websocket)


@app.get("/healthz")
async def healthz():
    return {"status": "ok", "service": "ws_api"}


if __name__ == "__main__":
    import uvicorn

    host = os.getenv("BOT_WS_HOST", "0.0.0.0")
    port = int(os.getenv("BOT_WS_PORT", "8001"))
    uvicorn.run("ws_api:app", host=host, port=port, reload=False)

