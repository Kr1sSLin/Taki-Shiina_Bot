"""时间线「读失败 → 整体覆盖」回归测试（数据毁灭级缺陷）。

背景
----
`chat_timeline.json` / `memory_timeline.json` 的写入模式是
**load → extend → save 整个列表**。因此「load 失败时返回空列表」不是容错，
而是把「读不出来」静默降级成「本来就没有内容」，紧接着一次 save 就会把
整份历史替换成「只有刚追加的那两条」。

触发条件都是现实中会发生的：
  1. `DATA_ENC_KEY` 与文件加密时不一致（换机器 / 轮换密钥 / 漏配 .env）
     → `SecureJsonStore.load` 抛 `DataDecryptError`；
  2. 文件被写坏 / 截断 / 手工编辑出错 → `json.load` 抛，`load` 吞掉后返回默认值；
  3. 任何 IO 异常。

后果：所有历史永久消失，且客户端再也无法把本地 `error` 行和解回 `sent`
（服务端已经没有对应行了），界面永久停在「发送失败」。

因此本测试锁定的契约是：**读失败必须放弃本次写入**，一个字节都不许动。
文件保持原样，修好密钥 / 修好文件之后历史即可完整恢复。
"""

from __future__ import annotations

import asyncio
import base64
import importlib
import json
import os
import sys
import tempfile
import unittest

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

# httpx 会读代理环境变量；本机若设了 socks 代理会让 import 直接失败。本用例不发真实请求。
for _var in ("ALL_PROXY", "all_proxy", "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy"):
    os.environ.pop(_var, None)

KEY_GOOD = base64.urlsafe_b64encode(b"7" * 32).decode("utf-8").rstrip("=")
KEY_OTHER = base64.urlsafe_b64encode(b"9" * 32).decode("utf-8").rstrip("=")

os.environ.setdefault("DATA_AUTO_MIGRATE", "0")
os.environ.setdefault("DATA_BACKUP_ON_MIGRATE", "0")
# ws_api / http_api 在 import 时就会构造 SecureJsonStore，密钥必须先就位
os.environ.setdefault("DATA_ENC_KEY", KEY_GOOD)
os.environ.setdefault("AUTH_JWT_SECRET", "")
os.environ.setdefault("DEEPSEEK_API_KEY", "test-key")
os.environ.setdefault("AUTH_USER_ID", "test_user")
os.environ.setdefault("BOT_HTTP_TOKEN", "test-access-token")

EXISTING = [
    {"messageId": f"old_{i}", "userId": "test_user", "role": "user", "content": f"历史消息 {i}", "timestamp": 1000 + i}
    for i in range(1, 4)
]
NEW_ITEMS = [
    {"messageId": "new_u", "userId": "test_user", "role": "user", "content": "新消息", "timestamp": 9001},
    {"messageId": "new_b", "userId": "test_user", "role": "bot", "content": "收到", "timestamp": 9002},
]


def _ensure_ws_api_importable() -> None:
    """让 ws_api 可被测试导入。

    ws_api 顶层 `import google.generativeai`，而测试环境按 requirements.txt 的
    最小集合安装时并没有该包 —— 这正是 ws_api（主通道）至今**零测试覆盖**的原因，
    也是「REST 通道写了时间线、WS 通道读失败会覆盖」这类缺陷长期存活的原因。
    这里只在缺失时注入最小桩；生产环境装了真包则完全不介入。
    """
    try:
        import google.generativeai  # noqa: F401

        return
    except Exception:
        pass

    import types

    google_pkg = sys.modules.get("google")
    if google_pkg is None:
        google_pkg = types.ModuleType("google")
        google_pkg.__path__ = []  # 声明为 package，避免被当作命名空间包再次查找
        sys.modules["google"] = google_pkg

    genai = types.ModuleType("google.generativeai")

    def _configure(**_kwargs):
        return None

    class _GenerativeModel:  # pragma: no cover - 桩，不参与断言
        def __init__(self, *_args, **_kwargs) -> None:
            pass

        def generate_content(self, *_args, **_kwargs):
            raise RuntimeError("测试桩：不发起真实 Gemini 调用")

    genai.configure = _configure
    genai.GenerativeModel = _GenerativeModel
    sys.modules["google.generativeai"] = genai
    setattr(google_pkg, "generativeai", genai)


