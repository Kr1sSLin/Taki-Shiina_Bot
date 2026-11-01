"""
互动菜单路由（PRD §8）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET  | /api/v1/interaction/items | 获取互动菜单物品列表（按 sort_order 平铺排序，FR-5） |
| POST | /api/v1/interaction/send  | 发送互动物品：扣积分 → 组装 Prompt → 调用 DeepSeek → WS 推送回复 |
"""

from __future__ import annotations

from services.async_storage import gamification_call

from fastapi import APIRouter, Header, Request
from pydantic import BaseModel
from services.controlled_response import ControlledJSONResponse
from services.chat_runtime import ChatFailed
from services.foundation_store import PermissionDenied, StorageBusy, VersionConflict
from services.interaction_config_service import ConfigUnavailableError
from services.input_budget import BODY_BYTES, InputTooLong, validate_text, edit_input

from api.v1.deps import (
    CODE_INSUFFICIENT_POINTS,
    CODE_ITEM_NOT_FOUND,
    CODE_INVALID_PARAM,
    business_error,
    get_gamification,
    ok,
    require_auth,
    resolve_trace_id,
)

router = APIRouter()


class InteractionSendReq(BaseModel):
    itemId: str | None = None
    requestId: str | None = None
    idempotencyKey: str | None = None
    # O5：送礼时输入框里的附言，随礼物一起交给模型（可选）
    text: str | None = None


@router.get("/interaction/items")
async def interaction_items(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    await gamification_call(gamification.touch_user, auth.user_id)
    handler = getattr(gamification, "interaction", None)
    if handler is None:
        return business_error(50301, "互动服务未就绪", None, trace_id, http_status=503)
    try:
        return ok(await gamification_call(handler.list_items, auth.user_id), trace_id)
    except ConfigUnavailableError:
        return business_error(50303, "送礼配置暂不可用，请稍后重试", {"retryable": True, "recoveryAction": "refresh_then_retry"}, trace_id)


@router.post("/interaction/send")
async def interaction_send(
    payload: InteractionSendReq,
    request: Request,
    authorization: str | None = Header(default=None),
):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    handler = getattr(gamification, "interaction", None)
    if handler is None:
        return business_error(50301, "互动服务未就绪", None, trace_id, http_status=503)

    item_id = (payload.itemId or "").strip()
    if not item_id:
        return business_error(CODE_INVALID_PARAM, "itemId 不能为空", None, trace_id, http_status=400)

    request_id = (payload.requestId or payload.idempotencyKey or "").strip() or None
    try:
        runtime = handler.runtime_provider() if handler.runtime_provider else None
        if not runtime or not request_id or not await runtime.request(auth.user_id, request_id):
            validate_text(payload.text or "")
            if getattr(request.state, "body_size", 0) > BODY_BYTES:
                raise InputTooLong("请求体最多 64 KiB，请编辑后发送")
    except InputTooLong as error:
        return business_error(CODE_INVALID_PARAM, str(error), edit_input(request_id), trace_id, http_status=400)
    except (StorageBusy, PermissionDenied):
        return business_error(50301, "服务暂时不可用，请使用原请求重试", None, trace_id, http_status=503)
    result = await handler.send(
        user_id=auth.user_id,
        item_id=item_id,
        request_id=request_id,
        device_id=auth.device_id,
        attachment=(payload.text or "").strip() or None,
    )

    code = result.error_code
    if result.ok:
        runtime = handler.runtime_provider() if handler.runtime_provider else None
        if runtime:
            try:
                existing = await runtime.request(auth.user_id, result.request_id)
                durable = await runtime.wait(auth.user_id, existing["turn_id"])
            except (ChatFailed, PermissionDenied, StorageBusy, VersionConflict):
                return business_error(50302, "暂时未能完成，请使用原请求重试", None, trace_id)
            result.reply = durable["reply"]
            result.message_id = durable["messageId"]
            result.timer_instruction = durable.get("timerInstruction")
            result.merged_request_ids = [identity for identity in durable.get("requestIds", []) if identity != result.request_id]
            return ControlledJSONResponse(ok(result.to_http_data(), trace_id), store=runtime.store, owner=auth.user_id, results=[durable])
        return ok(result.to_http_data(), trace_id)
    mapping = {
        CODE_INSUFFICIENT_POINTS: (CODE_INSUFFICIENT_POINTS, "积分不足"),
        CODE_ITEM_NOT_FOUND: (CODE_ITEM_NOT_FOUND, "物品不存在或已下架"),
        CODE_INVALID_PARAM: (CODE_INVALID_PARAM, "参数错误"),
    }
    error_code, message = mapping.get(code, (code, result.message or "互动失败"))
    return business_error(error_code, message, result.to_http_data(), trace_id)
