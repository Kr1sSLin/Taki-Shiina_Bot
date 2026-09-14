"""让 `ws_api` 可被测试导入的夹具。

`ws_api` 顶层执行 `import google.generativeai`，而测试环境按最小依赖安装时
并没有该包（`requirements.txt` 里有，但未安装）。结果是 **ws_api（主通道）至今
零测试覆盖** —— 这正是「REST 通道写了时间线、WS 通道读失败却整份覆盖」
「被防抖合并的消息漏写时间线」这类缺陷能长期存活的原因。

这里只在缺失时注入最小桩；装了真包的环境完全不介入。
"""

from __future__ import annotations

import os
import sys
import types

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
for _path in (PACKAGE_ROOT, CURRENT_DIR):
    if _path not in sys.path:
        sys.path.insert(0, _path)


def ensure_ws_api_importable() -> None:
    try:
        import google.generativeai  # noqa: F401

        return
    except Exception:
        pass

    google_pkg = sys.modules.get("google")
    if google_pkg is None:
        google_pkg = types.ModuleType("google")
        google_pkg.__path__ = []  # 声明为 package，避免被当成命名空间包再次查找
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


def ensure_ws_api_env() -> None:
    """ws_api 在 import 时就会构造 SecureJsonStore / 读环境变量。"""
    import base64

    for var in ("ALL_PROXY", "all_proxy", "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy"):
        os.environ.pop(var, None)
    os.environ.setdefault("DATA_AUTO_MIGRATE", "0")
    os.environ.setdefault("DATA_BACKUP_ON_MIGRATE", "0")
    os.environ.setdefault("DATA_ENC_KEY", base64.urlsafe_b64encode(b"7" * 32).decode("utf-8").rstrip("="))
    os.environ.setdefault("AUTH_JWT_SECRET", "")
    os.environ.setdefault("DEEPSEEK_API_KEY", "test-key")
    os.environ.setdefault("AUTH_USER_ID", "test_user")
    os.environ.setdefault("BOT_HTTP_TOKEN", "test-access-token")
    ensure_ws_api_importable()
