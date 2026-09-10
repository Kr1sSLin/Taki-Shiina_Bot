"""
互动积分 · 等级体系 —— WebSocket 事件构造

统一构造推送事件，保证「升级 / 断签 / 积分变动」等通知在下发格式上完全一致。
所有事件都通过既有 WS 通道发往该账号的**全部在线设备**（PRD 3.3 / FR-16 / FR-18 / EDGE-8，不做活跃设备过滤）。
"""

from __future__ import annotations

import time
from typing import Any

from services.level_service import LevelChange


def _timestamp() -> int:
    return int(time.time() * 1000)


def points_changed_event(entry: dict[str, Any], balance: int | None = None) -> dict[str, Any]:
    """积分变动（PRD 4.3 / EDGE-4：每条规则独立事件，不合并金额）。"""
    balance_after = entry.get("balance_after")
    return {
        "type": "points.changed",
        "payload": {
            "ledgerId": entry.get("id"),
            "reasonCode": entry.get("reason_code"),
            "changeAmount": entry.get("change_amount"),
            "balanceAfter": balance_after,
            "balance": balance if balance is not None else balance_after,
            "relatedItemId": entry.get("related_item_id"),
            "businessDate": entry.get("business_date"),
            "note": entry.get("note"),
            "timestamp": _timestamp(),
        },
    }


def level_changed_event(change: LevelChange) -> dict[str, Any]:
    """等级变化：UPGRADE（首次达成，庆祝）/ RESTORE（补签回溯挽回，克制文案）/ RESET（断签回落）。"""
    payload = change.to_event_payload()
    payload["timestamp"] = _timestamp()
    return {"type": "level.changed", "payload": payload}


def streak_warning_event(change: LevelChange, remaining_days: int) -> dict[str, Any]:
    """FR-18 断签提前提醒（连续 3～4 天未对话时触发）。"""
    return {
        "type": "streak.warning",
        "payload": {
            "levelCode": change.level_code,
            "levelName": change.level_name,
            "continuousDays": change.continuous_days,
            "gapDays": change.gap_days,
            "remainingDays": remaining_days,
            "deadlineDate": change.break_deadline_date,
            "timestamp": _timestamp(),
        },
    }


def makeup_card_changed_event(summary: dict[str, Any], reason: str) -> dict[str, Any]:
    """补签卡数量变化（发放 / 使用），用于多端同步库存展示。"""
    return {
        "type": "makeup_card.changed",
        "payload": {
            "reason": reason,
            "available": summary.get("available"),
            "used": summary.get("used"),
            "totalGranted": summary.get("totalGranted"),
            "maxAvailable": summary.get("maxAvailable"),
            "lastGrantedMonth": summary.get("lastGrantedMonth"),
            "timestamp": _timestamp(),
        },
    }

def points_summary_event(balance: int, user_id: str) -> dict[str, Any]:
    """余额全量同步事件（登录/重连场景使用）。"""
    return {
        "type": "points.snapshot",
        "payload": {"userId": user_id, "balance": balance, "timestamp": _timestamp()},
    }
