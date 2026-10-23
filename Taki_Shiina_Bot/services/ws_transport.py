"""WS 帧与图片的边界校验，不持有会话或业务状态。"""
import base64
import binascii
import hashlib
import math
from io import BytesIO

from PIL import Image, UnidentifiedImageError


def validate_frame(message):
    if not isinstance(message, dict) or not isinstance(message.get("type"), str) or not message["type"]:
        raise ValueError("invalid frame type")
    if "payload" in message and not isinstance(message["payload"], dict):
        raise ValueError("invalid payload")
    if "requestId" in message and (not isinstance(message["requestId"], str) or not message["requestId"].strip()):
        raise ValueError("invalid request identity")
    payload = message.get("payload", {})
    if "clientMessageId" in payload and (not isinstance(payload["clientMessageId"], str) or not payload["clientMessageId"].strip()):
        raise ValueError("invalid client identity")
    if message["type"] == "chat.message" and "content" in payload and payload["content"] is not None and not isinstance(payload["content"], str):
        raise ValueError("invalid content type")
    if message["type"] != "ping":
        return None
    timestamps = [obj["timestamp"] for obj in (message, payload) if "timestamp" in obj]
    if any(type(value) not in (int, float) or not math.isfinite(value) or value < 0 for value in timestamps):
        raise ValueError("invalid ping timestamp")
    if len(timestamps) == 2 and timestamps[0] != timestamps[1]:
        raise ValueError("conflicting ping timestamps")
    return timestamps[0] if timestamps else None


def parse_images(raw_images, *, max_count, max_image_mb, max_total_bytes, allowed_mime):
    parsed_images = []
    total_image_bytes = 0
    if len(raw_images) > max_count:
        raise ValueError("VISION_IMAGE_COUNT_EXCEEDED", f"单次最多 {max_count} 张图片")
    for idx, item in enumerate(raw_images):
        if not isinstance(item, dict):
            raise ValueError("VISION_INVALID_MIME", f"第 {idx + 1} 张图片格式无效")
        raw_mime_type = item.get("mimeType", "")
        mime_type = raw_mime_type.strip().lower() if isinstance(raw_mime_type, str) else ""
        data_base64 = (item or {}).get("dataBase64", "")
        if mime_type not in allowed_mime:
            raise ValueError("VISION_INVALID_MIME", f"第 {idx + 1} 张图片格式不支持")
        if not isinstance(data_base64, str) or len(data_base64) > ((max_image_mb * 1024 * 1024 + 2) // 3) * 4 + 4:
            raise ValueError("VISION_IMAGE_TOO_LARGE", f"第 {idx + 1} 张图片超过 {max_image_mb}MB")
        try:
            raw = base64.b64decode(data_base64, validate=True)
        except (ValueError, binascii.Error):
            raise ValueError("VISION_INVALID_BASE64", f"第 {idx + 1} 张图片解析失败")
        if len(raw) > max_image_mb * 1024 * 1024:
            raise ValueError("VISION_IMAGE_TOO_LARGE", f"第 {idx + 1} 张图片超过 {max_image_mb}MB")
        if not raw:
            raise ValueError("VISION_INVALID_BASE64", f"第 {idx + 1} 张图片为空")
        total_image_bytes += len(raw)
        if total_image_bytes > max_total_bytes:
            raise ValueError("VISION_IMAGE_TOO_LARGE", "本轮图片总量超过识图模型的请求上限")
        try:
            with Image.open(BytesIO(raw)) as decoded:
                if Image.MIME.get(decoded.format) != mime_type or decoded.width * decoded.height > 50_000_000:
                    raise ValueError("image format or dimensions are invalid")
                decoded.verify()
        except (UnidentifiedImageError, Image.DecompressionBombError, OSError, ValueError):
            raise ValueError("VISION_INVALID_MIME", f"第 {idx + 1} 张图片格式无效")
        parsed_images.append({"mimeType": mime_type, "raw": raw, "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()})
    return parsed_images
