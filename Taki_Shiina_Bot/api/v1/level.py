"""
等级路由（PRD §8 / FR-15）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /api/v1/level/status | 查询当前等级、连续陪伴天数、升级进度 |
| GET | /api/v1/level/config | 查询等级阈值表（供客户端渲染进度条与说明页） |
"""

from __future__ import annotations

from fastapi import APIRouter, Header, Request

from api.v1.deps import get_gamification, ok, require_auth, resolve_trace_id

router = APIRouter()


@router.get("/level/status")
async def level_status(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    auth = require_auth(authorization, trace_id)
    gamification = get_gamification()
    return ok(gamification.get_level_status(auth.user_id), trace_id)


@router.get("/level/config")
async def level_config(request: Request, authorization: str | None = Header(default=None)):
    trace_id = resolve_trace_id(request)
    require_auth(authorization, trace_id)
    gamification = get_gamification()
    rules = gamification.config.get_points_rules()
    return ok(
        {
            "levels": gamification.config.get_level_table(),
            "defaultLevelCode": "NONE",
            # EDGE-7：NONE 态不设专属文案，客户端与“从未升级过的新用户”共用同一套默认展示
            "defaultLevelName": "",
            "makeupCardMax": rules.get("makeup_card", {}).get("max_available"),
            "breakGapDays": rules.get("break_gap_days"),
            "warningGapDays": rules.get("warning_gap_days"),
        },
        trace_id,
    )
