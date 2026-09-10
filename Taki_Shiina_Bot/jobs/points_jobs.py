"""
互动积分 · 等级体系 —— 定时任务（PRD §3.2 jobs/ / 6.2 / FR-18 / FR-19 / 4.3）

| 任务 | 频率 | 对应 PRD |
|---|---|---|
| 断签扫描与提醒 | 每 10 分钟 | 6.2 步骤 1～3、FR-18（3～4 天提前提醒，推送给全部在线设备） |
| 月度补签卡发放 | 每 10 分钟检查一次（同一自然月只发 1 张） | FR-19 / FR-21（达上限当月暂停，消耗后自动恢复） |
| 纪念日 / Taki 生日奖励 | 每个自然日一次 | 4.3 纪念日（12.26）+100；需求方追加 8.9 Taki 生日 +100，均无条件发放 |

任务与积分数据同进程运行（``ws_api.py`` 启动时拉起），因此
“写入等级 → 推送通知”全程在同一进程内完成，不需要跨进程桥接。
"""

from __future__ import annotations

import asyncio
import logging
from datetime import date, timedelta
from typing import Any, Awaitable, Callable

from time_utils import get_business_today
from services.level_service import SOURCE_BREAK_SCAN
from services.points_events import (
    level_changed_event,
    makeup_card_changed_event,
    points_changed_event,
    streak_warning_event,
)

BREAK_SCAN_INTERVAL_SECONDS = 600
MAKEUP_GRANT_CHECK_INTERVAL_SECONDS = 600
ANNIVERSARY_CHECK_INTERVAL_SECONDS = 600


