"""定时任务单测（PRD 6.2 / FR-18 / FR-19 / 4.3 / EDGE-8）。"""

from __future__ import annotations

import asyncio
import unittest
from datetime import timedelta

from gamification_fixture import TEST_USER, d, make_rig  # noqa: F401
from services.progress_store import REASON_ANNIVERSARY, SOURCE_CHAT


def run(coro):
    return asyncio.run(coro)


class PointsJobsTestCase(unittest.TestCase):
    def setUp(self):
        self._tmp, self.rig = make_rig()
        self.addCleanup(self._tmp.cleanup)

    def chat_days(self, start: str, count: int):
        begin = d(start)
        for offset in range(count):
            self.rig.points.record_user_activity(
                TEST_USER, source=SOURCE_CHAT, today=begin + timedelta(days=offset)
            )

    # ---------- 断签提醒（FR-18 / EDGE-8） ----------
    def test_warning_pushed_at_three_and_four_days_gap_only_once_per_day(self):
        self.chat_days("2026-09-01", 7)  # 最后有效对话 09-07
        result = run(self.rig.jobs.scan_breaks(today=d("2026-09-10")))  # 距今 3 天
        self.assertEqual(result["warned"], [TEST_USER])

        warnings = self.rig.events_of("streak.warning")
        self.assertEqual(len(warnings), 1)
        payload = warnings[0]["payload"]
        self.assertEqual(payload["gapDays"], 3)
        self.assertEqual(payload["remainingDays"], 2)
        self.assertEqual(payload["levelCode"], "PANDA_LV2")
        self.assertEqual(payload["deadlineDate"], "2026-09-12")
        # 同一天重复扫描不重复推送
        run(self.rig.jobs.scan_breaks(today=d("2026-09-10")))
        self.assertEqual(len(self.rig.events_of("streak.warning")), 1)

    def test_warning_again_on_next_gap_day(self):
        self.chat_days("2026-09-01", 7)
        run(self.rig.jobs.scan_breaks(today=d("2026-09-10")))
        result = run(self.rig.jobs.scan_breaks(today=d("2026-09-11")))  # 距今 4 天
        self.assertEqual(result["warned"], [TEST_USER])
        self.assertEqual(len(self.rig.events_of("streak.warning")), 2)
        self.assertEqual(self.rig.events_of("streak.warning")[-1]["payload"]["remainingDays"], 1)

    # ---------- 断签执行（6.2 步骤 3） ----------
    def test_break_scan_resets_level_and_notifies_all_connections(self):
        self.chat_days("2026-09-01", 7)
        balance_before = self.rig.points.get_balance(TEST_USER)
        result = run(self.rig.jobs.scan_breaks(today=d("2026-09-13")))  # 距今 5 天
        self.assertEqual(result["broken"], [TEST_USER])

        events = self.rig.events_of("level.changed")
        self.assertTrue(events)
        payload = events[-1]["payload"]
        self.assertEqual(payload["levelCode"], "NONE")
        self.assertEqual(payload["prevLevelCode"], "PANDA_LV2")
        self.assertEqual(payload["changeType"], "RESET")
        # 事件不带设备维度过滤：广播函数对账号下全部在线连接生效（EDGE-8）
        self.assertEqual([user for user, _event in self.rig.events], [TEST_USER] * len(self.rig.events))

        stored = self.rig.gamification.progress_store.snapshot()["user_progress"][TEST_USER]
        self.assertEqual(stored["level_code"], "NONE")
        self.assertEqual(stored["continuous_days"], 0)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), balance_before)

        # 断签事件只通知一次
        run(self.rig.jobs.scan_breaks(today=d("2026-09-14")))
        self.assertEqual(len(self.rig.events_of("level.changed")), 1)

    def test_no_notice_for_user_without_level(self):
        self.chat_days("2026-09-01", 1)
        run(self.rig.jobs.scan_breaks(today=d("2026-09-10")))
        self.assertEqual(self.rig.events_of("level.changed"), [])

    # ---------- 月度补签卡（FR-19） ----------
    def test_monthly_makeup_card_granted_once_per_month(self):
        result = run(self.rig.jobs.grant_monthly_makeup_cards(today=d("2026-10-01")))
        self.assertEqual(result["granted"], [TEST_USER])
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 1)
        again = run(self.rig.jobs.grant_monthly_makeup_cards(today=d("2026-10-20")))
        self.assertEqual(again["granted"], [])
        next_month = run(self.rig.jobs.grant_monthly_makeup_cards(today=d("2026-11-01")))
        self.assertEqual(next_month["granted"], [TEST_USER])
        self.assertEqual(self.rig.makeup.get_available_count(TEST_USER), 2)

    # ---------- 纪念日（4.3 / EDGE-4） ----------
    def test_anniversary_granted_unconditionally_on_configured_dates(self):
        result = run(self.rig.jobs.grant_anniversaries(today=d("2026-12-26")))
        self.assertEqual(len(result["granted"]), 1)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 100)
        entry = self.rig.ledger()[0]
        self.assertEqual(entry["reason_code"], REASON_ANNIVERSARY)
        self.assertEqual(entry["note"], "纪念日")

        # 再次运行同一天不会重复发放
        run(self.rig.jobs.grant_anniversaries(today=d("2026-12-26")))
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 100)

        # 次年纪念日再次发放
        run(self.rig.jobs.grant_anniversaries(today=d("2027-12-26")))
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 200)

    def test_taki_birthday_granted_on_august_ninth(self):
        result = run(self.rig.jobs.grant_anniversaries(today=d("2026-08-09")))
        self.assertEqual(len(result["granted"]), 1)
        self.assertEqual(result["granted"][0]["name"], "Taki生日")
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 100)

    def test_no_grant_on_ordinary_day(self):
        result = run(self.rig.jobs.grant_anniversaries(today=d("2026-09-10")))
        self.assertEqual(result["granted"], [])
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 0)

    def test_anniversary_stacks_with_daily_first_chat(self):
        run(self.rig.jobs.grant_anniversaries(today=d("2026-12-26")))
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-12-26"))
        reasons = [entry["reason_code"] for entry in self.rig.ledger()]
        self.assertEqual(sorted(reasons), sorted([REASON_ANNIVERSARY, "DAILY_FIRST_CHAT"]))
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 101)


if __name__ == "__main__":
    unittest.main()
