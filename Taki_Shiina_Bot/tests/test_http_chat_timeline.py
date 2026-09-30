"""HTTP 兜底通道必须把消息写入 chat_timeline.json。

背景（客户端故障复盘）：客户端「发送」走 WS，「历史同步」走 GET /chat/history，
后者读的是 chat_timeline.json。ws_api 会写该文件，而 http_api 的
POST /api/v1/chat 早前**完全没写**，于是通过 REST 兜底发出的消息
永远进不了历史 —— 客户端本地那条 `error` 记录无论同步多少次都无法修复，
界面永久停在「发送失败」，也不会同步到其它设备。

本用例锁定该契约：
1. POST /api/v1/chat 之后，timeline 里出现 user + bot 两条；
2. 用户项的 messageId **等于**客户端传来的 requestId（客户端据此对齐本地行）；
3. GET /chat/history?since=... 能把它们取回来。
"""

from __future__ import annotations

import base64
import os
import tempfile
import unittest

from ws_api_import_fixture import ensure_ws_api_env

ensure_ws_api_env()
# httpx 会读代理环境变量；本机若设了 socks 代理会让 import 直接失败。
# 本用例不发真实请求，清掉即可。
for _var in ("ALL_PROXY", "all_proxy", "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy"):
    os.environ.pop(_var, None)
os.environ.setdefault("DATA_ENC_KEY", base64.b64encode(b"0" * 32).decode())
os.environ.setdefault("AUTH_JWT_SECRET", "")
# http_api 在 import 时读取这些常量，必须在 import 之前设好
os.environ["BOT_HTTP_TOKEN"] = "test-access-token"
os.environ.setdefault("AUTH_USER_ID", "test_user")
os.environ.setdefault("DEEPSEEK_API_KEY", "test-key")

TEST_TOKEN = "test-access-token"
AUTH_HEADER = {"Authorization": f"Bearer {TEST_TOKEN}"}


class _FakeMessage:
    def __init__(self, content: str):
        self.content = content


class _FakeChoice:
    def __init__(self, content: str):
        self.message = _FakeMessage(content)


class _FakeUsage:
    prompt_tokens = 1
    completion_tokens = 2
    total_tokens = 3


class _FakeResponse:
    def __init__(self, content: str):
        self.choices = [_FakeChoice(content)]
        self.usage = _FakeUsage()


class _FakeCompletions:
    async def create(self, **_kwargs):
        return _FakeResponse("收下了。")


class _FakeChatNamespace:
    completions = _FakeCompletions()


class _FakeClient:
    chat = _FakeChatNamespace()


