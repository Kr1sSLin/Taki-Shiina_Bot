import base64
import os
import sys
import tempfile
import unittest

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

from services.history_store import HistoryStore

TEST_KEY = base64.urlsafe_b64encode(b"0" * 32).decode("utf-8").rstrip("=")


class TestHistoryStore(unittest.TestCase):
    def setUp(self):
        self._old_key = os.environ.get("DATA_ENC_KEY")
        self._old_auto = os.environ.get("DATA_AUTO_MIGRATE")
        self._old_backup = os.environ.get("DATA_BACKUP_ON_MIGRATE")
        os.environ["DATA_ENC_KEY"] = TEST_KEY
        os.environ["DATA_AUTO_MIGRATE"] = "0"
        os.environ["DATA_BACKUP_ON_MIGRATE"] = "0"

    def tearDown(self):
        if self._old_key is None:
            os.environ.pop("DATA_ENC_KEY", None)
        else:
            os.environ["DATA_ENC_KEY"] = self._old_key
        if self._old_auto is None:
            os.environ.pop("DATA_AUTO_MIGRATE", None)
        else:
            os.environ["DATA_AUTO_MIGRATE"] = self._old_auto
        if self._old_backup is None:
            os.environ.pop("DATA_BACKUP_ON_MIGRATE", None)
        else:
            os.environ["DATA_BACKUP_ON_MIGRATE"] = self._old_backup

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
