"""
互动积分 · 等级体系 —— 积分服务

对应 PRD：

- 4.3 积分机制（每日首次 +1 / 连续 3 天 +3 循环 / 断签只清等级不动积分 / 纪念日叠加）
- FR-3 扣分与流水、FR-9 失败退款并记 ITEM_REFUND
- FR-11 余额与流水可查、FR-12 服务端持久化、FR-13 幂等键防重放（EDGE-1 并发安全）
- FR-17 与等级计数器彻底解耦：积分余额存在独立文件，本模块只按业务语义调用等级服务，
  断签逻辑不会写到 ``points_account``
- FR-10 只有“用户主动发起”的对话计入每日首次（系统早晚安推送不经过本模块）
- FR-19 / FR-21 补签卡发放与使用不产生积分流水（补签卡逻辑在 makeup_card_service）

并发安全（EDGE-1）：扣分在 ``PointsStore.transaction()`` 内完成“读余额 → 判断 → 写流水+写余额”，
跨进程有 ``fcntl.flock``、进程内有可重入锁，等价于
``UPDATE points_account SET balance = balance - :cost WHERE user_id=:uid AND balance >= :cost``
的原子语义。
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import date
from typing import Any

from time_utils import get_business_today
from services.level_service import (
    SOURCE_ACTIVITY,
    SOURCE_INTERACTION,
    LevelChange,
    LevelService,
)
from services.progress_store import (
    REASON_ADMIN_ADJUST,
    REASON_ANNIVERSARY,
    REASON_DAILY_FIRST_CHAT,
    REASON_ITEM_REFUND,
    REASON_ITEM_SEND,
    REASON_STREAK_3_DAY,
    SOURCE_CHAT,
    SOURCE_INTERACTION as ACTIVITY_SOURCE_INTERACTION,
    PointsStore,
    ProgressStore,
)


def uuid_hex() -> str:
    """幂等键后缀：手动调整每次都必须唯一（不可重复）。"""
    return uuid.uuid4().hex

_SOURCE_TO_CHANGE_SOURCE = {
    SOURCE_CHAT: SOURCE_ACTIVITY,
    ACTIVITY_SOURCE_INTERACTION: SOURCE_INTERACTION,
}


class InsufficientPointsError(RuntimeError):
    """积分不足（PRD FR-3 明确错误码场景）。"""


@dataclass
class RewardResult:
    entry: dict[str, Any]
    created: bool

    @property
    def amount(self) -> int:
        return int(self.entry.get("change_amount", 0))


@dataclass
class ActivityResult:
    user_id: str
    activity_date: str
    source: str
    new_activity: bool
    rewards: list[dict[str, Any]] = field(default_factory=list)
    balance: int = 0
    balance_delta: int = 0
    milestones: list[dict[str, Any]] = field(default_factory=list)
    level: LevelChange | None = None


class PointsService:
    def __init__(
        self,
        points_store: PointsStore,
        progress_store: ProgressStore,
        level_service: LevelService,
        config_service,
        logger=None,
    ):
        self.points_store = points_store
        self.progress_store = progress_store
        self.level_service = level_service
        self.config_service = config_service
        self.logger = logger

    # ==================== 查询（FR-11） ====================
    def get_balance(self, user_id: str) -> int:
        data = self.points_store.snapshot()
        return PointsStore.get_balance(data, user_id)

    def get_balance_detail(self, user_id: str) -> dict[str, Any]:
        data = self.points_store.snapshot()
        account = data["points_account"].get(user_id) or {}
        return {
            "balance": PointsStore.get_balance(data, user_id),
            "updatedAt": account.get("updated_at"),
        }

    def get_history(
        self,
        user_id: str,
        page: int = 1,
        page_size: int = 20,
        reason_code: str | None = None,
    ) -> dict[str, Any]:
        data = self.points_store.snapshot()
        entries = [
            entry
            for entry in data["points_ledger"]
            if entry.get("user_id") == user_id
            and (not reason_code or entry.get("reason_code") == reason_code)
        ]
        entries.sort(key=lambda entry: int(entry.get("id", 0)), reverse=True)
        total = len(entries)
        page = max(1, int(page or 1))
        page_size = max(1, min(int(page_size or 20), 100))
        start = (page - 1) * page_size
        items = [dict(entry) for entry in entries[start : start + page_size]]
        return {
            "items": items,
            "total": total,
            "page": page,
            "pageSize": page_size,
            "hasMore": start + len(items) < total,
        }

    def find_ledger_entry(self, user_id: str, idempotency_key: str) -> dict[str, Any] | None:
        data = self.points_store.snapshot()
        entry = PointsStore.find_by_idempotency(data, idempotency_key)
        if entry and entry.get("user_id") == user_id:
            return dict(entry)
        return None

    # ==================== 幂等发放 ====================
    def _award(
        self,
        user_id: str,
        amount: int,
        reason_code: str,
        idempotency_key: str,
        business_date: str | None,
        related_item_id: str | None = None,
        note: str | None = None,
    ) -> RewardResult:
        with self.points_store.transaction() as data:
            existed = PointsStore.find_by_idempotency(data, idempotency_key) is not None
            entry = PointsStore.append_ledger(
                data,
                user_id=user_id,
                change_amount=amount,
                reason_code=reason_code,
                idempotency_key=idempotency_key,
                related_item_id=related_item_id,
                business_date=business_date,
                note=note,
            )
        return RewardResult(entry=dict(entry), created=not existed)

    # ==================== 扣分 / 退款（互动，FR-3 / FR-9） ====================
    def deduct_for_item(
        self,
        user_id: str,
        item_id: str,
        cost_points: int,
        idempotency_key: str,
        business_date: str | None = None,
    ) -> RewardResult:
        """原子扣减积分并记 ITEM_SEND 流水；余额不足抛 InsufficientPointsError。"""
        if cost_points <= 0:
            raise ValueError("cost_points 必须为正数")
        key = f"item_send:{user_id}:{idempotency_key}"
        with self.points_store.transaction() as data:
            existing = PointsStore.find_by_idempotency(data, key)
            if existing is not None:
                return RewardResult(entry=dict(existing), created=False)
            try:
                entry = PointsStore.append_ledger(
                    data,
                    user_id=user_id,
                    change_amount=-int(cost_points),
                    reason_code=REASON_ITEM_SEND,
                    idempotency_key=key,
                    related_item_id=item_id,
                    business_date=business_date,
                )
            except ValueError as exc:
                raise InsufficientPointsError(str(exc)) from exc
        return RewardResult(entry=dict(entry), created=True)

    def refund_item(
        self,
        user_id: str,
        item_id: str,
        amount: int,
        idempotency_key: str,
        business_date: str | None = None,
        note: str | None = None,
    ) -> RewardResult:
        """互动最终失败时退回已扣积分（PRD FR-9 / 6.3：记一条“失败退回”流水）。"""
        key = f"item_refund:{user_id}:{idempotency_key}"
        return self._award(
            user_id=user_id,
            amount=abs(int(amount)),
            reason_code=REASON_ITEM_REFUND,
            idempotency_key=key,
            business_date=business_date,
            related_item_id=item_id,
            note=note,
        )

    # ==================== 每日有效对话与积分发放（FR-10 / 4.3） ====================
    def record_user_activity(
        self,
        user_id: str,
        source: str = SOURCE_CHAT,
        today: date | None = None,
        ensure_daily_first: bool = True,
    ) -> ActivityResult:
        """记录一次“用户主动发起”的有效对话，并结算当天应得的积分。

        - 写入 / 命中 ``daily_activity_log``（唯一约束 (user_id, activity_date)，幂等）
        - 依据 5.1a 重算连续陪伴天数与等级（可触发升级 / 回落）
        - 发放 ``DAILY_FIRST_CHAT``（+1）与循环触发的 ``STREAK_3_DAY``（+3），
          每条规则各自独立流水、独立幂等键（EDGE-4）
        """
        target_day = today or get_business_today()
        date_str = target_day.isoformat()
        rules = self.config_service.get_points_rules()

        with self.progress_store.transaction() as data:
            ProgressStore.ensure_progress(data, user_id)
            new_activity = ProgressStore.record_activity(data, user_id, date_str, source)

        level_change = self.level_service.recompute(
            user_id,
            today=target_day,
            change_source=_SOURCE_TO_CHANGE_SOURCE.get(source, SOURCE_ACTIVITY),
        )

        result = ActivityResult(
            user_id=user_id,
            activity_date=date_str,
            source=source,
            new_activity=new_activity,
            level=level_change,
        )

        if ensure_daily_first:
            daily_points = int(rules.get("daily_first_chat_points", 1))
            if daily_points > 0:
                reward = self._award(
                    user_id=user_id,
                    amount=daily_points,
                    reason_code=REASON_DAILY_FIRST_CHAT,
                    idempotency_key=f"daily_first_chat:{user_id}:{date_str}",
                    business_date=date_str,
                )
                if reward.created:
                    result.rewards.append(dict(reward.entry))

        cycle_days = int(rules.get("streak_cycle_days", 3))
        streak_points = int(rules.get("streak_reward_points", 3))
        if cycle_days > 0 and streak_points > 0:
            result.milestones = self.settle_streak_rewards(
                user_id=user_id,
                streak_dates=level_change.streak_dates,
                cycle_days=cycle_days,
                streak_points=streak_points,
                collected=result.rewards,
            )

        result.balance_delta = sum(int(entry.get("change_amount", 0)) for entry in result.rewards)
        result.balance = self.get_balance(user_id)
        return result

    def settle_streak_rewards(
        self,
        user_id: str,
        streak_dates: list[str],
        cycle_days: int | None = None,
        streak_points: int | None = None,
        collected: list[dict[str, Any]] | None = None,
    ) -> list[dict[str, Any]]:
        """结算“连续 3 天对话 +3、每满 3 天循环触发”的奖励（4.3 / FR-3）。

        幂等键绑定到里程碑当天日期，因此补签回溯导致连续天数跳变时不会重复发放，
        而过去确实未发放过的里程碑会被补齐。
        """
        rules = self.config_service.get_points_rules()
        cycle_days = int(cycle_days if cycle_days is not None else rules.get("streak_cycle_days", 3))
        streak_points = int(
            streak_points if streak_points is not None else rules.get("streak_reward_points", 3)
        )
        milestones: list[dict[str, Any]] = []
        if cycle_days <= 0 or streak_points <= 0:
            return milestones
        for milestone_days, milestone_date in self.level_service.streak_threshold_dates(
            streak_dates, cycle_days
        ):
            reward = self._award(
                user_id=user_id,
                amount=streak_points,
                reason_code=REASON_STREAK_3_DAY,
                idempotency_key=f"streak_3:{user_id}:{milestone_date}",
                business_date=milestone_date,
                note=f"连续 {milestone_days} 天",
            )
            milestones.append(
                {"days": milestone_days, "date": milestone_date, "created": reward.created}
            )
            if reward.created and collected is not None:
                collected.append(dict(reward.entry))
        return milestones

    # ==================== 测试/运维：手动调整余额 ====================
    def adjust_balance(
        self,
        user_id: str,
        delta: int,
        note: str | None = None,
        business_date: str | None = None,
    ) -> RewardResult:
        """按增量调整积分余额，并记一条 ``ADMIN_ADJUST`` 流水（保持账实一致、可对账）。

        仅用于测试与运维补偿：走的是同一条只增不改的流水，``balance_after`` 快照仍然准确。
        余额不足（结果为负）时抛 ``InsufficientPointsError``。
        """
        amount = int(delta)
        if amount == 0:
            raise ValueError("delta 不能为 0")
        key = f"admin_adjust:{user_id}:{uuid_hex()}"
        with self.points_store.transaction() as data:
            try:
                entry = PointsStore.append_ledger(
                    data,
                    user_id=user_id,
                    change_amount=amount,
                    reason_code=REASON_ADMIN_ADJUST,
                    idempotency_key=key,
                    business_date=business_date or get_business_today().isoformat(),
                    note=note or ("测试调整" if amount > 0 else "测试扣减"),
                )
            except ValueError as exc:
                raise InsufficientPointsError(str(exc)) from exc
        return RewardResult(entry=dict(entry), created=True)

    def set_balance(
        self,
        user_id: str,
        balance: int,
        note: str | None = None,
        business_date: str | None = None,
    ) -> RewardResult:
        """把余额设为指定值（同样落一条差额流水，便于对账）。"""
        target = int(balance)
        if target < 0:
            raise ValueError("balance 不能为负数")
        current = self.get_balance(user_id)
        if target == current:
            raise ValueError("余额未变化")
        return self.adjust_balance(
            user_id,
            delta=target - current,
            note=note or f"测试设为 {target}",
            business_date=business_date,
        )

    # ==================== 纪念日（4.3 / EDGE-4） ====================
    def grant_anniversary(
        self,
        user_id: str,
        anniversary_date: date,
        name: str,
        points: int,
    ) -> RewardResult:
        """纪念日奖励：每年固定触发、与当日其他规则叠加、每条规则独立流水。

        幂等键绑定 (用户, 自然日)，同一天重复调度不会重复发放。
        """
        date_str = anniversary_date.isoformat()
        return self._award(
            user_id=user_id,
            amount=int(points),
            reason_code=REASON_ANNIVERSARY,
            idempotency_key=f"anniversary:{user_id}:{date_str}",
            business_date=date_str,
            note=name,
        )
