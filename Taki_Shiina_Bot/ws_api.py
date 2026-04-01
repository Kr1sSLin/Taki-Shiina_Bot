"""
WebSocket API for TakiShiina Bot
实现实时双向通信和流式 AI 回复
"""

import asyncio
import json
import logging
import os
import uuid
from datetime import datetime, timedelta, timezone

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


def parse_timer_instruction(raw_reply: str):
    """解析回复中的定时器指令"""
    import re
    timer_pattern = r"\[\[TIMER:(\d{1,2}:\d{2})\|(.*?)\]\]"
    match = re.search(timer_pattern, raw_reply or "")
    if not match:
        return raw_reply, None, None
    target_time_str = match.group(1)
    reminder_text = match.group(2)
    cleaned_reply = re.sub(timer_pattern, "", raw_reply).strip()
    return cleaned_reply, target_time_str, reminder_text


async def send_json(websocket: WebSocket, data: dict):
    """发送 JSON 消息"""
    await websocket.send_text(json.dumps(data, ensure_ascii=False))


async def send_error(websocket: WebSocket, error_code: str, message: str, request_id: str | None = None):
    """发送错误消息"""
    await send_json(websocket, {
        "type": "bot.error",
        "requestId": request_id,
        "payload": {
            "errorCode": error_code,
            "message": message,
            "timestamp": int(datetime.now(timezone.utc).timestamp() * 1000)
        }
    })


async def send_typing(websocket: WebSocket, typing: bool = True):
    """发送打字状态"""
    await send_json(websocket, {
        "type": "chat.typing",
        "payload": {"typing": typing}
    })


async def handle_chat_message(websocket: WebSocket, request_id: str, payload: dict, user_id: str):
    """处理聊天消息并流式返回回复"""
    message_text = payload.get("content", "").strip()
    if not message_text:
        await send_error(websocket, "EMPTY_MESSAGE", "消息内容不能为空", request_id)
        return

    try:
        # 发送打字状态
        await send_typing(websocket, True)

        # 构建消息上下文
        history = state.user_chat_history.setdefault(user_id, [])
        weather_info = await weather_service.get_weather_str()
        system_content = f"{await prompt_service.get_system_prompt(user_id)}\n{weather_info}"

        # 情感触发器
        emotional_prompt = ""
        for keyword, instruction in EMOTIONAL_TRIGGERS.items():
            if keyword in message_text:
                emotional_prompt = f"\n\n💝 {instruction}"
                break

        # 剧情触发器
        trigger_prompt = ""
        for keyword, (reaction, _intensity) in LORE_TRIGGERS.items():
            if keyword.lower() in message_text.lower():
                trigger_prompt = f"\n\n🛑【突发状态】：{reaction}"
                break

        anchor_prompt = (
            "【强制提醒】：保持"酷但笨拙"人设；禁止动作叙事；回复长短跟随内容。"
            f"{emotional_prompt}{trigger_prompt}"
        )

        messages = [{"role": "system", "content": system_content}]
        messages.extend(history[-20:])
        messages.append({"role": "system", "content": anchor_prompt})
        messages.append({"role": "user", "content": message_text})

        # 流式调用 DeepSeek API
        full_content = ""
        bot_message_id = str(uuid.uuid4())

        try:
            response = await asyncio.wait_for(
                client.chat.completions.create(
                    model="deepseek-chat",
                    messages=messages,
                    temperature=0.75,
                    stream=True
                ),
                timeout=90.0  # 90 秒总超时
            )

            async for chunk in response:
                delta = chunk.choices[0].delta.content or ""
                if delta:
                    full_content += delta
                    await send_json(websocket, {
                        "type": "chat.reply.stream",
                        "requestId": request_id,
                        "payload": {
                            "delta": delta,
                            "done": False
                        }
                    })

        except asyncio.TimeoutError:
            await send_typing(websocket, False)
            await send_error(websocket, "AI_TIMEOUT", "AI 响应超时，请重试", request_id)
            return

        # 流式完成
        await send_typing(websocket, False)

        # 处理回复内容
        cleaned_reply, timer_at, timer_text = parse_timer_instruction(full_content)
        final_reply = inject_emojis(sanitize_taki_reply(cleaned_reply))

        # 发送完成消息
        await send_json(websocket, {
            "type": "chat.reply.stream",
            "requestId": request_id,
            "payload": {
                "delta": "",
                "done": True,
                "messageId": bot_message_id,
                "finalContent": final_reply,
                "timerInstruction": {
                    "target": timer_at,
                    "text": timer_text
                } if timer_at else None
            }
        })

        # 更新聊天历史
        history.append({"role": "user", "content": message_text})
        history.append({"role": "assistant", "content": final_reply})
        if len(history) > 300:
            state.user_chat_history[user_id] = history[-300:]
        history_store.save(state.user_chat_history)

        # 更新活动时间
        state.last_activity[user_id] = datetime.now(timezone.utc)
        state.last_bot_response_time[user_id] = datetime.now(timezone.utc)

        logger.info(f"[WS] 回复完成: user={user_id}, request={request_id}, len={len(final_reply)}")

    except Exception as e:
        logger.exception(f"[WS] 处理消息失败: {e}")
        await send_typing(websocket, False)
        await send_error(websocket, "INTERNAL_ERROR", f"处理失败: {str(e)}", request_id)


@app.websocket("/ws/chat")
async def websocket_chat(websocket: WebSocket, token: str = Query(default="")):
    """WebSocket 聊天端点"""
    # Token 验证
    if BOT_WS_TOKEN and token != BOT_WS_TOKEN:
        await websocket.close(code=4011, reason="Invalid token")
        logger.warning(f"[WS] 连接被拒绝: token 无效")
        return

    await websocket.accept()
    user_id = "default-user"  # 单用户场景
    logger.info(f"[WS] 连接建立: user={user_id}")

    try:
        while True:
            # 接收消息
            raw_message = await websocket.receive_text()

            try:
                message = json.loads(raw_message)
            except json.JSONDecodeError:
                await send_error(websocket, "INVALID_JSON", "无效的 JSON 格式")
                continue

            msg_type = message.get("type", "")
            request_id = message.get("requestId")

            # 心跳处理
            if msg_type == "ping":
                await send_json(websocket, {
                    "type": "pong",
                    "timestamp": int(datetime.now(timezone.utc).timestamp() * 1000)
                })
                continue

            # 聊天消息处理
            if msg_type == "chat.message":
                payload = message.get("payload", {})
                await handle_chat_message(websocket, request_id, payload, user_id)
                continue

            # 未知消息类型
            await send_error(websocket, "UNKNOWN_TYPE", f"未知的消息类型: {msg_type}", request_id)

    except WebSocketDisconnect:
        logger.info(f"[WS] 连接断开: user={user_id}")
    except Exception as e:
        logger.exception(f"[WS] 连接异常: {e}")


@app.get("/healthz")
async def healthz():
    """健康检查"""
    return {"status": "ok", "service": "ws_api"}


if __name__ == "__main__":
    import uvicorn

    host = os.getenv("BOT_WS_HOST", "0.0.0.0")
    port = int(os.getenv("BOT_WS_PORT", "8001"))
    uvicorn.run("ws_api:app", host=host, port=port, reload=False)
