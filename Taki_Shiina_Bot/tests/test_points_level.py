"""积分机制与等级体系单测（PRD 4.3 / 4.4 / FR-10 ~ FR-17 / 5.1a / EDGE-3 / EDGE-4）。"""

from __future__ import annotations

import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import patch
from datetime import date, timedelta

from gamification_fixture import TEST_USER, d, make_rig  # noqa: F401  (夹具已处理 sys.path)
from services.progress_store import (
    PointsStore,
    REASON_ANNIVERSARY,
    REASON_DAILY_FIRST_CHAT,
    REASON_STREAK_3_DAY,
    SOURCE_CHAT,
)
from services.points_service import InteractionLimitError, LedgerReconciliationError


class PointsLevelTestCase(unittest.TestCase):
    def setUp(self):
        self._tmp, self.rig = make_rig()
        self.addCleanup(self._tmp.cleanup)

    # ---------- 工具 ----------
    def chat_days(self, start: str, count: int, skip_last: int = 0):
        """从 start 起连续 count 天每天发生一次用户对话。"""
        begin = d(start)
        results = []
        for offset in range(count - skip_last):
            results.append(
                self.rig.points.record_user_activity(
                    TEST_USER, source=SOURCE_CHAT, today=begin + timedelta(days=offset)
                )
            )
        return results

    def reasons(self):
        return [entry["reason_code"] for entry in self.rig.ledger()]

    def test_atomic_daily_limit_owner_isolation_and_original_retry(self):
        with self.rig.gamification.points_store.transaction() as data:
            for owner in (TEST_USER, "other"):
                PointsStore.ensure_account(data, owner)["balance"] = 100
        rules = self.rig.gamification.config.get_points_rules()
        self.rig.gamification.config.update_sections({"points_rules": {**rules, "interaction_daily_limit": 2}})
        def charge(index):
            try:
                return self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, f"concurrent-{index}", "2026-10-22")
            except InteractionLimitError:
                return None
        with ThreadPoolExecutor(max_workers=5) as pool:
            approved = [result for result in pool.map(charge, range(5)) if result]
        self.assertEqual(len(approved), 2)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 90)
        original = approved[0].entry["idempotency_key"].split(f"item_send:{TEST_USER}:", 1)[1]
        self.assertFalse(self.rig.points.deduct_for_item(TEST_USER, "coffee", 99, original, "2026-10-23").created)
        self.assertTrue(self.rig.points.deduct_for_item("other", "coffee", 5, "other", "2026-10-22").created)
        self.assertEqual({result.entry["daily_limit"] for result in approved}, {2})

    def test_cross_day_refund_limit_changes_and_failed_refund(self):
        with self.rig.gamification.points_store.transaction() as data:
            PointsStore.ensure_account(data, TEST_USER)["balance"] = 100
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "old", "2026-10-21")
        rules = self.rig.gamification.config.get_points_rules()
        self.rig.gamification.config.update_sections({"points_rules": {**rules, "interaction_daily_limit": 1}})
        with self.assertRaises(InteractionLimitError):
            self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "blocked", "2026-10-21")
        with patch.object(self.rig.gamification.points_store, "_save", side_effect=OSError("合成退款落盘失败")):
            with self.assertRaises(OSError):
                self.rig.points.refund_item(TEST_USER, "coffee", 5, "old", "2026-10-22")
        with self.assertRaises(InteractionLimitError):
            self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "still-blocked", "2026-10-21")
        refund = self.rig.points.refund_item(TEST_USER, "coffee", 5, "old", "2026-10-22")
        self.assertEqual(refund.entry["business_date"], "2026-10-21")
        self.assertGreater(refund.entry["created_at"], 0)
        # 模拟旧版退款记在实际发生日；关联仍应释放原扣款名额。
        with self.rig.gamification.points_store.transaction() as data:
            PointsStore.find_by_idempotency(data, f"item_refund:{TEST_USER}:old")["business_date"] = "2026-10-22"
        self.assertTrue(self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "released", "2026-10-21").created)
        self.assertTrue(self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "today", "2026-10-22").created)
        with self.assertRaises(LedgerReconciliationError):
            self.rig.points.refund_item(TEST_USER, "coffee", 6, "today")

    def test_ambiguous_legacy_ledger_blocks_only_new_limited_charges(self):
        with self.rig.gamification.points_store.transaction() as data:
            PointsStore.ensure_account(data, TEST_USER)["balance"] = 100
        original = self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "legacy")
        with self.rig.gamification.points_store.transaction() as data:
            PointsStore.find_by_idempotency(data, original.entry["idempotency_key"])["business_date"] = None
        rules = self.rig.gamification.config.get_points_rules()
        self.rig.gamification.config.update_sections({"points_rules": {**rules, "interaction_daily_limit": 2}})
        with self.assertRaises(LedgerReconciliationError):
            self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "new")
        self.assertFalse(self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "legacy").created)
        self.assertTrue(self.rig.points.refund_item(TEST_USER, "coffee", 5, "legacy").created)

    # ---------- 每日首次对话 +1 ----------
    def test_daily_first_chat_awards_once_per_day(self):
        first = self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-01"))
        second = self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-01"))

        self.assertTrue(first.new_activity)
        self.assertFalse(second.new_activity)
        self.assertEqual(first.balance, 1)
        self.assertEqual(second.balance, 1)
        self.assertEqual(self.reasons().count(REASON_DAILY_FIRST_CHAT), 1)
        self.assertEqual(len(first.rewards), 1)
        self.assertEqual(first.rewards[0]["change_amount"], 1)

    def test_daily_first_chat_resets_next_day(self):
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-01"))
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-02"))
        self.assertEqual(self.reasons().count(REASON_DAILY_FIRST_CHAT), 2)

    # ---------- 连续 3 天 +3（循环） ----------
    def test_streak_reward_fires_every_three_days(self):
        self.chat_days("2026-09-01", 9)
        streak_entries = [e for e in self.rig.ledger() if e["reason_code"] == REASON_STREAK_3_DAY]
        self.assertEqual(len(streak_entries), 3, "9 天应触发 3 次（第 3/6/9 天）")
        self.assertTrue(all(entry["change_amount"] == 3 for entry in streak_entries))
        # 每日首次 9 次 ×1 + 连续奖励 3 次 ×3 = 18
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 18)

    def test_streak_reward_not_duplicated_on_same_milestone(self):
        self.chat_days("2026-09-01", 3)
        before = len([e for e in self.rig.ledger() if e["reason_code"] == REASON_STREAK_3_DAY])
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-03"))
        after = len([e for e in self.rig.ledger() if e["reason_code"] == REASON_STREAK_3_DAY])
        self.assertEqual(before, after)

    # ---------- 等级阈值（4.4） ----------
    def test_level_thresholds(self):
        cases = [
            (0, "NONE"),
            (2, "NONE"),
            (3, "PANDA_LV1"),
            (6, "PANDA_LV1"),
            (7, "PANDA_LV2"),
            (14, "PANDA_LV2"),
            (15, "PANDA_LV3"),
            (29, "PANDA_LV3"),
            (30, "PANDA_LV4"),
            (59, "PANDA_LV4"),
            (60, "PANDA_LV5"),
            (99, "PANDA_LV5"),
            (100, "PANDA_LV6"),
            (199, "PANDA_LV6"),
            (200, "PANDA_LV7"),
            (365, "PANDA_LV7"),
        ]
        for days, expected in cases:
            with self.subTest(days=days):
                self.assertEqual(self.rig.level.resolve_level(days)["level_code"], expected)

    def test_level_upgrade_event_and_progress(self):
        self.chat_days("2026-09-01", 3)
        status = self.rig.gamification.get_level_status(TEST_USER, today=d("2026-09-03"))
        self.assertEqual(status["levelCode"], "PANDA_LV1")
        self.assertEqual(status["continuousDays"], 3)
        self.assertEqual(status["nextLevelCode"], "PANDA_LV2")
        self.assertEqual(status["daysToNextLevel"], 4)
        self.assertFalse(status["isDefaultLevel"])

    def test_default_level_has_no_dedicated_copy(self):
        """EDGE-7：未达阈值时等级默认态不设专属文案。"""
        status = self.rig.gamification.get_level_status(TEST_USER, today=d("2026-09-01"))
        self.assertEqual(status["levelCode"], "NONE")
        self.assertEqual(status["levelName"], "")
        self.assertTrue(status["isDefaultLevel"])
        self.assertEqual(status["continuousDays"], 0)

    # ---------- 5.1a 推算规则 ----------
    def test_gap_under_threshold_keeps_level(self):
        self.chat_days("2026-09-01", 10)
        current = self.rig.gamification.get_level_status(TEST_USER, today=d("2026-09-10"))
        self.assertEqual(current["levelCode"], "PANDA_LV2")
        # 3 天未来对话：连续天数仍按最后一条记录向过去推算
        preview = self.rig.level.recompute(TEST_USER, today=d("2026-09-13"), persist=False)
        self.assertEqual(preview.continuous_days, 10)
        self.assertEqual(preview.gap_days, 3)
        self.assertEqual(preview.level_code, "PANDA_LV2")
        self.assertTrue(preview.break_pending)
        self.assertEqual(preview.break_deadline_date, "2026-09-15")

    def test_missing_single_day_breaks_streak_on_next_chat(self):
        """5.1a：向过去回溯遇到第一个缺失自然日即中断（补签卡即用于填补该缺口）。"""
        self.chat_days("2026-09-01", 5)
        # 跳过 09-06，09-07 再来
        result = self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-07"))
        self.assertEqual(result.level.continuous_days, 1)
        self.assertEqual(result.level.level_code, "NONE")
        self.assertEqual(result.level.change_type, "RESET")

    # ---------- 断签（FR-17 / FR-13） ----------
    def test_break_resets_level_but_keeps_balance(self):
        self.chat_days("2026-09-01", 10)
        balance_before = self.rig.points.get_balance(TEST_USER)
        ledger_before = len(self.rig.ledger())
        self.assertEqual(
            self.rig.gamification.get_level_status(TEST_USER, today=d("2026-09-10"))["levelCode"],
            "PANDA_LV2",
        )

        # 连续 5 天未对话
        status = self.rig.level.recompute(TEST_USER, today=d("2026-09-15"), persist=True)
        self.assertEqual(status.level_code, "NONE")
        self.assertEqual(status.continuous_days, 0)
        self.assertTrue(status.level_changed)
        self.assertEqual(status.change_type, "RESET")

        self.assertEqual(self.rig.points.get_balance(TEST_USER), balance_before)
        self.assertEqual(len(self.rig.ledger()), ledger_before, "断签不得产生任何积分流水")
        self.assertNotIn("RESET", [e.get("reason_code") for e in self.rig.ledger()])

    def test_gap_of_four_days_still_keeps_level(self):
        self.chat_days("2026-09-01", 7)  # 最后有效对话 09-07
        status = self.rig.level.recompute(TEST_USER, today=d("2026-09-10"), persist=False)
        self.assertEqual(status.gap_days, 3)
        self.assertEqual(status.level_code, "PANDA_LV2")
        status = self.rig.level.recompute(TEST_USER, today=d("2026-09-11"), persist=False)
        self.assertEqual(status.gap_days, 4)
        self.assertEqual(status.level_code, "PANDA_LV2")

    # ---------- 纪念日（4.3 / EDGE-4） ----------
    def test_anniversary_grant_is_idempotent_and_stacks(self):
        first = self.rig.points.grant_anniversary(
            TEST_USER, d("2026-12-26"), name="纪念日", points=100
        )
        again = self.rig.points.grant_anniversary(
            TEST_USER, d("2026-12-26"), name="纪念日", points=100
        )
        self.assertTrue(first.created)
        self.assertFalse(again.created)

        activity = self.rig.points.record_user_activity(TEST_USER, today=d("2026-12-26"))
        self.assertEqual(activity.balance, 101)
        entries = self.rig.ledger()
        self.assertEqual(
            sorted(entry["reason_code"] for entry in entries),
            sorted([REASON_ANNIVERSARY, REASON_DAILY_FIRST_CHAT]),
        )
        anniversary = next(e for e in entries if e["reason_code"] == REASON_ANNIVERSARY)
        self.assertEqual(anniversary["change_amount"], 100)
        self.assertEqual(anniversary["balance_after"], 100)

    def test_taki_birthday_is_configured(self):
        rules = self.rig.gamification.config.get_points_rules()
        dates = {entry["date"]: entry for entry in rules["anniversaries"]}
        self.assertIn("12-26", dates)
        self.assertIn("08-09", dates)
        self.assertEqual(dates["08-09"]["points"], 100)

    # ---------- 幂等（FR-13） ----------
    def test_ledger_idempotency_key_blocks_replay(self):
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-01"))
        keys = [entry["idempotency_key"] for entry in self.rig.ledger()]
        self.assertEqual(len(keys), len(set(keys)), "流水幂等键必须唯一")
        self.assertIn(f"daily_first_chat:{TEST_USER}:2026-09-01", keys)


