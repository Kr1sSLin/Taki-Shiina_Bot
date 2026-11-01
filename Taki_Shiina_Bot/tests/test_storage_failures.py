"""F-A01: failed reads/writes must not become empty state or success."""
import base64
import json
import logging
import os
import threading
import time
import pathlib
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from secure_storage import (
    DataCorruptionError, DataDecryptError, DataReadError, DataWriteError,
    EncryptionConfig, SecureJsonStore, load_encryption_config,
)
from services.history_store import HistoryStore
from services.memory_service import MemoryService
from services.progress_store import ProgressStore
from services.file_lock import FileLockTimeoutError, acquire_file_lock, release_file_lock


class StorageFailures(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = pathlib.Path(self.temp.name) / "store.json"
        self.store = SecureJsonStore(str(self.path), logging.getLogger(__name__), EncryptionConfig(b"x" * 32, False, False))

    def test_only_missing_file_returns_default(self):
        self.assertEqual(self.store.load({}), {})
        self.assertFalse(self.path.exists())
        self.path.write_text("{broken", encoding="utf-8")
        for mode in ("load", "load_strict"):
            with self.assertRaises(DataCorruptionError):
                getattr(self.store, mode)({})
        with self.assertRaises(DataCorruptionError):
            self.store.save({"replacement": True})
        self.assertEqual(self.path.read_text(), "{broken")

    def test_wrong_key_never_overwrites(self):
        self.store.save({"original": True})
        original = self.path.read_bytes()
        other = SecureJsonStore(str(self.path), logging.getLogger(__name__), EncryptionConfig(b"y" * 32, False, False))
        for operation in (lambda: other.load({}), lambda: other.save({})):
            with self.assertRaises(DataDecryptError):
                operation()
        self.assertEqual(self.path.read_bytes(), original)

    def test_invalid_envelopes_are_never_treated_as_plaintext(self):
        for payload in ({"ver": 2, "alg": "AESGCM", "nonce": "", "ciphertext": ""}, {"alg": "AESGCM"}, {"nonce": "%%"}, {"ver": 1}):
            self.path.write_text(json.dumps(payload))
            with self.assertRaises(DataCorruptionError):
                self.store.load({})
            with self.assertRaises(DataCorruptionError):
                self.store.save({})

    def test_io_errors_are_distinct_and_not_empty(self):
        with patch("secure_storage.open", side_effect=PermissionError("synthetic private path")):
            with self.assertRaises(DataReadError) as raised:
                self.store.load({})
        self.assertNotIn("private", str(raised.exception))

    def test_failed_replace_and_fsync_preserve_original_and_remove_temp(self):
        self.store.save({"original": True})
        original = self.path.read_bytes()
        for target in ("secure_storage.os.replace", "secure_storage.os.fsync"):
            with patch(target, side_effect=OSError("synthetic disk failure")):
                with self.assertRaises(DataWriteError):
                    self.store.save({"replacement": True})
            self.assertEqual(self.path.read_bytes(), original)
            self.assertEqual(list(self.path.parent.glob("*.tmp")), [])

    def test_invalid_root_is_rejected(self):
        self.store.save([])
        with self.assertRaises(DataCorruptionError):
            self.store.load({})

    def test_history_and_memory_propagate_failure_and_keep_memory_state(self):
        key = base64.urlsafe_b64encode(b"x" * 32).decode()
        with patch.dict("os.environ", {"DATA_ENC_KEY": key, "DATA_AUTO_MIGRATE": "0"}):
            history = HistoryStore(str(self.path))
            self.path.write_text("broken")
            with self.assertRaises(DataCorruptionError):
                history.load()
            memory_path = pathlib.Path(self.temp.name) / "user_memories.json"
            memory_path.write_text("broken")
            with self.assertRaises(DataCorruptionError):
                MemoryService(self.temp.name)
            memory_path.unlink()
            memory = MemoryService(self.temp.name)
            with patch.object(memory.store, "save_strict", side_effect=DataWriteError("disk")):
                with self.assertRaises(DataWriteError):
                    memory.update_profile("owner", "synthetic statement")
            self.assertEqual(memory.data, {})

    def test_progress_invalid_version_and_fields_do_not_reset_state(self):
        key = base64.urlsafe_b64encode(b"x" * 32).decode()
        with patch.dict("os.environ", {"DATA_ENC_KEY": key, "DATA_AUTO_MIGRATE": "0"}):
            progress = ProgressStore(str(self.path), logging.getLogger(__name__))
            for payload in ({"version": 999}, {"meta": []}):
                self.path.write_text(json.dumps(payload))
                original = self.path.read_bytes()
                with self.assertRaises(DataCorruptionError):
                    with progress.transaction():
                        pass
                self.assertEqual(self.path.read_bytes(), original)

    def test_progress_lock_timeout_is_bounded_and_does_not_write(self):
        key = base64.urlsafe_b64encode(b"x" * 32).decode()
        with patch.dict("os.environ", {"DATA_ENC_KEY": key, "DATA_AUTO_MIGRATE": "0"}):
            progress = ProgressStore(str(self.path), logging.getLogger(__name__), lock_timeout=0.05)
            fd = os.open(progress._lock_path, os.O_CREAT | os.O_RDWR, 0o600)
            try:
                acquire_file_lock(fd, exclusive=True, timeout=1)
                for operation in (progress.snapshot, lambda: self._transaction(progress)):
                    start = time.monotonic()
                    with self.assertRaises(FileLockTimeoutError):
                        operation()
                    self.assertLess(time.monotonic() - start, 0.5)
                self.assertFalse(self.path.exists())
            finally:
                release_file_lock(fd)
                os.close(fd)
            held, release = threading.Event(), threading.Event()
            def hold():
                with progress._thread_lock:
                    held.set()
                    release.wait(2)
            holder = threading.Thread(target=hold)
            holder.start()
            try:
                self.assertTrue(held.wait(1))
                with self.assertRaises(FileLockTimeoutError):
                    progress.snapshot()
            finally:
                release.set()
                holder.join(2)
            with progress.transaction() as data:
                self.assertIsInstance(data, dict)

    @staticmethod
    def _transaction(progress):
        with progress.transaction():
            pass

    def test_hex_key_configuration(self):
        with patch.dict("os.environ", {"DATA_ENC_KEY": (b"x" * 32).hex()}):
            self.assertEqual(load_encryption_config().key, b"x" * 32)
