import json


TEXT_CODEPOINTS = 8_000
TEXT_BYTES = 32 * 1024
BODY_BYTES = 64 * 1024
TURN_SOURCES = 8
TURN_CODEPOINTS = 12_000
TURN_BYTES = 48 * 1024
MODEL_INPUT_BYTES = 96 * 1024
MODEL_OUTPUT_TOKENS = 4_096
MODEL_OUTPUT_BYTES = 16 * 1024
MEMORY_BYTES = 12 * 1024
CANDIDATE_BYTES = 16 * 1024
CANDIDATE_COUNT = 64
VISION_OUTPUT_BYTES = 4 * 1024


class InputTooLong(ValueError):
    pass


class ModelBudgetExceeded(ValueError):
    pass


def json_bytes(value):
    return len(json.dumps(value, ensure_ascii=False, allow_nan=False).encode("utf-8"))


def validate_text(text):
    try:
        size = len(text.encode("utf-8"))
    except UnicodeEncodeError:
        raise InputTooLong("文字包含无效 Unicode，请编辑后发送") from None
    if len(text) > TEXT_CODEPOINTS or size > TEXT_BYTES:
        raise InputTooLong("文字最多 8000 个 Unicode 字符、32 KiB UTF-8，请编辑后发送")


def turn_fits(texts):
    return (len(texts) <= TURN_SOURCES and sum(map(len, texts)) <= TURN_CODEPOINTS
            and sum(len(text.encode("utf-8")) for text in texts) <= TURN_BYTES)


def validate_output(text, limit=MODEL_OUTPUT_BYTES):
    if len(text.encode("utf-8")) > limit:
        raise ModelBudgetExceeded("模型返回超过大小预算")


def edit_input(request_id=None):
    return {"retryable": False, "recoveryAction": "edit_input", "requestIds": [request_id] if request_id else []}


def error_data(code, data):
    if not code:
        return data
    action = "edit_input" if code in (40001, 40002, 40200, 40901, 422) else "retry_same_request" if code == 50301 else "poll_status" if code in (5000, 50302) else "refresh_then_retry"
    return {"retryable": action in ("retry_same_request", "poll_status", "refresh_then_retry"), "recoveryAction": action, **(data or {})}