class BusinessDateTestCase(unittest.TestCase):
    def test_business_timezone_defaults_to_utc8(self):
        import datetime as _dt

        from time_utils import business_date_from_ms, get_business_tz_offset_hours, iter_dates

        self.assertEqual(get_business_tz_offset_hours(), 8.0)
        # UTC 15:30 == 北京时间 23:30（当天）；UTC 16:30 == 北京时间次日 00:30
        utc_dt = _dt.datetime(2026, 9, 1, 15, 30, tzinfo=_dt.timezone.utc)
        self.assertEqual(
            business_date_from_ms(int(utc_dt.timestamp() * 1000)).isoformat(), "2026-09-01"
        )
        utc_dt2 = _dt.datetime(2026, 9, 1, 16, 30, tzinfo=_dt.timezone.utc)
        self.assertEqual(
            business_date_from_ms(int(utc_dt2.timestamp() * 1000)).isoformat(), "2026-09-02"
        )
        self.assertEqual(
            [value.isoformat() for value in iter_dates(d("2026-09-01"), d("2026-09-03"))],
            ["2026-09-01", "2026-09-02", "2026-09-03"],
        )


class BusinessPeriodTestCase(unittest.TestCase):
    """时段判定由服务端完成（确定性），不再让模型按 HH:MM 自己猜。"""

    def test_period_boundaries(self):
        import datetime as _dt

        from time_utils import build_time_block, describe_business_period

        cases = [
            ("23:00", "深夜"),
            ("00:30", "深夜"),
            ("04:59", "深夜"),
            ("05:00", "清晨"),
            ("08:59", "清晨"),
            ("09:00", "上午"),
            ("11:59", "上午"),
            ("12:00", "下午"),
            ("17:59", "下午"),
            ("18:00", "晚上"),
            ("22:59", "晚上"),
        ]
        for text, expected in cases:
            hour, minute = (int(part) for part in text.split(":"))
            moment = _dt.datetime(2026, 9, 10, hour, minute)
            with self.subTest(time=text):
                self.assertEqual(describe_business_period(moment), expected)

    def test_time_block_contains_both_lines(self):
        import datetime as _dt

        from time_utils import build_time_block

        block = build_time_block(_dt.datetime(2026, 9, 10, 2, 14))
        self.assertIn("【当前北京时间】：02:14", block)
        self.assertIn("【当前时段】：深夜", block)
        self.assertIn("系统已判定", block)


if __name__ == "__main__":
    unittest.main()
