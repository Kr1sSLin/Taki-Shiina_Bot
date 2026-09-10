"""
聊天防抖缓冲的「摘取」工具（B 方案：文字 + 礼物合并成一条回复）。

背景：聊天链路有防抖静默窗口（8 秒，距上次回复 <40 秒则 20 秒），窗口内用户连发的消息会被
合并成一整轮再回复；而互动礼物是立即执行的。当用户在窗口内又点了礼物时，会出现"礼物先回一条、
文字再回一条"。本模块负责：**等窗口结束 → 把窗口内的用户消息摘走**，交给礼物回复一起回答，
使整轮只产生一条回复。

摘取后必须把被摘消息的 `requestId` 一并回给客户端（客户端据此把气泡标记为"已发送"），
否则那些气泡会永远停在"发送中"。

抽成独立模块是为了可注入时钟、可单测（见 tests/test_debounce_merge.py）。
"""

from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from typing import Awaitable, Callable


async def collect_pending_after_quiet_window(
    user_id: str,
    *,
    buffer: dict[str, list[dict]],
    last_message_at: dict[str, datetime],
    window_seconds: Callable[[str], float],
    max_wait_seconds: float = 25.0,
    poll_interval: float = 0.5,
    now: Callable[[], datetime] | None = None,
    sleep: Callable[[float], Awaitable[None]] | None = None,
    logger=None,
) -> list[dict]:
    """等静默窗口结束，然后摘走该用户缓冲中的消息。

    - 缓冲为空：立即返回 `[]`（常见路径，礼物不额外等待）；
    - 窗口内用户又发新消息：静默窗口会重置，这里继续等，直到窗口真正结束；
    - 等待超过 [max_wait_seconds]：不再等，把当前已收到的消息摘走（避免无限等待）；
    - 等待期间聊天 worker 已经把缓冲取走：返回 `[]`。
    """
    if not buffer.get(user_id):
        return []

    now = now or (lambda: datetime.now(timezone.utc))
    sleep = sleep or asyncio.sleep
    waited = 0.0

    while True:
        buffered = buffer.get(user_id)
        if not buffered:
            return []
        last_at = last_message_at.get(user_id)
        if not last_at:
            break
        window = float(window_seconds(user_id))
        elapsed = (now() - last_at).total_seconds()
        remaining = window - elapsed
        if remaining <= 0:
            break
        if waited + remaining > max_wait_seconds:
            if logger:
                logger.info(
                    f"[防抖合并] 等待窗口超过上限 {max_wait_seconds:.0f}s，先合并已收到消息: user={user_id}"
                )
            break
        step = min(remaining, poll_interval)
        await sleep(step)
        waited += step

    return buffer.pop(user_id, []) or []


def merge_buffered_texts(items: list[dict], limit: int = 200) -> str:
    """把摘到的缓冲消息拼成一段附言文本。"""
    merged = " ".join(
        str(item.get("content") or "").strip() for item in items if str(item.get("content") or "").strip()
    ).strip()
    return merged[-limit:].strip()


def buffered_request_ids(items: list[dict]) -> list[str]:
    """摘到的消息对应的 requestId（客户端据此把气泡标记为已发送）。"""
    return [str(item["requestId"]) for item in items if item.get("requestId")]
