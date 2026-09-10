"""B 方案（文字 + 礼物合并）里「等静默窗口并摘取缓冲」的逻辑单测。

注入假时钟/假 sleep，完全确定性、不真的等待。
"""

from __future__ import annotations

import asyncio
import unittest
from datetime import datetime, timedelta, timezone

from gamification_fixture import setup_crypto_env  # noqa: F401  (夹具已处理 sys.path)
from services.debounce_merge import (
    buffered_request_ids,
    collect_pending_after_quiet_window,
    merge_buffered_texts,
)

USER = "u1"


class _FakeClock:
    def __init__(self, start: datetime):
        self.current = start
        self.slept: list[float] = []

    def now(self) -> datetime:
        return self.current

    async def sleep(self, seconds: float) -> None:
        self.slept.append(seconds)
        self.current += timedelta(seconds=seconds)

    @property
    def total_slept(self) -> float:
        return sum(self.slept)


class CollectPendingTestCase(unittest.TestCase):
    def setUp(self):
        setup_crypto_env()
        self.clock = _FakeClock(datetime(2026, 9, 10, 12, 0, 0, tzinfo=timezone.utc))

    def _run(self, **kwargs):
        return asyncio.run(
            collect_pending_after_quiet_window(
                USER,
                buffer=kwargs.pop("buffer"),
                last_message_at=kwargs.pop("last_message_at"),
                window_seconds=kwargs.pop("window_seconds", lambda _uid: 8.0),
                now=self.clock.now,
                sleep=self.clock.sleep,
                **kwargs,
            )
        )

    def test_empty_buffer_returns_immediately(self):
        buffer: dict = {USER: []}
        items = self._run(buffer=buffer, last_message_at={USER: self.clock.now()})
        self.assertEqual(items, [])
        self.assertEqual(self.clock.slept, [], "没有待回复消息时不应等待")

    def test_missing_buffer_entry_returns_immediately(self):
        items = self._run(buffer={}, last_message_at={USER: self.clock.now()})
        self.assertEqual(items, [])
        self.assertEqual(self.clock.slept, [])

    def test_waits_until_quiet_window_closes_then_takes_messages(self):
        buffer = {USER: [{"requestId": "c1", "content": "今天好累啊"}]}
        last_message_at = {USER: self.clock.now() - timedelta(seconds=2)}
        items = self._run(buffer=buffer, last_message_at=last_message_at)

        self.assertEqual(len(items), 1)
        self.assertAlmostEqual(self.clock.total_slept, 6.0, places=3, msg="应补足剩余的 6 秒静默窗口")
        # 摘取后缓冲被 pop 掉，聊天 worker 拿不到这批消息 → 不会再回第二条
        self.assertNotIn(USER, buffer)

    def test_no_buffer_left_when_worker_already_took_it(self):
        buffer = {USER: [{"requestId": "c1", "content": "在吗"}]}
        last_message_at = {USER: self.clock.now()}

        async def sleep_and_drain(seconds: float):
            buffer[USER] = []          # 模拟聊天 worker 抢先取走
            await self.clock.sleep(seconds)

        items = asyncio.run(
            collect_pending_after_quiet_window(
                USER,
                buffer=buffer,
                last_message_at=last_message_at,
                window_seconds=lambda _uid: 8.0,
                now=self.clock.now,
                sleep=sleep_and_drain,
            )
        )
        self.assertEqual(items, [])

    def test_stops_at_max_wait_when_window_keeps_resetting(self):
        buffer = {USER: [{"requestId": "c1", "content": "一"}]}
        # 窗口 20 秒 > 上限 5 秒：不应无限等下去
        items = self._run(
            buffer=buffer,
            last_message_at={USER: self.clock.now()},
            window_seconds=lambda _uid: 20.0,
            max_wait_seconds=5.0,
        )
        self.assertEqual(len(items), 1)
        self.assertEqual(self.clock.slept, [], "超过上限时直接合并已收到消息，不再等待")

    def test_returns_immediately_without_last_message_marker(self):
        buffer = {USER: [{"requestId": "c1", "content": "嗨"}]}
        items = self._run(buffer=buffer, last_message_at={})
        self.assertEqual(len(items), 1)
        self.assertEqual(self.clock.slept, [])

    def test_helpers_merge_texts_and_request_ids(self):
        items = [
            {"requestId": "c1", "content": " 今天好累啊 "},
            {"requestId": "c2", "content": "陪我一会儿"},
            {"requestId": "c3", "content": "   "},
        ]
        self.assertEqual(merge_buffered_texts(items), "今天好累啊 陪我一会儿")
        self.assertEqual(buffered_request_ids(items), ["c1", "c2", "c3"])


if __name__ == "__main__":
    unittest.main()
