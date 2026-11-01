"""包入口沿用项目目录入口的依赖解析路径。"""
import os
import sys
from importlib import import_module

_base_dir = os.path.dirname(os.path.abspath(__file__))
if _base_dir not in sys.path:
    sys.path.insert(0, _base_dir)


def __getattr__(name):
    if name in ("http_api", "ws_api", "unified_api"):
        module = import_module(f"{__name__}.{name}")
        globals()[name] = module
        return module
    raise AttributeError(name)
