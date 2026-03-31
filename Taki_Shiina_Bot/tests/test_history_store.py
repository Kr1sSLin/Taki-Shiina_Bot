import os
import sys
import tempfile
import unittest

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

from services.history_store import HistoryStore


class TestHistoryStore(unittest.TestCase):
    def test_load_returns_empty_when_missing(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            history_file = os.path.join(tmp_dir, "chat_history.json")
            store = HistoryStore(history_file)
            self.assertEqual(store.load(), {})

    def test_save_and_load_round_trip(self):
        with tempfile.TemporaryDirectory() as tmp_dir:
            history_file = os.path.join(tmp_dir, "chat_history.json")
            store = HistoryStore(history_file)

            payload = {
                "123": [
                    {"role": "user", "content": "hi"},
                    {"role": "assistant", "content": "hello"},
                ]
            }
            store.save(payload)
            loaded = store.load()
            self.assertEqual(loaded, payload)


if __name__ == "__main__":
    unittest.main()
