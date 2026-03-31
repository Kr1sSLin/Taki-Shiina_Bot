import os
import sys
import unittest

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

from text_utils import split_into_bubbles


class TestTextUtils(unittest.TestCase):
    def test_split_into_bubbles_by_newline(self):
        text = "第一句\n第二句\n第三句"
        result = split_into_bubbles(text)
        self.assertEqual(result, ["第一句", "第二句", "第三句"])

    def test_split_into_bubbles_by_punctuation(self):
        text = "你吃饭了吗？我还没。等会一起吃。"
        result = split_into_bubbles(text)
        self.assertGreaterEqual(len(result), 2)

    def test_split_into_bubbles_three_sentences_keep_split(self):
        text = "第一句。第二句。第三句。"
        result = split_into_bubbles(text)
        self.assertEqual(result, ["第一句。", "第二句。", "第三句。"])

    def test_split_into_bubbles_enforce_min_sentence_bubbles(self):
        text = "第一句。第二句。第三句。"
        result = split_into_bubbles(text, min_sentence_bubbles=3)
        self.assertEqual(len(result), 3)

    def test_split_into_bubbles_no_cap(self):
        text = "。".join(["一句很长的话"] * 10)
        result = split_into_bubbles(text)
        self.assertGreaterEqual(len(result), 5)

    def test_split_into_bubbles_join_clause_with_space(self):
        text = "第一小句，第二小句，第三小句，第四小句"
        result = split_into_bubbles(text, max_chars_per_bubble=16)
        self.assertTrue(any(" " in bubble for bubble in result))


if __name__ == "__main__":
    unittest.main()
