"""Strict, cross-process-safe storage for append-only encrypted timelines."""

from __future__ import annotations

import asyncio
import copy
import logging
import math
import os
import threading
import time
from typing import Any

from secure_storage import SecureJsonStore
from services.file_lock import FileLockTimeoutError, acquire_file_lock, release_file_lock


class TimelineStore:
    """Serialize strict load/append/save transactions across threads and processes."""

    def __init__(
        self,
        path: str,
        logger: logging.Logger | None = None,
        *,
        max_items: int = 3000,
        lock_timeout: float | None = None,
    ):
        self.path = path
        self.logger = logger or logging.getLogger(__name__)
        self.max_items = max_items
        if lock_timeout is None:
            lock_timeout = float(os.getenv("TIMELINE_LOCK_TIMEOUT_SECONDS", "10"))
        if not math.isfinite(lock_timeout) or lock_timeout < 0:
            raise ValueError("timeline lock timeout must be finite and non-negative")
        self.lock_timeout = lock_timeout
        self.store = SecureJsonStore(path, self.logger)
        self._thread_lock = threading.RLock()
        self._lock_path = f"{path}.lock"

    def _locked(self, operation, *, exclusive: bool):
        deadline = time.monotonic() + self.lock_timeout
        if not self._thread_lock.acquire(timeout=self.lock_timeout):
            raise FileLockTimeoutError("timeline thread lock timed out")
        try:
            fd = os.open(self._lock_path, os.O_CREAT | os.O_RDWR, 0o600)
            locked = False
            try:
                os.chmod(self._lock_path, 0o600)
                acquire_file_lock(fd, exclusive=exclusive, timeout=max(0, deadline - time.monotonic()))
                locked = True
                return operation()
            finally:
                try:
                    if locked:
                        release_file_lock(fd)
                finally:
                    os.close(fd)
        finally:
            self._thread_lock.release()

    def snapshot(self) -> list[dict[str, Any]]:
        """Return a consistent strict snapshot; corruption/key mismatch propagates."""
        def load():
            data = self.store.load_strict([])
            if not isinstance(data, list):
                raise ValueError(f"时间线根节点必须是列表: {self.path}")
            return copy.deepcopy(data)

        return self._locked(load, exclusive=False)

    def append(self, items: list[dict[str, Any]]) -> None:
        """Atomically perform strict load → extend → trim → strict save."""
        pending = copy.deepcopy(items)

        def transact():
            data = self.store.load_strict([])
            if not isinstance(data, list):
                raise ValueError(f"时间线根节点必须是列表: {self.path}")
            data.extend(pending)
            if len(data) > self.max_items:
                data = data[-self.max_items :]
            self.store.save_strict(data)

        self._locked(transact, exclusive=True)

    async def snapshot_async(self) -> list[dict[str, Any]]:
        """Acquire the blocking file lock on a worker thread."""
        return await asyncio.to_thread(self.snapshot)

    async def append_async(self, items: list[dict[str, Any]]) -> None:
        """Run the complete blocking append transaction on a worker thread."""
        await asyncio.to_thread(self.append, items)
