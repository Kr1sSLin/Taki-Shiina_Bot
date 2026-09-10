"""
积分与补签卡路由（PRD §8）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET  | /api/v1/points/balance                | 查询当前积分余额（FR-11） |
| GET  | /api/v1/points/overview               | 余额 + 等级 + 补签卡聚合（客户端首屏） |
| GET  | /api/v1/points/history                | 查询积分变动流水（分页，FR-11） |
| GET  | /api/v1/points/makeup-card            | 查询当前可用补签卡数量（FR-19） |
| POST | /api/v1/points/makeup-card/use        | 使用补签卡为指定日期补签（FR-20/22，6.2） |
| GET  | /api/v1/points/makeup-card/history    | 查询补签卡发放/使用历史，便于核对跨月结转（FR-21） |
"""

from __future__ import annotations

from fastapi import APIRouter, Header, Query, Request
from pydantic import BaseModel

from api.v1.deps import (
    CODE_DATE_ALREADY_ACTIVE,
    CODE_INVALID_PARAM,
    CODE_INVALID_TARGET_DATE,
    CODE_MAKEUP_CARD_NOT_ENOUGH,
    business_error,
    get_gamification,
    ok,
    require_auth,
    resolve_trace_id,
)
from services.makeup_card_service import (
    ERROR_CARD_NOT_ENOUGH,
    ERROR_DATE_ALREADY_ACTIVE,
    ERROR_INVALID_TARGET_DATE,
    ERROR_TARGET_IN_FUTURE,
)
from services.points_events import level_changed_event, makeup_card_changed_event, points_changed_event

router = APIRouter()

_MAKEUP_ERROR_MAP = {
    ERROR_CARD_NOT_ENOUGH: (CODE_MAKEUP_CARD_NOT_ENOUGH, "补签卡不足"),
    ERROR_DATE_ALREADY_ACTIVE: (CODE_DATE_ALREADY_ACTIVE, "该日期已有有效对话记录，无需补签"),
    ERROR_INVALID_TARGET_DATE: (CODE_INVALID_TARGET_DATE, "target_date 需为 YYYY-MM-DD 格式"),
    ERROR_TARGET_IN_FUTURE: (CODE_INVALID_TARGET_DATE, "不能为未来日期补签"),
}


class MakeupCardUseReq(BaseModel):
    targetDate: str | None = None
    target_date: str | None = None


@router.get("/points/balance")
async def points_balance(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    gamification.touch_user(auth.user_id)
    detail = gamification.points.get_balance_detail(auth.user_id)
    return ok({"balance": detail["balance"], "updatedAt": detail.get("updatedAt")}, trace_id)


@router.get("/points/overview")
async def points_overview(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    return ok(gamification.get_points_overview(auth.user_id), trace_id)


@router.get("/points/history")
async def points_history(
    request: Request,
    page: int = Query(default=1, ge=1),
    pageSize: int = Query(default=20, ge=1, le=100),
    reasonCode: str | None = None,
    authorization: str | None = Header(default=None),
):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    data = gamification.points.get_history(
        auth.user_id,
        page=page,
        page_size=pageSize,
        reason_code=(reasonCode or "").strip() or None,
    )
    return ok(data, trace_id)


@router.get("/points/makeup-card")
async def makeup_card_summary(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    gamification.touch_user(auth.user_id)
    return ok(gamification.makeup.get_summary(auth.user_id), trace_id)


@router.post("/points/makeup-card/use")
async def makeup_card_use(
    payload: MakeupCardUseReq,
    request: Request,
    authorization: str | None = Header(default=None),
):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    gamification.touch_user(auth.user_id)

    target_date = (payload.targetDate or payload.target_date or "").strip()
    if not target_date:
        return business_error(CODE_INVALID_PARAM, "targetDate 不能为空", None, trace_id, http_status=400)

    result = gamification.makeup.use_card(auth.user_id, target_date)
    if not result.ok:
        error_code, message = _MAKEUP_ERROR_MAP.get(
            result.error_code or "", (CODE_INVALID_PARAM, result.message or "补签失败")
        )
        return business_error(error_code, message, result.to_dict(), trace_id, http_status=200)

    if gamification.interaction is not None:
        # 补签可能让等级一次性跳回（6.1 NONE → 高等级），通知全部在线设备（EDGE-8）
        try:
            for entry in result.rewards:
                await gamification.interaction.push_event(
                    auth.user_id, points_changed_event(entry)
                )
            if result.level is not None and result.level.level_changed:
                await gamification.interaction.push_event(
                    auth.user_id, level_changed_event(result.level)
                )
            await gamification.interaction.push_event(
                auth.user_id,
                makeup_card_changed_event(gamification.makeup.get_summary(auth.user_id), "USED"),
            )
        except Exception:
            pass

    return ok(result.to_dict(), trace_id)


@router.get("/points/makeup-card/history")
async def makeup_card_history(
    request: Request,
    page: int = Query(default=1, ge=1),
    pageSize: int = Query(default=20, ge=1, le=100),
    authorization: str | None = Header(default=None),
):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    return ok(gamification.makeup.get_history(auth.user_id, page=page, page_size=pageSize), trace_id)


@router.get("/points/makeup-card/candidates")
async def makeup_card_candidates(
    request: Request,
    limit: int = Query(default=120, ge=1, le=365),
    authorization: str | None = Header(default=None),
):
    """可补签日期候选列表（PRD FR-22：可回溯任意历史缺口，客户端据此给出可选项）。"""
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    gamification.touch_user(auth.user_id)
    return ok(gamification.makeup.get_candidates(auth.user_id, limit=limit), trace_id)
