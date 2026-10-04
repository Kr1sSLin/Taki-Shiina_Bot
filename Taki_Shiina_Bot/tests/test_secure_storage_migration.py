"""Migration must not create plaintext backups or replace unbacked-up data."""

import json
import logging
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from secure_storage import DataDecryptError, EncryptionConfig, SecureJsonStore


class SecureStorageMigrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = pathlib.Path(self.temp.name) / "chat_history.json"
        self.data = [{"content": "synthetic-private-message-迁移验证", "count": 3}]
        self.original = json.dumps(self.data, ensure_ascii=False).encode("utf-8")
        self.path.write_bytes(self.original)
        self.store = SecureJsonStore(
            str(self.path), logging.getLogger(__name__),
            EncryptionConfig(b"7" * 32, auto_migrate=True, backup_on_migrate=True),
        )

    def backups(self):
        return list(self.path.parent.glob("*.bak.*"))

    def test_both_read_modes_create_recoverable_encrypted_backup(self):
        for mode in ("load", "load_strict"):
            with self.subTest(mode=mode):
                self.path.write_bytes(self.original)
                self.assertEqual(getattr(self.store, mode)([]), self.data)
                for path in [self.path, *self.backups()]:
                    self.assertNotIn(b"synthetic-private-message", path.read_bytes())
                    payload = json.loads(path.read_text(encoding="utf-8"))
                    self.assertEqual(payload["alg"], "AESGCM")
                    self.assertEqual(self.store._decrypt(payload), self.data)
        self.assertEqual(len(self.backups()), 2)

    def test_backup_write_failure_preserves_original_in_both_modes(self):
        for mode in ("load", "load_strict"):
            with self.subTest(mode=mode), patch("secure_storage.os.fsync", side_effect=OSError("disk failure")):
                if mode == "load_strict":
                    with self.assertRaises(OSError):
                        self.store.load_strict([])
                else:
                    self.assertEqual(self.store.load([]), self.data)
                self.assertEqual(self.path.read_bytes(), self.original)
                self.assertEqual(self.backups(), [])

    def test_unreadable_backup_aborts_before_original_is_replaced(self):
        with patch.object(self.store, "_decrypt", side_effect=DataDecryptError("corrupt backup")):
            with self.assertRaises(DataDecryptError):
                self.store.load_strict([])
        self.assertEqual(self.path.read_bytes(), self.original)
        self.assertEqual(self.backups(), [])

    def test_incorrect_backup_contents_abort_migration(self):
        with patch.object(self.store, "_decrypt", return_value={"wrong": "data"}):
            with self.assertRaises(DataDecryptError):
                self.store.load_strict([])
        self.assertEqual(self.path.read_bytes(), self.original)
        self.assertEqual(self.backups(), [])

    def test_replace_failure_leaves_original_and_recoverable_backup(self):
        with patch.object(self.store, "_write_atomic", side_effect=OSError("replace failure")):
            with self.assertRaises(OSError):
                self.store.load_strict([])
        self.assertEqual(self.path.read_bytes(), self.original)
        self.assertEqual(len(self.backups()), 1)
        self.assertEqual(self.store._decrypt(json.loads(self.backups()[0].read_text())), self.data)

    def test_already_encrypted_file_does_not_create_another_backup(self):
        self.store.load_strict([])
        self.assertEqual(self.store.load_strict([]), self.data)
        self.assertEqual(len(self.backups()), 1)

    def test_backup_can_be_disabled_explicitly(self):
        self.store.config = EncryptionConfig(b"7" * 32, True, False)
        self.assertEqual(self.store.load_strict([]), self.data)
        self.assertEqual(self.backups(), [])
        self.assertEqual(self.store._decrypt(json.loads(self.path.read_text())), self.data)


if __name__ == "__main__":
    unittest.main()
