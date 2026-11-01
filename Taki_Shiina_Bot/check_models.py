"""手动查询 Gemini 模型清单；导入本模块不会访问网络。"""

from __future__ import annotations

import os


def main() -> int:
    key = (os.getenv("GEMINI_API_KEY") or os.getenv("GOOGLE_API_KEY") or "").strip()
    if not key:
        print("请先配置 GEMINI_API_KEY 或 GOOGLE_API_KEY。")
        return 2

    import google.generativeai as genai

    try:
        genai.configure(api_key=key)
        print("正在查询支持 generateContent 的模型：")
        for model in genai.list_models():
            if "generateContent" in model.supported_generation_methods:
                print(model.name)
    except Exception as exc:
        # 不打印 SDK 异常正文，避免 URL 或凭据进入终端记录。
        print(f"查询失败：{type(exc).__name__}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