class HttpChatTimelineTestCase(unittest.TestCase):
    """POST /api/v1/chat → chat_timeline.json → GET /api/v1/chat/history 闭环。"""

    @classmethod
    def setUpClass(cls):
        try:
            import http_api  # noqa: PLC0415
        except Exception as exc:  # pragma: no cover - 环境缺依赖时跳过而非误报
            raise unittest.SkipTest(f"http_api 无法导入: {exc}") from exc
        cls.http_api = http_api

    def setUp(self):
        from fastapi.testclient import TestClient

        api = self.http_api
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        from services.history_store import HistoryStore
        from unittest.mock import patch
        history_patch = patch.object(api, "history_store", HistoryStore(os.path.join(self._tmp.name, "history.json")))
        history_patch.start()
        self.addCleanup(history_patch.stop)

        # 把 timeline 指向临时目录，避免污染真实数据
        timeline_file = os.path.join(self._tmp.name, "chat_timeline.json")
        from secure_storage import SecureJsonStore

        self._orig_file = api.TIMELINE_FILE
        self._orig_store = api.timeline_store
        api.TIMELINE_FILE = timeline_file
        api.timeline_store = SecureJsonStore(timeline_file, api.logger)
        self.addCleanup(lambda: setattr(api, "TIMELINE_FILE", self._orig_file))
        self.addCleanup(lambda: setattr(api, "timeline_store", self._orig_store))

        # 替换 LLM 客户端与外部副作用，只保留时间线写入路径
        self._orig_client = api.client
        api.client = _FakeClient()
        self.addCleanup(lambda: setattr(api, "client", self._orig_client))

        self._orig_extract = api.extract_user_facts

        async def _noop_extract(*_a, **_kw):
            return None

        api.extract_user_facts = _noop_extract
        self.addCleanup(lambda: setattr(api, "extract_user_facts", self._orig_extract))

        self._orig_weather = api.weather_service.get_weather_str

        async def _fake_weather(*_a, **_kw):
            return "天气：晴"

        api.weather_service.get_weather_str = _fake_weather
        self.addCleanup(lambda: setattr(api.weather_service, "get_weather_str", self._orig_weather))

        self._orig_prompt = api.prompt_service.get_system_prompt

        async def _fake_prompt(*_a, **_kw):
            return "你是 Taki。"

        api.prompt_service.get_system_prompt = _fake_prompt
        self.addCleanup(lambda: setattr(api.prompt_service, "get_system_prompt", self._orig_prompt))

        self.client = TestClient(api.app)

    def _post_chat(self, request_id: str, message: str = "帮我记一下"):
        return self.client.post(
            "/api/v1/chat",
            json={"requestId": request_id, "message": message},
            headers=AUTH_HEADER,
        )

    def test_chat_writes_timeline_keyed_by_request_id(self):
        request_id = "req_client_abc123"
        response = self._post_chat(request_id)
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(response.json().get("code"), 0, response.text)

        items = self.http_api.timeline_store.load([])
        roles = [x.get("role") for x in items]
        self.assertIn("user", roles, f"timeline 缺少 user 项: {items}")
        self.assertIn("bot", roles, f"timeline 缺少 bot 项: {items}")

        user_item = next(x for x in items if x.get("role") == "user")
        self.assertEqual(
            user_item.get("messageId"),
            request_id,
            "用户项的 messageId 必须等于客户端 requestId，否则客户端无法把本地 error 行修复为 sent",
        )
        self.assertTrue(user_item.get("timestamp"), "timeline 项必须带 timestamp（history 按其过滤）")

    def test_history_returns_messages_written_by_http_channel(self):
        request_id = "req_client_def456"
        self.assertEqual(self._post_chat(request_id).status_code, 200)

        history = self.client.get("/api/v1/chat/history?since=0&limit=50", headers=AUTH_HEADER)
        self.assertEqual(history.status_code, 200, history.text)
        payload = history.json()
        self.assertEqual(payload.get("code"), 0, history.text)
        ids = [x.get("messageId") for x in payload["data"]["items"]]
        self.assertIn(
            request_id,
            ids,
            "通过 HTTP 通道发出的消息必须能被 /chat/history 取回，否则客户端永远无法和解",
        )

    def test_bot_and_user_items_have_distinct_ids(self):
        request_id = "req_client_ghi789"
        self.assertEqual(self._post_chat(request_id).status_code, 200)
        items = self.http_api.timeline_store.load([])
        ids = [x.get("messageId") for x in items]
        self.assertEqual(len(ids), len(set(ids)), f"timeline messageId 必须唯一: {ids}")

    def test_chat_storage_failure_returns_generic_error(self):
        import copy
        from unittest.mock import AsyncMock, patch
        before = copy.deepcopy(self.http_api.state.user_chat_history)
        original_append = self.http_api.append_timeline

        async def _fail(_items):
            raise RuntimeError("sensitive storage path")

        self.http_api.append_timeline = _fail
        self.addCleanup(lambda: setattr(self.http_api, "append_timeline", original_append))
        with patch.object(self.http_api, "extract_user_facts", AsyncMock()) as extract:
            response = self._post_chat("req-storage-fail")
            extract.assert_not_called()
        # Creating an empty per-user context is harmless; no failed message may persist.
        self.assertEqual(
            {key: value for key, value in self.http_api.state.user_chat_history.items() if value},
            {key: value for key, value in before.items() if value},
        )
        self.assertFalse(os.path.exists(os.path.join(self._tmp.name, "history.json")))
        self.assertEqual(response.status_code, 500)
        self.assertEqual(response.json()["code"], 5000)
        self.assertNotIn("sensitive storage path", response.text)


if __name__ == "__main__":
    unittest.main()
