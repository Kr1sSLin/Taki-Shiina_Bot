"""心情（立希当前状态）缓存与整点刷新单测。

覆盖 S1/E5：
- 首次生成后写入缓存，`expires_at` 指向下一个整点；
- 缓存过期时**先返回旧心情**（stale-while-revalidate），后台异步重建，绝不在请求链路上等 LLM；
- `refresh_scene` 供整点任务 / 内部接口复用，失败保留旧值。
"""

from __future__ import annotations

import asyncio
import logging
import unittest
from datetime import datetime, timedelta, timezone
from types import SimpleNamespace

from gamification_fixture import setup_crypto_env  # noqa: F401  (夹具已处理 sys.path)
from services.prompt_service import PromptService


class _SceneClient:
    """每次调用返回不同的「当前状态」，用于验证是否真的重建了。"""

    def __init__(self):
        self.calls = 0
        self.fail_next = False

    async def _create(self, **_kwargs):
        self.calls += 1
        if self.fail_next:
            self.fail_next = False
            raise RuntimeError("boom")
        text = (
            f"【立希当前状态】：\n- 情绪底色：第 {self.calls} 次生成的情绪。\n"
            f"- 正在做：第 {self.calls} 次生成的动作。"
        )
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=text))]
        )

    @property
    def chat(self):
        return SimpleNamespace(completions=SimpleNamespace(create=self._create))


class _FakeMemory:
    def get_profile(self, _user_id):
        return "新朋友"


class SceneCacheTestCase(unittest.TestCase):
    def setUp(self):
        setup_crypto_env()
        self.logger = logging.getLogger("test-scene")
        self.logger.addHandler(logging.NullHandler())
        self.client = _SceneClient()
        self.cache: dict = {}
        self.service = PromptService(
            client=self.client,
            memory_service=_FakeMemory(),
            notify_owner=lambda *_a, **_k: None,
            scene_cache=self.cache,
            user_memo="memo",
            logger=self.logger,
        )

    def test_initial_generation_writes_cache_with_next_hour_expiry(self):
        scene = asyncio.run(self.service.get_random_scene("u1"))
        self.assertIn("第 1 次生成", scene)
        self.assertEqual(self.client.calls, 1)

        cache = self.cache["u1"]
        self.assertIn("第 1 次生成", cache["scene"])
        self.assertGreater(cache["expires_at"], datetime.now(timezone.utc))
        self.assertLessEqual(cache["expires_at"] - datetime.now(timezone.utc), timedelta(hours=1))

        # 未过期：命中缓存，不再调用模型
        again = asyncio.run(self.service.get_random_scene("u1"))
        self.assertEqual(again, cache["scene"])
        self.assertEqual(self.client.calls, 1)

    def test_expired_cache_returns_stale_then_background_refresh(self):
        async def scenario():
            await self.service.get_random_scene("u1")
            # 把缓存手动置为过期（模拟整点已过）
            self.cache["u1"]["expires_at"] = datetime.now(timezone.utc) - timedelta(seconds=1)

            stale = await self.service.get_random_scene("u1")   # 必须立即返回旧值
            task = self.service._refresh_tasks.get("u1")
            self.assertIsNotNone(task, "过期应触发后台重建")
            await task                                          # 等后台任务完成
            fresh = await self.service.get_random_scene("u1")
            return stale, fresh

        stale, fresh = asyncio.run(scenario())
        self.assertIn("第 1 次生成", stale, "过期时先返回旧心情，保证请求零额外延迟")
        self.assertIn("第 2 次生成", fresh, "后台重建后下一条消息用到新心情")
        self.assertEqual(self.client.calls, 2)

    def test_refresh_scene_updates_cache_and_survives_failure(self):
        async def scenario():
            first = await self.service.refresh_scene("u1")
            self.client.fail_next = True
            # generate_scene 内部会兜底返回默认状态，因此刷新不会抛错、也不会清空缓存
            second = await self.service.refresh_scene("u1")
            return first, second

        first, second = asyncio.run(scenario())
        self.assertIn("第 1 次生成", first)
        self.assertTrue(second, "生成失败时仍应返回可用心情（兜底文案）")
        self.assertIn("scene", self.cache["u1"])

    def test_missing_or_malformed_expiry_is_treated_as_expired(self):
        async def scenario():
            await self.service.get_random_scene("u1")
            self.cache["u1"].pop("expires_at")
            await self.service.get_random_scene("u1")
            task = self.service._refresh_tasks.get("u1")
            self.assertIsNotNone(task, "缺少 expires_at 时应视为过期并触发重建")
            await task
            return self.cache["u1"]["scene"]

        scene = asyncio.run(scenario())
        self.assertIn("第 2 次生成", scene)

    def test_refresh_debounce_blocks_repeat_triggers(self):
        async def scenario():
            await self.service.get_random_scene("u1")
            self.cache["u1"]["expires_at"] = datetime.now(timezone.utc) - timedelta(seconds=1)
            await self.service.get_random_scene("u1")
            first_task = self.service._refresh_tasks["u1"]
            await first_task
            # 立刻再次过期：防抖窗口内不应再排一个新的重建任务
            self.cache["u1"]["expires_at"] = datetime.now(timezone.utc) - timedelta(seconds=1)
            await self.service.get_random_scene("u1")
            return first_task, self.service._refresh_tasks.get("u1")

        first_task, second_task = asyncio.run(scenario())
        self.assertIs(first_task, second_task, "60 秒防抖窗口内不重复重建")


if __name__ == "__main__":
    unittest.main()
