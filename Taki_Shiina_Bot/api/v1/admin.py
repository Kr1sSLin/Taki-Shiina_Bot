"""
后台配置与运维路由（PRD 九·可配置性 / US-3 / 5.5）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET  | /api/v1/admin/gamification-config          | 查询全部可配置项（物品 / Prompt 模板 / 等级 / 规则） |
| PUT  | /api/v1/admin/gamification-config          | 局部更新配置段，改完即时生效、无需发版 |
| POST | /api/v1/admin/gamification-config/reset    | 恢复内置默认配置 |
| GET  | /api/v1/admin/user-progress                | 只读查看某账号的积分/等级/补签卡状态，便于对账排障 |

配置同时支持运维直接编辑 ``config/gamification_config.json``（按 mtime 热加载），
两种方式等价，后者适合批量改动。
"""

from __future__ import annotations

import os
import time
from typing import Any

from fastapi import APIRouter, Body, Header, Request
from pydantic import BaseModel

from api.v1.deps import (
    CODE_INVALID_PARAM,
    CODE_ITEM_NOT_FOUND,
    business_error,
    get_gamification,
    ok,
    require_auth,
    resolve_trace_id,
)
from services.interaction_config_service import ConfigValidationError
from services.points_events import points_changed_event
from services.points_service import InsufficientPointsError
from time_utils import get_business_today, parse_business_date

router = APIRouter()


