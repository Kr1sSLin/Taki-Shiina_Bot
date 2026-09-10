"""
互动菜单路由（PRD §8）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET  | /api/v1/interaction/items | 获取互动菜单物品列表（按 sort_order 平铺排序，FR-5） |
| POST | /api/v1/interaction/send  | 发送互动物品：扣积分 → 组装 Prompt → 调用 DeepSeek → WS 推送回复 |
"""

from __future__ import annotations

from fastapi import APIRouter, Header, Request
from pydantic import BaseModel

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
    gamification.touch_user(auth.user_id)
    handler = getattr(gamification, "interaction", None)
    if handler is None:
        return business_error(50301, "互动服务未就绪", None, trace_id, http_status=503)
    return ok(handler.list_items(auth.user_id), trace_id)


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
    result = await handler.send(
        user_id=auth.user_id,
        item_id=item_id,
        request_id=request_id,
        device_id=auth.device_id,
        attachment=(payload.text or "").strip() or None,
    )

    code = result.error_code
    if result.ok:
        return ok(result.to_http_data(), trace_id)
    mapping = {
        CODE_INSUFFICIENT_POINTS: (CODE_INSUFFICIENT_POINTS, "积分不足"),
        CODE_ITEM_NOT_FOUND: (CODE_ITEM_NOT_FOUND, "物品不存在或已下架"),
        CODE_INVALID_PARAM: (CODE_INVALID_PARAM, "参数错误"),
    }
    error_code, message = mapping.get(code, (code, result.message or "互动失败"))
    return business_error(error_code, message, result.to_http_data(), trace_id)
