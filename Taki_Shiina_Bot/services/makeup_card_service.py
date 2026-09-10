"""
互动积分 · 等级体系 —— 断签保护：补签卡

对应 PRD 4.5 / 5.4 / 6.2 与 FR-19 ~ FR-22：

- FR-19 每位用户每月自动发放 1 张；可用张数达到上限（默认 12 张）时当月暂停发放，
  待用户使用消耗、可用数量低于上限后自动恢复发放
- FR-20 需用户**主动点击使用**，系统不自动代为使用
- FR-21 可跨月结转、当月未用不作废，一卡一条库存记录，可用数量 = 未使用记录数
- FR-22 可回溯任意历史缺口日期补签，断签已实际发生后仍可追溯挽回
- 6.2 校验：① 存在 AVAILABLE 卡；② 目标日期在 ``daily_activity_log`` 中确实缺失

使用一张卡即在 ``daily_activity_log`` 插入 ``source=MAKEUP_CARD`` 记录，
再按 5.1a 重新推算 ``continuous_days`` 并重映射 ``level_code``（可能一次性跳级）。
补签卡自身**不产生任何积分流水**（PRD 5.3 注），但补签后重新成立的连续天数
若跨过 +3 里程碑且历史上未发放过，会补发该里程碑奖励。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, timedelta
from typing import Any

from time_utils import get_business_month_str, get_business_today, parse_business_date
from services.level_service import SOURCE_MAKEUP_CARD, LevelChange, LevelService
from services.progress_store import (
    CARD_AVAILABLE,
    CARD_USED,
    ProgressStore,
)

ERROR_CARD_NOT_ENOUGH = "MAKEUP_CARD_NOT_ENOUGH"
ERROR_DATE_ALREADY_ACTIVE = "DATE_ALREADY_ACTIVE"
ERROR_INVALID_TARGET_DATE = "INVALID_TARGET_DATE"
ERROR_TARGET_IN_FUTURE = "TARGET_DATE_IN_FUTURE"


@dataclass
class UseCardResult:
    ok: bool
    error_code: str | None = None
    message: str = ""
    user_id: str = ""
    target_date: str | None = None
    card: dict[str, Any] | None = None
    available_cards: int = 0
    level: LevelChange | None = None
    rewards: list[dict[str, Any]] = field(default_factory=list)
    milestones: list[dict[str, Any]] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        payload: dict[str, Any] = {
            "success": self.ok,
            "availableCards": self.available_cards,
        }
        if self.error_code:
            payload["errorCode"] = self.error_code
            payload["message"] = self.message
        if self.target_date:
            payload["targetDate"] = self.target_date
        if self.card:
            payload["card"] = self.card
        if self.level:
            payload["level"] = self.level.to_status_dict()
        if self.rewards:
            payload["rewards"] = self.rewards
        if self.milestones:
            payload["milestones"] = self.milestones
        return payload


class MakeupCardService:
    def __init__(self, progress_store: ProgressStore, level_service: LevelService, config_service, logger=None):
        self.progress_store = progress_store
        self.level_service = level_service
        self.config_service = config_service
        self.logger = logger
        # 由装配方注入，避免 points_service ↔ makeup_card_service 循环依赖
        self.points_service = None

    # ==================== 配置 ====================
    def _card_rules(self) -> dict[str, Any]:
        rules = self.config_service.get_points_rules().get("makeup_card", {})
        return {
            "monthly_grant": int(rules.get("monthly_grant", 1) or 0),
            "max_available": int(rules.get("max_available", 12) or 0),
            "grant_on_first_seen": bool(rules.get("grant_on_first_seen", True)),
        }

    # ==================== 查询 ====================
    def get_available_count(self, user_id: str) -> int:
        data = self.progress_store.snapshot()
        ProgressStore.ensure_progress(data, user_id)
        return ProgressStore.available_card_count(data, user_id)

    def get_summary(self, user_id: str) -> dict[str, Any]:
        rules = self._card_rules()
        data = self.progress_store.snapshot()
        meta = data["meta"]
        cards = ProgressStore.user_cards(data, user_id)
        available = sum(1 for card in cards if card.get("status") == CARD_AVAILABLE)
        used = sum(1 for card in cards if card.get("status") == CARD_USED)
        return {
            "available": available,
            "used": used,
            "totalGranted": len(cards),
            "maxAvailable": rules["max_available"],
            "monthlyGrant": rules["monthly_grant"],
            "lastGrantedMonth": meta.get("makeup_grant_months", {}).get(user_id),
            "currentMonthGranted": meta.get("makeup_grant_months", {}).get(user_id)
            == get_business_month_str(),
            "atLimit": available >= rules["max_available"],
        }

    def get_history(self, user_id: str, page: int = 1, page_size: int = 20) -> dict[str, Any]:
        data = self.progress_store.snapshot()
        cards = sorted(
            ProgressStore.user_cards(data, user_id),
            key=lambda card: (int(card.get("created_at", 0)), int(card.get("id", 0))),
            reverse=True,
        )
        total = len(cards)
        page = max(1, int(page or 1))
        page_size = max(1, min(int(page_size or 20), 100))
        start = (page - 1) * page_size
        items = [dict(card) for card in cards[start : start + page_size]]
        return {
            "items": items,
            "total": total,
            "page": page,
            "pageSize": page_size,
            "hasMore": start + len(items) < total,
            "summary": self.get_summary(user_id),
        }

    def get_candidates(self, user_id: str, today: date | None = None, limit: int = 120) -> dict[str, Any]:
        """可补签日期候选：从用户首条有效记录到今天之间所有缺失的自然日（FR-22 任意历史缺口）。

        仅供客户端展示选择列表，服务端 ``use_card`` 仍接受任意合法历史日期。
        """
        today = today or get_business_today()
        data = self.progress_store.snapshot()
        dates = ProgressStore.activity_dates(data, user_id)
        if not dates:
            return {"items": [], "available": self.get_available_count(user_id), "total": 0}
        first = parse_business_date(dates[0])
        existing = set(dates)
        candidates: list[dict[str, Any]] = []
        cursor = today
        while cursor >= first:
            if cursor.isoformat() not in existing:
                candidates.append(
                    {"date": cursor.isoformat(), "daysAgo": (today - cursor).days}
                )
            cursor -= timedelta(days=1)
        total = len(candidates)
        return {
            "items": candidates[: max(1, int(limit))],
            "total": total,
            "firstActivityDate": first.isoformat(),
            "available": self.get_available_count(user_id),
        }

    # ==================== 发放 ====================
    def grant_for_month(self, user_id: str, month: str | None = None) -> dict[str, Any] | None:
        """为指定月份发放补签卡（月度定时任务与“新用户当月立即发放”共用）。

        返回新发放的卡记录；本月已发放或已达上限时返回 None。
        """
        month = month or get_business_month_str()
        rules = self._card_rules()
        if rules["monthly_grant"] <= 0:
            return None
        with self.progress_store.transaction() as data:
            ProgressStore.ensure_progress(data, user_id)
            meta = data["meta"]
            granted_months = meta.setdefault("makeup_grant_months", {})
            if granted_months.get(user_id) == month:
                return None
            available = ProgressStore.available_card_count(data, user_id)
            if available >= rules["max_available"]:
                # FR-19 / FR-21：达到上限则暂停该月发放，不覆盖也不作废已有卡片；
                # 不写 granted_months，待用户消耗后由后续调度自动补发。
                return None
            card = ProgressStore.grant_card(data, user_id, month)
            granted_months[user_id] = month
        if self.logger:
            self.logger.info(f"[补签卡] 已发放: user={user_id}, month={month}, cardId={card['id']}")
        return dict(card)

    def ensure_initial_grant(self, user_id: str) -> dict[str, Any] | None:
        """新用户首次出现时按需求方确认的规则立即发放当月补签卡。"""
        if not self._card_rules()["grant_on_first_seen"]:
            return None
        return self.grant_for_month(user_id, get_business_month_str())

    # ==================== 使用（6.2 流程） ====================
    def use_card(self, user_id: str, target_date: str, today: date | None = None) -> UseCardResult:
        today = today or get_business_today()
        try:
            parsed_target = parse_business_date(target_date)
        except ValueError:
            return UseCardResult(
                ok=False,
                error_code=ERROR_INVALID_TARGET_DATE,
                message="target_date 需为 YYYY-MM-DD 格式",
                user_id=user_id,
                target_date=target_date,
            )

        if parsed_target > today:
            return UseCardResult(
                ok=False,
                error_code=ERROR_TARGET_IN_FUTURE,
                message="不能为未来日期补签",
                user_id=user_id,
                target_date=parsed_target.isoformat(),
            )

        target_str = parsed_target.isoformat()
        consumed_card: dict[str, Any] | None = None
        with self.progress_store.transaction() as data:
            ProgressStore.ensure_progress(data, user_id)
            # 校验②：目标日期确实缺失（防止重复补签同一天）
            if ProgressStore.has_activity(data, user_id, target_str):
                return UseCardResult(
                    ok=False,
                    error_code=ERROR_DATE_ALREADY_ACTIVE,
                    message="该日期已有有效对话记录，无需补签",
                    user_id=user_id,
                    target_date=target_str,
                    available_cards=ProgressStore.available_card_count(data, user_id),
                )
            # 校验①：存在可用补签卡
            if ProgressStore.available_card_count(data, user_id) <= 0:
                return UseCardResult(
                    ok=False,
                    error_code=ERROR_CARD_NOT_ENOUGH,
                    message="补签卡不足",
                    user_id=user_id,
                    target_date=target_str,
                    available_cards=0,
                )
            consumed_card = ProgressStore.consume_card(data, user_id, target_str)
            ProgressStore.record_activity(data, user_id, target_str, SOURCE_MAKEUP_CARD)

        # 重新推算连续天数与等级（5.1a / 6.2 第 4 步）
        level_change = self.level_service.recompute(
            user_id, today=today, change_source=SOURCE_MAKEUP_CARD, persist=True
        )

        result = UseCardResult(
            ok=True,
            user_id=user_id,
            target_date=target_str,
            card=dict(consumed_card) if consumed_card else None,
            level=level_change,
        )

        if self.points_service is not None:
            result.milestones = self.points_service.settle_streak_rewards(
                user_id=user_id,
                streak_dates=level_change.streak_dates,
                collected=result.rewards,
            )

        result.available_cards = self.get_available_count(user_id)
        if self.logger:
            self.logger.info(
                f"[补签卡] 已使用: user={user_id}, target={target_str}, "
                f"continuousDays={level_change.continuous_days}, level={level_change.level_code}"
            )
        return result
