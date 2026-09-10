"""
互动积分 · 等级体系 —— 连续陪伴天数推算与等级判定

对应 PRD：

- 4.4 等级阈值表、FR-14 自动判定解锁、FR-15 展示当前等级与升级进度
- FR-17 等级与积分余额彻底解耦：本模块**只读** ``daily_activity_log``，
  只写 ``user_progress``，绝不触碰 ``points_account`` / ``points_ledger``
- 5.1a ``continuous_days`` 的推算规则（任意历史缺口可被补签卡填补后重新推算）
- 6.1 状态机（含 NONE → 高等级的一次性跳级）
- EDGE-7 等级回退至 NONE 时不设专属文案，与新用户共用默认展示

推算规则（严格按 5.1a）：

1. ``continuous_days`` = 从 ``daily_activity_log`` 里最大的 ``activity_date`` 开始向过去逐日回溯，
   遇到第一个缺失自然日为止的长度（``CHAT`` / ``INTERACTION`` / ``MAKEUP_CARD`` 均计入）。
2. 断签判定 = 最近一条有效记录距“今天”的自然日间隔 ≥ ``break_gap_days``（默认 5，PRD 4.3 / EDGE-3）。
   一旦断签，``continuous_days`` 视为 0、``level_code`` 回落 ``NONE``；
   若之后用补签卡填补历史缺口，重新推算即可跳回缺口发生前的等级（6.1）。
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from datetime import date, timedelta
from typing import Any

from time_utils import get_business_today, parse_business_date
from services.progress_store import LEVEL_NONE, ProgressStore

CHANGE_UPGRADE = "UPGRADE"
CHANGE_RESTORE = "RESTORE"
CHANGE_RESET = "RESET"
CHANGE_NONE = "NONE"

SOURCE_ACTIVITY = "ACTIVITY"
SOURCE_INTERACTION = "INTERACTION"
SOURCE_MAKEUP_CARD = "MAKEUP_CARD"
SOURCE_BREAK_SCAN = "BREAK_SCAN"
SOURCE_QUERY = "QUERY"
# 测试/运维通过 admin 接口构造的进度变化
SOURCE_ADMIN = "ADMIN"

DEFAULT_LEVEL_NAME = ""  # EDGE-7：NONE 态不设专属文案，由客户端共用默认展示


@dataclass
class LevelChange:
    user_id: str
    prev_level_code: str
    level_code: str
    level_name: str
    continuous_days: int
    change_type: str
    change_source: str
    level_changed: bool
    highest_level_code: str
    last_valid_date: str | None = None
    gap_days: int | None = None
    break_deadline_date: str | None = None
    break_pending: bool = False
    next_level_code: str | None = None
    next_level_name: str | None = None
    next_level_threshold_days: int | None = None
    days_to_next_level: int | None = None
    level_updated_at: int | None = None
    streak_dates: list[str] = field(default_factory=list)

    def to_event_payload(self) -> dict[str, Any]:
        return {
            "levelCode": self.level_code,
            "levelName": self.level_name,
            "prevLevelCode": self.prev_level_code,
            "continuousDays": self.continuous_days,
            "changeType": self.change_type,
            "changeSource": self.change_source,
            "highestLevelCode": self.highest_level_code,
            "lastValidDate": self.last_valid_date,
            "gapDays": self.gap_days,
            "breakDeadlineDate": self.break_deadline_date,
            "nextLevelCode": self.next_level_code,
            "nextLevelName": self.next_level_name,
            "nextLevelThresholdDays": self.next_level_threshold_days,
            "daysToNextLevel": self.days_to_next_level,
            "levelUpdatedAt": self.level_updated_at,
        }

    def to_status_dict(self) -> dict[str, Any]:
        payload = self.to_event_payload()
        payload.update(
            {
                "isDefaultLevel": self.level_code == LEVEL_NONE,
                "streakDates": self.streak_dates,
            }
        )
        return payload


class LevelService:
    def __init__(self, progress_store: ProgressStore, config_service, logger=None):
        self.progress_store = progress_store
        self.config_service = config_service
        self.logger = logger

    # ==================== 基础映射 ====================
    def level_table(self) -> list[dict[str, Any]]:
        return self.config_service.get_level_table()

    def resolve_level(self, continuous_days: int) -> dict[str, Any]:
        """continuous_days → 等级；未达最低阈值（<3 天）时返回默认态 NONE。"""
        days = max(0, int(continuous_days or 0))
        matched: dict[str, Any] | None = None
        for level in self.level_table():
            if days >= int(level["threshold_days"]):
                matched = level
            else:
                break
        if matched is None:
            return {
                "level_code": LEVEL_NONE,
                "level_name": DEFAULT_LEVEL_NAME,
                "threshold_days": 0,
                "rank": -1,
            }
        return {
            "level_code": matched["level_code"],
            "level_name": matched["level_name"],
            "threshold_days": int(matched["threshold_days"]),
            "rank": self.level_rank(matched["level_code"]),
        }

    def level_rank(self, level_code: str) -> int:
        if not level_code or level_code == LEVEL_NONE:
            return -1
        for index, level in enumerate(self.level_table()):
            if level["level_code"] == level_code:
                return index
        return -1

    def next_level(self, continuous_days: int) -> dict[str, Any] | None:
        days = max(0, int(continuous_days or 0))
        for level in self.level_table():
            if days < int(level["threshold_days"]):
                return level
        return None

    # ==================== 连续天数推算（5.1a） ====================
    def compute_streak(self, activity_dates: list[str], today: date | None = None) -> dict[str, Any]:
        """按 5.1a 规则推算连续陪伴天数与断签状态。"""
        today = today or get_business_today()
        break_gap_days = int(self.config_service.get_points_rules().get("break_gap_days", 5))
        unique_dates = sorted({value for value in activity_dates if value})
        if not unique_dates:
            return {
                "continuous_days": 0,
                "latest_date": None,
                "gap_days": None,
                "broken": False,
                "streak_dates": [],
                "break_deadline_date": None,
            }

        latest = parse_business_date(unique_dates[-1])
        gap_days = (today - latest).days
        # 防御：记录中出现未来日期（时钟回拨/脏数据）时按今天处理，避免负数间隔
        gap_days = max(0, gap_days)
        break_deadline = latest + timedelta(days=break_gap_days)
        broken = gap_days >= break_gap_days
        if broken:
            return {
                "continuous_days": 0,
                "latest_date": latest.isoformat(),
                "gap_days": gap_days,
                "broken": True,
                "streak_dates": [],
                "break_deadline_date": break_deadline.isoformat(),
            }

        date_set = set(unique_dates)
        streak_dates: list[str] = []
        cursor = latest
        while cursor.isoformat() in date_set:
            streak_dates.append(cursor.isoformat())
            cursor -= timedelta(days=1)
        streak_dates.reverse()
        return {
            "continuous_days": len(streak_dates),
            "latest_date": latest.isoformat(),
            "gap_days": gap_days,
            "broken": False,
            "streak_dates": streak_dates,
            "break_deadline_date": break_deadline.isoformat(),
        }

    # ==================== 重算与落库 ====================
    def recompute(
        self,
        user_id: str,
        today: date | None = None,
        change_source: str = SOURCE_QUERY,
        persist: bool = True,
    ) -> LevelChange:
        """依据 ``daily_activity_log`` 重算等级并回写 ``user_progress``（FR-17/FR-22）。"""
        today = today or get_business_today()

        def _derive(data: dict) -> LevelChange:
            progress = ProgressStore.ensure_progress(data, user_id)
            dates = ProgressStore.activity_dates(data, user_id)
            streak = self.compute_streak(dates, today)
            continuous_days = int(streak["continuous_days"])
            resolved = self.resolve_level(continuous_days)
            prev_level_code = str(progress.get("level_code") or LEVEL_NONE)
            highest = str(progress.get("highest_level_code") or LEVEL_NONE)
            new_level_code = resolved["level_code"]

            prev_rank = self.level_rank(prev_level_code)
            new_rank = resolved["rank"]
            if new_rank > prev_rank:
                # EDGE-11：曾经达成过的等级（多为补签回溯挽回）用“已恢复”，
                # 只有真正首次达成才走升级庆祝文案。
                change_type = CHANGE_UPGRADE if new_rank > self.level_rank(highest) else CHANGE_RESTORE
            elif new_rank < prev_rank:
                change_type = CHANGE_RESET
            else:
                change_type = CHANGE_NONE
            level_changed = change_type != CHANGE_NONE

            if new_rank > self.level_rank(highest):
                highest = new_level_code

            now_ms = int(time.time() * 1000)
            if level_changed:
                progress["level_updated_at"] = now_ms
            progress["continuous_days"] = continuous_days
            progress["level_code"] = new_level_code
            progress["highest_level_code"] = highest
            progress["last_valid_date"] = streak["latest_date"]
            progress["updated_at"] = now_ms

            upcoming = self.next_level(continuous_days)
            break_pending = bool(
                streak["gap_days"] is not None
                and not streak["broken"]
                and streak["gap_days"] >= min(
                    self.config_service.get_points_rules().get("warning_gap_days", [3, 4]) or [3]
                )
            )
            return LevelChange(
                user_id=user_id,
                prev_level_code=prev_level_code,
                level_code=new_level_code,
                level_name=resolved["level_name"],
                continuous_days=continuous_days,
                change_type=change_type,
                change_source=change_source,
                level_changed=level_changed,
                highest_level_code=highest,
                last_valid_date=streak["latest_date"],
                gap_days=streak["gap_days"],
                break_deadline_date=streak["break_deadline_date"],
                break_pending=break_pending,
                next_level_code=upcoming["level_code"] if upcoming else None,
                next_level_name=upcoming["level_name"] if upcoming else None,
                next_level_threshold_days=int(upcoming["threshold_days"]) if upcoming else None,
                days_to_next_level=(
                    int(upcoming["threshold_days"]) - continuous_days if upcoming else None
                ),
                level_updated_at=progress.get("level_updated_at"),
                streak_dates=list(streak["streak_dates"]),
            )

        if persist:
            with self.progress_store.transaction() as data:
                return _derive(data)

        snapshot = self.progress_store.snapshot()
        return _derive(snapshot)

    def get_status(self, user_id: str, today: date | None = None, persist: bool = False) -> LevelChange:
        """查询当前等级状态。

        默认不落库：查询只返回**实时推算**结果（多端看到的都是最新值），
        由断签扫描任务负责把等级回落持久化并发出通知，避免“用户先查询导致
        扫描任务看不到等级变化、从而漏发断签通知”的时序问题。
        """
        return self.recompute(user_id, today=today, change_source=SOURCE_QUERY, persist=persist)

    def streak_threshold_dates(self, streak_dates: list[str], cycle_days: int) -> list[tuple[int, str]]:
        """连续天数里“每满 cycle_days 天”的里程碑：[(3, 该天日期), (6, ...), ...]。

        用于 FR-3（连续 3 天对话 +3，循环触发）的发放与幂等去重：
        幂等键绑定到里程碑所在的那一天，因此补签回溯导致连续天数跳变时不会重复发放。
        """
        if cycle_days <= 0:
            return []
        milestones: list[tuple[int, str]] = []
        for index in range(cycle_days - 1, len(streak_dates), cycle_days):
            milestones.append((index + 1, streak_dates[index]))
        return milestones
