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
BOT_HTTP_TOKEN = os.getenv("BOT_HTTP_TOKEN", "")

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

app = FastAPI(title="TakiShiina Bot HTTP API")
TIMELINE_FILE = os.path.join(_base_dir, "chat_timeline.json")


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
            model="deepseek-chat",
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
            for fact in facts:
                if isinstance(fact, str) and fact.strip():
                    db.update_profile(user_id, fact.strip())
                    logger.info(f"✅ 已写入事实: {fact.strip()}")
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
    if BOT_HTTP_TOKEN:
        expected = f"Bearer {BOT_HTTP_TOKEN}"
        if authorization != expected:
            raise HTTPException(status_code=401, detail=response_body(40101, "鉴权失败", None, trace_id))

    try:
        if not os.path.exists(TIMELINE_FILE):
            return response_body(0, "ok", {"items": []}, trace_id)
        with open(TIMELINE_FILE, "r", encoding="utf-8") as f:
            items = json.load(f)
        filtered = [x for x in items if int(x.get("timestamp", 0)) > since]
        filtered = filtered[-max(1, min(limit, 500)) :]
        return response_body(0, "ok", {"items": filtered}, trace_id)
    except Exception as error:
        logger.exception("history failed")
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
    if BOT_HTTP_TOKEN:
        expected = f"Bearer {BOT_HTTP_TOKEN}"
        if authorization != expected:
            raise HTTPException(status_code=401, detail=response_body(40101, "鉴权失败", None, trace_id))

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
    if BOT_HTTP_TOKEN:
        expected = f"Bearer {BOT_HTTP_TOKEN}"
        if authorization != expected:
            raise HTTPException(status_code=401, detail=response_body(40101, "鉴权失败", None, trace_id))

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

    if BOT_HTTP_TOKEN:
        expected = f"Bearer {BOT_HTTP_TOKEN}"
        if authorization != expected:
            raise HTTPException(status_code=401, detail=response_body(40101, "鉴权失败", None, trace_id))

    if not payload.message or not payload.message.strip():
        return JSONResponse(
            status_code=400,
            content=response_body(40001, "message 不能为空", None, trace_id),
        )

    user_id = str(payload.userId or payload.conversationId or "default-user")
    message_text = payload.message.strip()

    try:
        history = state.user_chat_history.setdefault(user_id, [])
        weather_info = await weather_service.get_weather_str()
        system_content = f"{await prompt_service.get_system_prompt(user_id)}\n{weather_info}"

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
            f"{emotional_prompt}{trigger_prompt}"
        )

        messages = [{"role": "system", "content": system_content}]
        messages.extend(history[-20:])
        messages.append({"role": "system", "content": anchor_prompt})
        messages.append({"role": "user", "content": message_text})

        response = await client.chat.completions.create(
            model="deepseek-chat",
            messages=messages,
            temperature=0.75,
        )
        raw_reply = response.choices[0].message.content or ""
        cleaned_reply, timer_at, timer_text = parse_timer_instruction(raw_reply)

        history.append({"role": "user", "content": message_text})
        history.append({"role": "assistant", "content": cleaned_reply})
        if len(history) > 300:
            state.user_chat_history[user_id] = history[-300:]
        history_store.save(state.user_chat_history)

        state.last_activity[user_id] = datetime.now(timezone.utc)
        state.last_bot_response_time[user_id] = datetime.now(timezone.utc)

        asyncio.create_task(extract_user_facts(user_id, message_text))

        final_reply = inject_emojis(sanitize_taki_reply(cleaned_reply))
        usage = response.usage
        usage_data = {
            "promptTokens": getattr(usage, "prompt_tokens", 0),
            "completionTokens": getattr(usage, "completion_tokens", 0),
            "totalTokens": getattr(usage, "total_tokens", 0),
        }
        data = {
            "conversationId": payload.conversationId or f"conv_{user_id}",
            "reply": final_reply,
            "model": "deepseek-chat",
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