_ensure_ws_api_importable()


def _set_key(key: str) -> None:
    os.environ["DATA_ENC_KEY"] = key


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as f:
        return f.read()


class _TimelineHarness:
    """把某个模块的时间线文件/store 指向临时目录，并在结束后恢复。"""

    def __init__(self, module, file_attr: str, store_attr: str, mem: bool = False):
        self.module = module
        self.file_attr = file_attr
        self.store_attr = store_attr
        self.mem = mem
        self.tmp = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.tmp.name, "timeline.json")

    def __enter__(self):
        from secure_storage import SecureJsonStore

        self._orig_file = getattr(self.module, self.file_attr)
        self._orig_store = getattr(self.module, self.store_attr)
        setattr(self.module, self.file_attr, self.path)
        setattr(self.module, self.store_attr, SecureJsonStore(self.path, self.module.logger))
        return self

    def __exit__(self, *_exc):
        setattr(self.module, self.file_attr, self._orig_file)
        setattr(self.module, self.store_attr, self._orig_store)
        self.tmp.cleanup()
        return False

    def seed(self, items, key: str = KEY_GOOD) -> None:
        """用指定密钥写入初始历史。"""
        _set_key(key)
        from secure_storage import SecureJsonStore

        setattr(self.module, self.store_attr, SecureJsonStore(self.path, self.module.logger))
        getattr(self.module, self.store_attr).save(items)

    def store_under(self, key: str):
        """换一把密钥构造 store（模拟 DATA_ENC_KEY 与文件不一致）。"""
        _set_key(key)
        from secure_storage import SecureJsonStore

        store = SecureJsonStore(self.path, self.module.logger)
        setattr(self.module, self.store_attr, store)
        return store

    def load_under(self, key: str):
        _set_key(key)
        from secure_storage import SecureJsonStore

        return SecureJsonStore(self.path, self.module.logger).load([])


class _AppendSafetyMixin:
    """一组断言：读失败时 append 必须整体放弃，文件一字节不动。"""

    module_name = ""
    append_name = ""
    file_attr = ""
    store_attr = ""

    @property
    def module(self):
        return importlib.import_module(self.module_name)

    def _append(self, items):
        asyncio.run(getattr(self.module, self.append_name)(items))

    def test_append_aborts_when_decryption_fails(self):
        """密钥不匹配（最现实的误配）时不得覆盖文件。"""
        module = self.module
        with _TimelineHarness(module, self.file_attr, self.store_attr) as h:
            h.seed(EXISTING, KEY_GOOD)
            before = _read_bytes(h.path)
            self.assertEqual(len(h.load_under(KEY_GOOD)), 3, "前置条件：初始历史应为 3 条")

            h.store_under(KEY_OTHER)  # 读会抛 DataDecryptError
            self._append(NEW_ITEMS)

            self.assertEqual(
                _read_bytes(h.path),
                before,
                "读失败时 append 不得写回：整份历史会被替换成只有新追加的两条",
            )
            # 运维把密钥改回正确值后，历史必须完好无损
            restored = h.load_under(KEY_GOOD)
            self.assertEqual(
                [x["messageId"] for x in restored],
                ["old_1", "old_2", "old_3"],
                "读失败后历史必须仍然完整（修好密钥即可恢复）",
            )

    def test_append_aborts_when_file_is_corrupt(self):
        """文件损坏（json 解析失败）时不得把它替换成「只有新内容」。"""
        module = self.module
        with _TimelineHarness(module, self.file_attr, self.store_attr) as h:
            h.seed(EXISTING, KEY_GOOD)
            with open(h.path, "w", encoding="utf-8") as f:
                f.write('{"ver": 1, "alg": "AESGCM", "nonce": "!!!", "ciphertext": "truncated')
            before = _read_bytes(h.path)

            h.store_under(KEY_GOOD)
            self._append(NEW_ITEMS)

            self.assertEqual(
                _read_bytes(h.path),
                before,
                "文件损坏时 append 不得覆盖：否则损坏会被升级成永久性数据丢失",
            )

    def test_append_still_works_when_file_missing(self):
        """文件不存在（首次写入）时仍必须正常追加，不得被本修复误伤。"""
        module = self.module
        with _TimelineHarness(module, self.file_attr, self.store_attr) as h:
            h.store_under(KEY_GOOD)
            self._append(NEW_ITEMS)
            items = h.load_under(KEY_GOOD)
            self.assertEqual([x["messageId"] for x in items], ["new_u", "new_b"])

    def test_append_preserves_existing_items(self):
        """正常路径：新项追加在既有历史之后，既有项不得丢失。"""
        module = self.module
        with _TimelineHarness(module, self.file_attr, self.store_attr) as h:
            h.seed(EXISTING, KEY_GOOD)
            h.store_under(KEY_GOOD)
            self._append(NEW_ITEMS)
            items = h.load_under(KEY_GOOD)
            self.assertEqual(
                [x["messageId"] for x in items],
                ["old_1", "old_2", "old_3", "new_u", "new_b"],
            )


