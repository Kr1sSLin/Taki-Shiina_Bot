"""Regression tests for cross-process-safe timeline transactions."""

from __future__ import annotations

import base64
import os
import sys
import tempfile
import threading
import multiprocessing
import stat
import time
import unittest

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

from services.timeline_store import TimelineStore  # noqa: E402
from services.file_lock import FileLockTimeoutError, acquire_file_lock, release_file_lock  # noqa: E402
from secure_storage import SecureJsonStore  # noqa: E402

TEST_KEY = base64.urlsafe_b64encode(b"7" * 32).decode().rstrip("=")
OTHER_KEY = base64.urlsafe_b64encode(b"9" * 32).decode().rstrip("=")


def _process_writer(path: str, prefix: str, count: int, key: str) -> None:
    """Spawn-safe worker: a distinct process performs full timeline transactions."""
    os.environ["DATA_ENC_KEY"] = key
    os.environ["DATA_AUTO_MIGRATE"] = "0"
    os.environ["DATA_BACKUP_ON_MIGRATE"] = "0"
    store = TimelineStore(path)
    for index in range(count):
        store.append([{"messageId": f"{prefix}-{index}"}])


class TimelineStoreTestCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = os.path.join(self.tmp.name, "timeline.json")
        self.previous = os.environ.get("DATA_ENC_KEY")
        self.addCleanup(self._restore_key)
        os.environ["DATA_ENC_KEY"] = TEST_KEY

    def _restore_key(self):
        if self.previous is None:
            os.environ.pop("DATA_ENC_KEY", None)
        else:
            os.environ["DATA_ENC_KEY"] = self.previous

    def test_two_instances_concurrent_append_does_not_lose_data(self):
        stores = [TimelineStore(self.path), TimelineStore(self.path)]
        barrier = threading.Barrier(2)
        errors = []

        def writer(index):
            try:
                barrier.wait(timeout=5)
                for item in range(100):
                    stores[index].append([{"messageId": f"{index}-{item}"}])
            except Exception as exc:  # pragma: no cover - surfaced by assertion
                errors.append(exc)

        threads = [threading.Thread(target=writer, args=(index,)) for index in range(2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=30)
        self.assertFalse(any(thread.is_alive() for thread in threads), "concurrent append hung")
        self.assertEqual(errors, [])
        snapshot = stores[0].snapshot()
        self.assertEqual(len(snapshot), 200)
        self.assertEqual(len({item["messageId"] for item in snapshot}), 200)

    def test_two_processes_concurrent_append_does_not_lose_data(self):
        context = multiprocessing.get_context("spawn")
        processes = [
            context.Process(target=_process_writer, args=(self.path, str(index), 40, TEST_KEY))
            for index in range(2)
        ]
        for process in processes:
            process.start()
        for process in processes:
            process.join(timeout=60)
        self.assertFalse(any(process.is_alive() for process in processes), "process append hung")
        self.assertEqual([process.exitcode for process in processes], [0, 0])
        snapshot = TimelineStore(self.path).snapshot()
        self.assertEqual(len(snapshot), 80)
        self.assertEqual(len({item["messageId"] for item in snapshot}), 80)

    def test_append_trims_to_last_3000(self):
        store = TimelineStore(self.path)
        store.append([{"messageId": str(i)} for i in range(3050)])
        snapshot = store.snapshot()
        self.assertEqual(len(snapshot), 3000)
        self.assertEqual(snapshot[0]["messageId"], "50")
        self.assertEqual(snapshot[-1]["messageId"], "3049")

    def test_thread_lock_wait_is_bounded(self):
        store = TimelineStore(self.path, lock_timeout=0.05)
        entered, release = threading.Event(), threading.Event()

        def holder():
            with store._thread_lock:
                entered.set()
                release.wait(timeout=2)

        thread = threading.Thread(target=holder)
        thread.start()
        try:
            self.assertTrue(entered.wait(timeout=1))
            started = time.monotonic()
            with self.assertRaises(FileLockTimeoutError):
                store.snapshot()
            self.assertLess(time.monotonic() - started, 0.5)
        finally:
            release.set()
            thread.join(timeout=3)

    def test_nonfinite_lock_timeouts_are_rejected(self):
        for timeout in (float("nan"), float("inf"), -1):
            with self.subTest(timeout=timeout), self.assertRaises(ValueError):
                TimelineStore(self.path, lock_timeout=timeout)

    def test_read_failure_does_not_overwrite_existing_file(self):
        SecureJsonStore(self.path, __import__("logging").getLogger(__name__)).save_strict([{"messageId": "old"}])
        with open(self.path, "rb") as handle:
            before = handle.read()
        os.environ["DATA_ENC_KEY"] = OTHER_KEY
        mismatched = TimelineStore(self.path)
        with self.assertRaises(Exception):
            mismatched.append([{"messageId": "new"}])
        with open(self.path, "rb") as handle:
            self.assertEqual(handle.read(), before)
        os.environ["DATA_ENC_KEY"] = TEST_KEY
        self.assertEqual(TimelineStore(self.path).snapshot(), [{"messageId": "old"}])

    def test_lock_timeout_is_bounded_and_lock_file_is_private(self):
        holder = TimelineStore(self.path, lock_timeout=1)
        holder.append([{"messageId": "seed"}])
        lock_fd = os.open(f"{self.path}.lock", os.O_CREAT | os.O_RDWR, 0o600)
        acquire_file_lock(lock_fd, exclusive=True)
        try:
            blocked = TimelineStore(self.path, lock_timeout=0.05)
            started = time.monotonic()
            with self.assertRaises(FileLockTimeoutError):
                blocked.append([{"messageId": "never-written"}])
            self.assertLess(time.monotonic() - started, 1.0)
        finally:
            release_file_lock(lock_fd)
            os.close(lock_fd)
        if os.name == "posix":
            mode = stat.S_IMODE(os.stat(f"{self.path}.lock").st_mode)
            self.assertEqual(mode, 0o600)
        self.assertEqual(holder.snapshot(), [{"messageId": "seed"}])


if __name__ == "__main__":
    unittest.main()
