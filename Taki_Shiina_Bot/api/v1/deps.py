"""
互动积分 · 等级体系 —— API 公共依赖

复用现有 JWT + 设备白名单鉴权体系（PRD §8：无需新增认证体系），
并统一响应信封 ``{code, message, data, traceId}``（与 ``http_api.py`` 保持一致）。
"""

from __future__ import annotations

import uuid
from typing import Any

from fastapi import HTTPException
from fastapi.responses import JSONResponse

from auth_utils import AuthContext, DeviceEntry, resolve_auth_context

# 业务错误码（HTTP 层统一返回 200 + code，便于客户端按 code 判定业务结果）
CODE_OK = 0
CODE_INVALID_PARAM = 40001
CODE_UNAUTHORIZED = 40101
CODE_FORBIDDEN = 40301
CODE_NOT_READY = 50301
CODE_INTERNAL = 5000

# 互动相关
CODE_INSUFFICIENT_POINTS = 40201
CODE_ITEM_NOT_FOUND = 40202
CODE_AI_FAILED_REFUNDED = 40204
# 补签卡相关
CODE_MAKEUP_CARD_NOT_ENOUGH = 40205
CODE_DATE_ALREADY_ACTIVE = 40206
CODE_INVALID_TARGET_DATE = 40207


class AuthRuntime:
    def __init__(
        self,
        jwt_secret: str,
        device_allowlist: dict[str, DeviceEntry] | None = None,
        legacy_token_map: dict[str, AuthContext] | None = None,
        fallback_tokens: list[str] | None = None,
        default_user_id: str = "default-user",
    ):
        self.jwt_secret = jwt_secret
        self.device_allowlist = device_allowlist or {}
        self.legacy_token_map = legacy_token_map or {}
        self.fallback_tokens = [token for token in (fallback_tokens or []) if token]
        self.default_user_id = default_user_id


_runtime: AuthRuntime | None = None
_gamification: Any = None


def configure_auth(runtime: AuthRuntime) -> None:
    global _runtime
    _runtime = runtime


def register_gamification(service: Any) -> None:
    global _gamification
    _gamification = service


def get_gamification():
    if _gamification is None:
        raise HTTPException(
            status_code=503,
            detail={"code": CODE_NOT_READY, "message": "积分/等级服务未初始化", "data": None},
        )
    return _gamification


def resolve_trace_id(request) -> str:
    header = request.headers.get("x-trace-id") if request is not None else None
    return (header or "").strip() or f"trace_{uuid.uuid4().hex}"


def response_body(code: int, message: str, data: Any, trace_id: str) -> dict[str, Any]:
    return {"code": code, "message": message, "data": data, "traceId": trace_id}


def ok(data: Any, trace_id: str, message: str = "ok") -> dict[str, Any]:
    return response_body(CODE_OK, message, data, trace_id)


def business_error(
    code: int,
    message: str,
    data: Any,
    trace_id: str,
    http_status: int = 200,
) -> JSONResponse:
    return JSONResponse(status_code=http_status, content=response_body(code, message, data, trace_id))


def require_auth(authorization: str | None, trace_id: str) -> AuthContext:
    if _runtime is None:
        raise HTTPException(
            status_code=503,
            detail=response_body(CODE_NOT_READY, "鉴权未初始化", None, trace_id),
        )
    auth = resolve_auth_context(
        token=authorization or "",
        jwt_secret=_runtime.jwt_secret,
        device_allowlist=_runtime.device_allowlist,
        legacy_token_map=_runtime.legacy_token_map,
        fallback_tokens=_runtime.fallback_tokens,
        default_user_id=_runtime.default_user_id,
    )
    if not auth:
        raise HTTPException(
            status_code=401,
            detail=response_body(CODE_UNAUTHORIZED, "鉴权失败", None, trace_id),
        )
    return auth
