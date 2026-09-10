"""补签卡单测（PRD 4.5 / 5.4 / 6.2 / FR-19 ~ FR-22 / EDGE-5 / EDGE-6）。"""

from __future__ import annotations

import unittest
from datetime import timedelta

from gamification_fixture import TEST_USER, d, make_rig  # noqa: F401
from services.progress_store import SOURCE_CHAT


class MakeupCardTestCase(unittest.TestCase):
    def setUp(self):
        self._tmp, self.rig = make_rig()
        self.addCleanup(self._tmp.cleanup)

    def chat_days(self, start: str, count: int):
        begin = d(start)
        for offset in range(count):
            self.rig.points.record_user_activity(
                TEST_USER, source=SOURCE_CHAT, today=begin + timedelta(days=offset)
            )

    # ---------- 发放 ----------
    def test_first_seen_grants_current_month_card(self):
        """需求方确认：新用户当月立即获得 1 张，不必等下月 1 日。"""
        card = self.rig.makeup.ensure_initial_grant(TEST_USER)
        self.assertIsNotNone(card)
        self.assertEqual(card["status"], "AVAILABLE")
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 1)
        # 同月重复调用不再发放
        self.assertIsNone(self.rig.makeup.ensure_initial_grant(TEST_USER))

    def test_monthly_grant_and_cross_month_carryover(self):
        """FR-21：可跨月结转，当月未用不作废。"""
        self.rig.makeup.grant_for_month(TEST_USER, "2026-01")
        self.rig.makeup.grant_for_month(TEST_USER, "2026-02")
        self.rig.makeup.grant_for_month(TEST_USER, "2026-03")
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 3)
        # 同一月份不会重复发放
        self.assertIsNone(self.rig.makeup.grant_for_month(TEST_USER, "2026-03"))
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 3)

    def test_accumulation_cap_is_twelve(self):
        """EDGE-6 / FR-21：累积上限 12 张，达到上限当月暂停发放。"""
        for month in range(1, 15):
            self.rig.makeup.grant_for_month(TEST_USER, f"2026-{month:02d}")
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 12)

    def test_grant_resumes_after_consumption_within_same_month(self):
        """FR-19/FR-21：达到上限当月暂停，用户消耗后自动恢复发放。"""
        for month in range(1, 14):
            self.rig.makeup.grant_for_month(TEST_USER, f"2026-{month:02d}")
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 12)
        # 当月（2026-13）因已达上限被跳过，且不记账
        self.assertNotEqual(
            self.rig.makeup.get_summary(TEST_USER)["lastGrantedMonth"], "2026-13"
        )
        self.rig.makeup.use_card(TEST_USER, "2026-01-05", today=d("2026-01-10"))
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 11)
        # 消耗后同月自动恢复发放
        card = self.rig.makeup.grant_for_month(TEST_USER, "2026-13")
        self.assertIsNotNone(card)
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 12)

    # ---------- 使用：追溯挽回（FR-22 / 6.2 / 6.1） ----------
    def test_use_card_restores_level_after_break(self):
        self.chat_days("2026-09-01", 10)  # 09-01 ~ 09-10，等级 PANDA_LV2（7 天阈值）
        self.assertEqual(
            self.rig.gamification.get_level_status(TEST_USER, today=d("2026-09-10"))["levelCode"],
            "PANDA_LV2",
        )

        # 跳过 09-11，09-12 再聊 → 连续天数断为 1，等级回落 NONE
        result = self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-12"))
        self.assertEqual(result.level.level_code, "NONE")
        self.assertEqual(result.level.change_type, "RESET")

        # 用补签卡补上 09-11 → 重新推算为 12 天（09-01~09-12 连续），等级跳回缺口发生前的水平
        # （查询等级时已按“新用户当月立即发放”规则发过 1 张）
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 1)
        use = self.rig.makeup.use_card(TEST_USER, "2026-09-11", today=d("2026-09-12"))
        self.assertTrue(use.ok, use.message)
        self.assertEqual(use.target_date, "2026-09-11")
        self.assertEqual(use.level.continuous_days, 12)
        self.assertEqual(use.level.level_code, "PANDA_LV2")
        # EDGE-11：属于“曾经达成过”的等级，使用恢复语义而非首次升级
        self.assertEqual(use.level.change_type, "RESTORE")
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 0)

        # 权威数据以 daily_activity_log 为准，并写入了 MAKEUP_CARD 来源
        snapshot = self.rig.gamification.progress_store.snapshot()
        entries = [e for e in snapshot["daily_activity_log"].values() if e["user_id"] == TEST_USER]
        sources = {e["activity_date"]: e["source"] for e in entries}
        self.assertEqual(sources["2026-09-11"], "MAKEUP_CARD")
        self.assertEqual(sources["2026-09-12"], "CHAT")

    def test_use_card_does_not_create_points_ledger(self):
        """PRD 5.3 注：补签卡的发放/使用不产生积分流水。"""
        self.rig.makeup.ensure_initial_grant(TEST_USER)
        self.chat_days("2026-09-01", 4)
        balance_before = self.rig.points.get_balance(TEST_USER)
        ledger_before = len(self.rig.ledger())

        # 补 09-05（原本缺失）
        use = self.rig.makeup.use_card(TEST_USER, "2026-09-05", today=d("2026-09-06"))
        self.assertTrue(use.ok, use.message)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), balance_before)
        self.assertEqual(len(self.rig.ledger()), ledger_before)

    def test_use_card_backfills_milestone_reward_once(self):
        """补签使连续天数跨过 +3 里程碑时，历史未发放过的里程碑会补齐且只补一次。"""
        self.chat_days("2026-09-01", 2)      # 09-01, 09-02
        self.rig.makeup.ensure_initial_grant(TEST_USER)
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-04"))  # 断一天

        before = len([e for e in self.rig.ledger() if e["reason_code"] == "STREAK_3_DAY"])
        use = self.rig.makeup.use_card(TEST_USER, "2026-09-03", today=d("2026-09-04"))
        self.assertTrue(use.ok, use.message)
        self.assertEqual(use.level.continuous_days, 4)
        created = [entry for entry in use.rewards if entry["reason_code"] == "STREAK_3_DAY"]
        self.assertEqual(len(created), 1, "补签后连续 4 天应补发 1 次里程碑奖励")
        after = len([e for e in self.rig.ledger() if e["reason_code"] == "STREAK_3_DAY"])
        self.assertEqual(after - before, 1)

    # ---------- 使用：校验失败场景 ----------
    def test_use_card_without_card_fails(self):
        result = self.rig.makeup.use_card(TEST_USER, "2026-09-01", today=d("2026-09-10"))
        self.assertFalse(result.ok)
        self.assertEqual(result.error_code, "MAKEUP_CARD_NOT_ENOUGH")

    def test_use_card_rejects_date_with_existing_activity(self):
        self.rig.makeup.ensure_initial_grant(TEST_USER)
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-01"))
        result = self.rig.makeup.use_card(TEST_USER, "2026-09-01", today=d("2026-09-10"))
        self.assertFalse(result.ok)
        self.assertEqual(result.error_code, "DATE_ALREADY_ACTIVE")
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 1, "校验失败不应消耗卡片")

    def test_use_card_rejects_future_and_bad_format(self):
        self.rig.makeup.ensure_initial_grant(TEST_USER)
        future = self.rig.makeup.use_card(TEST_USER, "2026-09-30", today=d("2026-09-10"))
        self.assertFalse(future.ok)
        self.assertEqual(future.error_code, "TARGET_DATE_IN_FUTURE")

        bad = self.rig.makeup.use_card(TEST_USER, "2026/09/01", today=d("2026-09-10"))
        self.assertFalse(bad.ok)
        self.assertEqual(bad.error_code, "INVALID_TARGET_DATE")

    def test_history_and_summary(self):
        self.rig.makeup.grant_for_month(TEST_USER, "2026-01")
        self.rig.makeup.grant_for_month(TEST_USER, "2026-02")
        self.rig.makeup.use_card(TEST_USER, "2026-01-03", today=d("2026-02-10"))
        history = self.rig.makeup.get_history(TEST_USER)
        self.assertEqual(history["total"], 2)
        self.assertEqual(history["summary"]["available"], 1)
        self.assertEqual(history["summary"]["used"], 1)
        used = next(card for card in history["items"] if card["status"] == "USED")
        self.assertEqual(used["used_for_date"], "2026-01-03")
        self.assertEqual(used["granted_month"], "2026-01")

    def test_cap_config_is_twelve_by_default(self):
        rules = self.rig.gamification.config.get_points_rules()
        self.assertEqual(rules["makeup_card"]["max_available"], 12)
        self.assertEqual(rules["makeup_card"]["monthly_grant"], 1)


if __name__ == "__main__":
    unittest.main()
