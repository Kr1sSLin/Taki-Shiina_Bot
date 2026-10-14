"""当前文本清洗契约；不再要求已退役的分气泡接口。"""

import unittest
from unittest.mock import patch

from text_utils import (
    clean_short_term_history, detect_polluted_tails, inject_emojis,
    is_repetitive_reply, sanitize_taki_reply, strip_polluted_tail,
    with_history_timestamp,
)


class TestTextUtils(unittest.TestCase):
    def test_sanitize_preserves_natural_lines_and_removes_stage_directions(self):
        with patch("text_utils.random.random", return_value=1):
            self.assertEqual(sanitize_taki_reply("[08-22 17:22] *挥手*你好！\n（轻轻点头）一起吃饭？"),
                             "你好！\n一起吃饭？")

    def test_sanitize_does_not_split_sentences(self):
        self.assertEqual(sanitize_taki_reply("第一句。第二句。第三句。"), "第一句。第二句。第三句。")

    def test_inject_emojis_can_be_deterministic(self):
        with patch("text_utils.random.random", return_value=1):
            self.assertEqual(inject_emojis("你好😀"), "你好")

    def test_similarity_ignores_whitespace_and_only_uses_last_ten(self):
        self.assertTrue(is_repetitive_reply("一起 吃饭", ["一起吃饭"]))
        self.assertFalse(is_repetitive_reply("一起吃饭", ["一起吃饭"] + ["天气很好"] * 10))
        self.assertFalse(is_repetitive_reply(" ", [" "]))

    def test_polluted_tail_requires_repetition_and_preserves_user(self):
        tail = "我会一直在这里等着你。"
        messages = [{"role": "user", "content": tail}] + [
            {"role": "assistant", "content": f"{i}\n{tail}", "ts": i} for i in range(3)
        ]
        cleaned, tails = clean_short_term_history(messages)
        self.assertIn(tail, tails)
        self.assertEqual(cleaned[0], messages[0])
        self.assertEqual([m["content"] for m in cleaned[1:]], ["0", "1", "2"])
        self.assertTrue(messages[1]["content"].endswith(tail))
        self.assertEqual(detect_polluted_tails([tail, tail]), [])
        self.assertEqual(strip_polluted_tail(f"正文\n{tail}\n{tail}", [tail]), "正文")

    def test_clean_history_drops_empty_assistant_only(self):
        tail = "我会一直在这里等着你。"
        messages = [{"role": "assistant", "content": tail}] * 3
        self.assertEqual(clean_short_term_history(messages)[0], [])

    def test_timestamp_is_beijing_and_does_not_mutate_history(self):
        message = {"role": "user", "content": "早上好", "ts": 1000}
        self.assertEqual(with_history_timestamp(message)["content"], "【01-01 08:00】早上好")
        self.assertEqual(message["content"], "早上好")
        separator = {"role": "system", "content": "──── 新的一天 ────", "ts": 1000}
        self.assertIs(with_history_timestamp(separator), separator)
        self.assertEqual(with_history_timestamp({}), {})


if __name__ == "__main__":
    unittest.main()
