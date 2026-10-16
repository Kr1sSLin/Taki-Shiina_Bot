import os
import sys
import unittest
from unittest.mock import patch

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

from text_utils import (
    clean_short_term_history, detect_polluted_tails, inject_emojis,
    is_repetitive_reply, sanitize_taki_reply, strip_polluted_tail,
    with_history_timestamp,
)


class TestTextUtils(unittest.TestCase):
    def test_reply_removes_timestamps_and_action_narrative(self):
        self.assertEqual(sanitize_taki_reply("【08-22 17:22】*看向窗外*\n今天怎么样？"), "今天怎么样？")

    def test_chat_keeps_newlines_and_does_not_split_sentences(self):
        text = "第一句。第二句。\n等会一起吃。"
        self.assertEqual(sanitize_taki_reply(text), text)

    def test_repeated_tail_is_removed_without_changing_user_input(self):
        tail = "你这家伙，真拿你没办法。"
        user = {"role": "user", "content": tail}
        history = [user] + [{"role": "assistant", "content": f"答复{i}\n{tail}"} for i in range(3)]
        cleaned, tails = clean_short_term_history(history)
        self.assertIn(tail, tails)
        self.assertEqual(cleaned[0], user)
        self.assertEqual([m["content"] for m in cleaned[1:]], ["答复0", "答复1", "答复2"])
        self.assertTrue(history[1]["content"].endswith(tail))

    def test_history_timestamp_does_not_mutate_saved_message(self):
        original = {"role": "user", "content": "早上好", "ts": 1000}
        self.assertIn("08:00", with_history_timestamp(original)["content"])
        self.assertEqual(original["content"], "早上好")

    def test_emoji_and_similarity_helpers(self):
        with patch("text_utils.random.random", return_value=1):
            self.assertEqual(inject_emojis("你好😀"), "你好")
        self.assertTrue(is_repetitive_reply("一起 吃饭", ["一起吃饭"]))
        self.assertFalse(is_repetitive_reply("一起吃饭", ["一起吃饭"] + ["天气很好"] * 10))
        self.assertFalse(is_repetitive_reply(" ", [" "]))

    def test_polluted_tail_helpers(self):
        tail = "我会一直在这里等着你。"
        self.assertEqual(detect_polluted_tails([tail, tail]), [])
        self.assertEqual(strip_polluted_tail(f"正文\n{tail}\n{tail}", [tail]), "正文")

    def test_history_separator_is_unchanged(self):
        separator = {"role": "system", "content": "──── 新的一天 ────", "ts": 1000}
        self.assertIs(with_history_timestamp(separator), separator)
        self.assertEqual(with_history_timestamp({}), {})


if __name__ == "__main__":
    unittest.main()