@router.get("/admin/gamification-config")
async def get_gamification_config(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    require_auth(authorization, trace_id)
    gamification = get_gamification()
    return ok(gamification.config.get_config(), trace_id)


@router.put("/admin/gamification-config")
async def update_gamification_config(
    request: Request,
    payload: dict[str, Any] = Body(default_factory=dict),
    authorization: str | None = Header(default=None),
):
    trace_id = resolve_trace_id(request)
    require_auth(authorization, trace_id)
    gamification = get_gamification()
    if not payload:
        return business_error(CODE_INVALID_PARAM, "请求体不能为空", None, trace_id, http_status=400)
    try:
        config = gamification.config.update_sections(payload)
    except ConfigValidationError as exc:
        return business_error(CODE_INVALID_PARAM, f"配置校验失败: {exc}", None, trace_id, http_status=400)
    return ok(config, trace_id, message="配置已更新")


@router.post("/admin/gamification-config/reset")
async def reset_gamification_config(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    require_auth(authorization, trace_id)
    gamification = get_gamification()
    return ok(gamification.config.reset_to_default(), trace_id, message="已恢复默认配置")


@router.get("/admin/user-progress")
async def user_progress(
    request: Request,
    userId: str | None = None,
    authorization: str | None = Header(default=None),
):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    target = (userId or "").strip() or auth.user_id

    progress_data = gamification.progress_store.snapshot()
    points_data = gamification.points_store.snapshot()
    activity = [
        entry
        for entry in progress_data.get("daily_activity_log", {}).values()
        if entry.get("user_id") == target
    ]
    activity.sort(key=lambda entry: entry.get("activity_date", ""), reverse=True)
    return ok(
        {
            "userId": target,
            "balance": gamification.points.get_balance(target),
            "level": gamification.get_level_status(target),
            "makeupCard": gamification.makeup.get_summary(target),
            "progress": progress_data.get("user_progress", {}).get(target),
            "recentActivity": activity[:30],
            "ledgerCount": sum(
                1 for entry in points_data.get("points_ledger", []) if entry.get("user_id") == target
            ),
        },
        trace_id,
    )


# ==================== 测试 / 运维接口 ====================
# ⚠️ 以下接口只用于联调与测试：同样需要 JWT 鉴权，且所有改动都会留下积分流水，
#    便于随时对账与回滚。生产环境可用 ENABLE_ADMIN_TEST_API=0 一次性关闭。


def _test_api_enabled() -> bool:
    return (os.getenv("ENABLE_ADMIN_TEST_API", "1") or "1").strip() != "0"


class PointsAdjustReq(BaseModel):
    userId: str | None = None
    # 二选一：amount = 增量（可为负）；balance = 直接设为该值
    amount: int | None = None
    balance: int | None = None
    note: str | None = None


@router.post("/admin/points/adjust")
async def adjust_points(
    payload: PointsAdjustReq,
    request: Request,
    authorization: str | None = Header(default=None),
):
    """测试用：增减/设定积分余额。

    走正规流水（reason_code=ADMIN_ADJUST），因此余额快照与流水始终一致，
    客户端「积分流水」页也能看到这笔调整。
    """
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    if not _test_api_enabled():
        return business_error(40301, "测试接口已关闭（ENABLE_ADMIN_TEST_API=0）", None, trace_id, http_status=403)

    gamification = get_gamification()
    target = (payload.userId or "").strip() or auth.user_id
    gamification.touch_user(target)

    if payload.amount is None and payload.balance is None:
        return business_error(CODE_INVALID_PARAM, "amount 与 balance 至少提供一个", None, trace_id, http_status=400)

    try:
        if payload.balance is not None:
            reward = gamification.points.set_balance(target, payload.balance, note=payload.note)
        else:
            reward = gamification.points.adjust_balance(target, int(payload.amount or 0), note=payload.note)
    except InsufficientPointsError:
        return business_error(
            CODE_INVALID_PARAM,
            "扣减后余额会变成负数，请检查 amount",
            {"balance": gamification.points.get_balance(target)},
            trace_id,
            http_status=400,
        )
    except ValueError as exc:
        return business_error(CODE_INVALID_PARAM, str(exc), None, trace_id, http_status=400)

    data = {
        "userId": target,
        "balance": gamification.points.get_balance(target),
        "change": reward.entry.get("change_amount"),
        "ledgerId": reward.entry.get("id"),
        "reasonCode": reward.entry.get("reason_code"),
    }
    if gamification.interaction is not None:
        try:
            await gamification.interaction.push_event(
                target, points_changed_event(reward.entry, balance=data["balance"])
            )
        except Exception:
            pass
    return ok(data, trace_id, message="积分已调整")


class ProgressBackfillReq(BaseModel):
    userId: str | None = None
    days: int = 3
    awardPoints: bool = False
    date: str | None = None  # 指定单日（YYYY-MM-DD）时忽略 days


@router.post("/admin/progress/backfill")
async def backfill_progress(
    payload: ProgressBackfillReq,
    request: Request,
    authorization: str | None = Header(default=None),
):
    """测试用：把最近 N 天（含今天）标记为「有效对话」，免等天数即可验证等级 / 断签 / 补签。

    默认**不发放积分**（不写流水），只改连续陪伴天数与等级；需要验证积分发放时传 awardPoints=true。
    """
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    if not _test_api_enabled():
        return business_error(40301, "测试接口已关闭（ENABLE_ADMIN_TEST_API=0）", None, trace_id, http_status=403)

    gamification = get_gamification()
    target = (payload.userId or "").strip() or auth.user_id

    if payload.date:
        try:
            anchor = parse_business_date(payload.date)
        except ValueError:
            return business_error(CODE_INVALID_PARAM, "date 需为 YYYY-MM-DD", None, trace_id, http_status=400)
        days = (get_business_today() - anchor).days + 1
        if days <= 0:
            return business_error(CODE_INVALID_PARAM, "date 不能晚于今天", None, trace_id, http_status=400)
    else:
        days = int(payload.days)
        if days <= 0 or days > 400:
            return business_error(CODE_INVALID_PARAM, "days 需在 1~400 之间", None, trace_id, http_status=400)

    data = gamification.backfill_activity(
        target, days=days, award_points=bool(payload.awardPoints)
    )
    if gamification.interaction is not None and data["level"].get("changeType") not in (None, "NONE"):
        try:
            await gamification.interaction.push_event(
                target, {"type": "level.changed", "payload": {**data["level"], "timestamp": int(time.time() * 1000)}}
            )
        except Exception:
            pass
    return ok(data, trace_id, message="已构造连续陪伴天数")


class ProgressResetReq(BaseModel):
    userId: str | None = None
    clearMakeupCards: bool = False


@router.post("/admin/progress/reset")
async def reset_progress(
    payload: ProgressResetReq,
    request: Request,
    authorization: str | None = Header(default=None),
):
    """测试用：清空每日有效对话流水与等级（**保留积分余额**），用于反复验证升级 / 断签 / 补签。"""
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    if not _test_api_enabled():
        return business_error(40301, "测试接口已关闭（ENABLE_ADMIN_TEST_API=0）", None, trace_id, http_status=403)

    gamification = get_gamification()
    target = (payload.userId or "").strip() or auth.user_id
    data = gamification.reset_progress(target, clear_makeup_cards=bool(payload.clearMakeupCards))
    return ok(data, trace_id, message="等级与连续天数已重置（积分保留）")


@router.get("/admin/interaction-prompt-preview")
async def interaction_prompt_preview(
    request: Request,
    itemId: str = "coffee",
    userId: str | None = None,
    text: str | None = None,
    authorization: str | None = Header(default=None),
):
    """查看某个互动物品**实际发给 DeepSeek 的完整 messages**（含人设 prompt 与历史）。

    用于运营改完 Prompt 模板后立刻确认效果，不需要真发一次互动；
    `text` 可附带一段附言，预览 O5「文字 + 礼物」的组装结果。
    """
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    handler = getattr(gamification, "interaction", None)
    if handler is None:
        return business_error(50301, "互动服务未就绪", None, trace_id, http_status=503)
    target = (userId or "").strip() or auth.user_id
    preview = await handler.build_prompt_preview(
        target, itemId, attachment=(text or "").strip()
    )
    if preview is None:
        return business_error(CODE_ITEM_NOT_FOUND, "物品不存在或已下架", None, trace_id, http_status=404)
    return ok(preview, trace_id)
