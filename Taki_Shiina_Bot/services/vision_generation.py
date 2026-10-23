"""Shared image description provider for independent HTTP and WebSocket workers.

The caller verifies image identity and its turn lease before invoking this
function. Original image bytes, including EXIF and pixels, go to Gemini as the
user authorized. The caller redacts coordinates from the returned *text* before
passing it to the chat model.
"""
import asyncio
import base64
import os

import google.generativeai as genai

from services.vision_limits import MAX_INLINE_IMAGE_BYTES
from services.public_policy import public_text, require_public_text
from services.input_budget import MODEL_INPUT_BYTES, VISION_OUTPUT_BYTES, ModelBudgetExceeded, validate_output


VISION_DESCRIPTION_PROMPT = (
    "你是一个图片描述助手。请客观、准确地描述用户图片中的内容，供后续文本模型参考。\n"
    "要求：\n"
    "1. 输出纯事实描述：画面主体、环境、动作、人物表情、画面中的文字（如有）等。\n"
    "2. 严禁代入任何人设或角色，严禁使用对话语气，严禁评价图片好坏。\n"
    "3. 如果画面中有可辨认的文字，请逐字引用。\n"
    "4. 如果图片内容不清晰或无法辨认，直接回答：图片内容无法辨认。\n"
    "5. 200 字以内，直接输出描述正文，不要加标题或前缀。"
)


async def describe_images(images: list[dict], caption: str) -> str:
    """Send exact image bytes to Gemini and return its unmodified description."""
    api_key = os.getenv("GEMINI_API_KEY", "")
    if not api_key:
        raise RuntimeError("vision model is not configured")
    genai.configure(api_key=api_key, transport="rest")
    model = genai.GenerativeModel(os.getenv("GEMINI_MODEL", "gemini-2.5-flash"))
    prompt = public_text("image_description") + "\n" + VISION_DESCRIPTION_PROMPT + (f"\n\n用户留言：{caption}" if caption else "")
    require_public_text(prompt)
    if len(prompt.encode("utf-8")) > MODEL_INPUT_BYTES:
        raise ModelBudgetExceeded("识图文本输入超过预算")
    inline_size = len(prompt.encode("utf-8")) + sum(len(base64.b64encode(image["raw"])) for image in images) + 1_000_000
    if sum(len(image["raw"]) for image in images) > MAX_INLINE_IMAGE_BYTES or inline_size >= 20_000_000:
        raise ValueError("images exceed Gemini inline request budget")
    parts = [prompt, *({"mime_type": image["mimeType"], "data": image["raw"]} for image in images)]
    response = await asyncio.wait_for(asyncio.to_thread(model.generate_content, parts,
        generation_config={"max_output_tokens": 1024}, request_options={"timeout": 20, "retry": None}), timeout=20)
    if any(getattr(candidate, "finish_reason", None) == 2 for candidate in getattr(response, "candidates", [])):
        raise ModelBudgetExceeded("识图输出未完整结束")
    raw = response.text or ""
    validate_output(raw, VISION_OUTPUT_BYTES)
    description = raw.strip()
    if not description:
        raise ValueError("vision model returned no description")
    return description