class PointsJobs:
    def __init__(
        self,
        gamification,
        logger: logging.Logger | None = None,
        broadcast_json: Callable[[str, dict], Awaitable[Any]] | None = None,
        alert_sender: Callable[[str], Any] | None = None,
    ):
        self.gamification = gamification
        self.logger = logger or logging.getLogger(__name__)
        self.broadcast_json = broadcast_json
        self.alert_sender = alert_sender
        self._last_break_scan = 0.0
        self._last_makeup_check = 0.0
        self._last_anniversary_date: str | None = None
        self._loop: asyncio.AbstractEventLoop | None = None

    # ==================== 调度循环 ====================
    async def run_forever(self, interval_seconds: int = 60) -> None:
        self.logger.info("[积分任务] 定时任务调度器已启动（断签扫描 / 补签卡发放 / 纪念日）")
        while True:
            try:
                now = asyncio.get_running_loop().time()
                today = get_business_today()

                # 纪念日：每个自然日只在首次调度时结算一次（账本幂等键兜底）
                if self._last_anniversary_date != today.isoformat():
                    self._last_anniversary_date = today.isoformat()
                    await self.grant_anniversaries(today)

                if now - self._last_makeup_check >= MAKEUP_GRANT_CHECK_INTERVAL_SECONDS:
                    self._last_makeup_check = now
                    await self.grant_monthly_makeup_cards(today)

                if now - self._last_break_scan >= BREAK_SCAN_INTERVAL_SECONDS:
                    self._last_break_scan = now
                    await self.scan_breaks(today)
            except Exception as exc:  # 定时任务异常不能中断循环
                self.logger.exception(f"[积分任务] 调度异常: {exc}")
                self._alert(f"❗ 积分定时任务异常: {exc}")
            await asyncio.sleep(max(5, int(interval_seconds)))

    # ==================== 断签扫描（6.2） ====================
    async def scan_breaks(self, today: date | None = None) -> dict[str, Any]:
        today = today or get_business_today()
        gamification = self.gamification
        rules = gamification.config.get_points_rules()
        break_gap_days = int(rules.get("break_gap_days", 5))
        warning_gaps = set(rules.get("warning_gap_days", [3, 4]) or [])
        today_str = today.isoformat()

        summary = {"scanned": 0, "warned": [], "broken": []}
        for user_id in gamification.known_user_ids():
            try:
                # 先只读推算，拿到“断签前”的等级，再决定是否落库与通知
                preview = gamification.level.recompute(
                    user_id, today=today, change_source=SOURCE_BREAK_SCAN, persist=False
                )
                summary["scanned"] += 1
                gap_days = preview.gap_days
                if gap_days is None:
                    continue

                if gap_days >= break_gap_days:
                    change = gamification.level.recompute(
                        user_id, today=today, change_source=SOURCE_BREAK_SCAN, persist=True
                    )
                    if await self._notify_break(user_id, preview, change, today_str, break_gap_days):
                        summary["broken"].append(user_id)
                    continue

                if gap_days in warning_gaps:
                    if await self._notify_warning(user_id, preview, today_str, break_gap_days):
                        summary["warned"].append(user_id)
            except Exception as exc:
                self.logger.exception(f"[积分任务] 断签扫描失败: user={user_id}, err={exc}")

        if summary["warned"] or summary["broken"]:
            self.logger.info(
                f"[积分任务] 断签扫描完成: 扫描 {summary['scanned']} 人, "
                f"提醒 {len(summary['warned'])} 人, 断签 {len(summary['broken'])} 人"
            )
        return summary

    async def _notify_warning(
        self, user_id: str, preview, today_str: str, break_gap_days: int
    ) -> bool:
        """FR-18：连续 3～4 天未对话时提醒；用户当日完成对话后提醒自动解除。"""
        gamification = self.gamification
        with gamification.progress_store.transaction() as data:
            meta = data["meta"].setdefault("streak_warnings", {})
            if meta.get(user_id) == today_str:
                return False
            meta[user_id] = today_str
        remaining = max(0, break_gap_days - int(preview.gap_days or 0))
        await self._push(user_id, streak_warning_event(preview, remaining))
        self.logger.info(
            f"[积分任务] 断签提醒已推送: user={user_id}, gap={preview.gap_days} 天, "
            f"剩余 {remaining} 天, 当前等级={preview.level_code}"
        )
        return True

    async def _notify_break(
        self, user_id: str, preview, change, today_str: str, break_gap_days: int
    ) -> bool:
        """6.2 步骤 3：达到阈值直接断签（不因持有补签卡而暂缓，可事后追溯挽回）。"""
        gamification = self.gamification
        occurrence = preview.last_valid_date or today_str
        with gamification.progress_store.transaction() as data:
            meta = data["meta"].setdefault("break_notices", {})
            already = meta.get(user_id) == occurrence
            if not already:
                meta[user_id] = occurrence
        # 仅当断签前确实持有等级时才通知，避免对一直是 NONE 的用户产生噪音。
        # 注意用 prev_level_code（断签前的等级）判断：preview 里的 level_code 已经是
        # 按 gap≥阈值推算后的 NONE。
        if preview.prev_level_code == "NONE":
            return False
        if already:
            return False
        await self._push(user_id, level_changed_event(change if change.level_changed else preview))
        self.logger.info(
            f"[积分任务] 已执行断签: user={user_id}, 原等级={preview.level_code}, "
            f"连续天数={preview.continuous_days}, 距今={preview.gap_days} 天"
        )
        self._alert(
            f"📉 等级断签清零\n用户: {user_id}\n原等级: {preview.level_code}（连续 {preview.continuous_days} 天）\n"
            f"最后有效对话: {preview.last_valid_date}\n间隔: {break_gap_days}+ 天"
        )
        return True

    # ==================== 月度补签卡发放（FR-19 / FR-21） ====================
    async def grant_monthly_makeup_cards(self, today: date | None = None) -> dict[str, Any]:
        today = today or get_business_today()
        month = today.strftime("%Y-%m")
        gamification = self.gamification
        granted: list[str] = []
        for user_id in gamification.known_user_ids():
            try:
                card = gamification.makeup.grant_for_month(user_id, month)
                if not card:
                    continue
                granted.append(user_id)
                await self._push(
                    user_id,
                    makeup_card_changed_event(gamification.makeup.get_summary(user_id), "MONTHLY_GRANT"),
                )
            except Exception as exc:
                self.logger.exception(f"[积分任务] 补签卡发放失败: user={user_id}, err={exc}")
        if granted:
            self.logger.info(f"[积分任务] 本月补签卡已发放: {month}, 用户数={len(granted)}")
        return {"month": month, "granted": granted}

    # ==================== 纪念日 / Taki 生日（4.3） ====================
    async def grant_anniversaries(self, today: date | None = None) -> dict[str, Any]:
        today = today or get_business_today()
        gamification = self.gamification
        rules = gamification.config.get_points_rules()
        month_day = today.strftime("%m-%d")
        matched = [
            entry for entry in rules.get("anniversaries", []) if entry.get("date") == month_day
        ]
        if not matched:
            return {"date": today.isoformat(), "granted": []}

        granted: list[dict[str, Any]] = []
        for user_id in gamification.known_user_ids():
            for entry in matched:
                try:
                    reward = gamification.points.grant_anniversary(
                        user_id=user_id,
                        anniversary_date=today,
                        name=entry.get("name", "纪念日"),
                        points=int(entry.get("points", 0)),
                    )
                    if not reward.created:
                        continue
                    granted.append({"userId": user_id, "name": entry.get("name"), "points": reward.amount})
                    await self._push(
                        user_id, points_changed_event(reward.entry, balance=gamification.points.get_balance(user_id))
                    )
                except Exception as exc:
                    self.logger.exception(f"[积分任务] 纪念日奖励发放失败: user={user_id}, err={exc}")
        if granted:
            self.logger.info(f"[积分任务] 纪念日奖励已发放: {month_day}, 共 {len(granted)} 笔")
        return {"date": today.isoformat(), "granted": granted}

    # ==================== 工具 ====================
    async def _push(self, user_id: str, event: dict[str, Any]) -> None:
        if not self.broadcast_json:
            return
        try:
            await self.broadcast_json(user_id, event)
        except Exception as exc:
            self.logger.warning(f"[积分任务] 推送失败: {exc}")

    def _alert(self, text: str) -> None:
        if not self.alert_sender:
            return
        try:
            self.alert_sender(text)
        except Exception as exc:
            self.logger.warning(f"[积分任务] 告警发送失败: {exc}")