class WsApiChatTimelineSafetyTestCase(_AppendSafetyMixin, unittest.TestCase):
    """ws_api（主通道）chat_timeline.json 写入安全。"""

    module_name = "ws_api"
    append_name = "append_timeline"
    file_attr = "TIMELINE_FILE"
    store_attr = "timeline_store"


class WsApiMemoryTimelineSafetyTestCase(_AppendSafetyMixin, unittest.TestCase):
    """ws_api memory_timeline.json 写入安全。"""

    module_name = "ws_api"
    append_name = "append_memory_timeline"
    file_attr = "MEMORY_TIMELINE_FILE"
    store_attr = "memory_timeline_store"


class HttpApiChatTimelineSafetyTestCase(_AppendSafetyMixin, unittest.TestCase):
    """http_api（REST 兜底通道）chat_timeline.json 写入安全。"""

    module_name = "http_api"
    append_name = "append_timeline"
    file_attr = "TIMELINE_FILE"
    store_attr = "timeline_store"


class HttpApiMemoryTimelineSafetyTestCase(_AppendSafetyMixin, unittest.TestCase):
    """http_api memory_timeline.json 写入安全。"""

    module_name = "http_api"
    append_name = "append_memory_timeline"
    file_attr = "MEMORY_TIMELINE_FILE"
    store_attr = "memory_timeline_store"


class StoreJsonCorruptionTestCase(unittest.TestCase):
    """补一条底层语义：SecureJsonStore.load 遇到坏 JSON 会静默返回默认值。

    这是上面四个缺陷的**共同源头**——所以写入方不能依赖 `load(default)` 判断
    「文件是空的」还是「读失败了」。这里把该行为固定下来，作为文档化事实。
    """

    def test_load_swallows_json_error_and_returns_default(self):
        from secure_storage import SecureJsonStore
        import logging

        _set_key(KEY_GOOD)
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "x.json")
            with open(path, "w", encoding="utf-8") as f:
                f.write("not json at all")
            store = SecureJsonStore(path, logging.getLogger("test"))
            self.assertEqual(store.load(["DEFAULT"]), ["DEFAULT"])

    def test_load_strict_raises_on_json_error(self):
        from secure_storage import SecureJsonStore
        import logging

        _set_key(KEY_GOOD)
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "x.json")
            with open(path, "w", encoding="utf-8") as f:
                f.write("not json at all")
            store = SecureJsonStore(path, logging.getLogger("test"))
            with self.assertRaises(json.JSONDecodeError):
                store.load_strict(["DEFAULT"])


if __name__ == "__main__":
    unittest.main()
