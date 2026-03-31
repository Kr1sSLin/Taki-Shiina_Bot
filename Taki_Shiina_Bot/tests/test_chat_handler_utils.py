import datetime
import os
import sys
import unittest
from datetime import timezone

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

from handlers.chat_handler import compute_debounce_window, extract_timer_instruction


class TestChatHandlerUtils(unittest.TestCase):
    def test_compute_debounce_window_without_last_reply(self):
        now_utc = datetime.datetime.now(timezone.utc)
        result = compute_debounce_window(None, now_utc, 8.0, 20.0, 40.0)
        self.assertEqual(result, 8.0)

    def test_compute_debounce_window_with_recent_reply(self):
        now_utc = datetime.datetime.now(timezone.utc)
        bot_last = now_utc - datetime.timedelta(seconds=10)
        result = compute_debounce_window(bot_last, now_utc, 8.0, 20.0, 40.0)
        self.assertEqual(result, 20.0)

    def test_compute_debounce_window_with_old_reply(self):
        now_utc = datetime.datetime.now(timezone.utc)
        bot_last = now_utc - datetime.timedelta(seconds=100)
        result = compute_debounce_window(bot_last, now_utc, 8.0, 20.0, 40.0)
        self.assertEqual(result, 8.0)

    def test_extract_timer_instruction_with_timer(self):
        raw = "啧，知道了。 [[TIMER:02:00|去睡觉]]"
        cleaned, target, reminder = extract_timer_instruction(raw)
        self.assertEqual(cleaned, "啧，知道了。")
        self.assertEqual(target, "02:00")
        self.assertEqual(reminder, "去睡觉")

    def test_extract_timer_instruction_without_timer(self):
        raw = "今天就先这样。"
        cleaned, target, reminder = extract_timer_instruction(raw)
        self.assertEqual(cleaned, raw)
        self.assertIsNone(target)
        self.assertIsNone(reminder)


if __name__ == "__main__":
    unittest.main()
