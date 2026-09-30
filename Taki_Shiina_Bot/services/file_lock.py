"""Cross-platform blocking file-lock primitives shared by JSON stores.

Callers must execute potentially contended lock acquisition outside an asyncio event
loop (for example via ``asyncio.to_thread``). Do not acquire the same lock path
recursively through a second file descriptor.
"""

from __future__ import annotations

import errno
import math
import os
import time

try:  # POSIX
    import fcntl
except ImportError:  # pragma: no cover - Windows
    fcntl = None  # type: ignore[assignment]

try:  # Windows
    import msvcrt
except ImportError:  # pragma: no cover - POSIX
    msvcrt = None  # type: ignore[assignment]

_WIN_LOCK_OFFSET = 0
_WIN_LOCK_BYTES = 1
_LOCK_RETRY_INTERVAL = 0.02
_LOCK_RETRY_ERRNOS = frozenset(
    code
    for code in (
        getattr(errno, "EACCES", None),
        getattr(errno, "EAGAIN", None),
        getattr(errno, "EDEADLOCK", None),
    )
    if code is not None
)


class FileLockTimeoutError(TimeoutError):
    """Raised when a bounded file-lock acquisition reaches its deadline."""


def acquire_file_lock(fd: int, *, exclusive: bool, timeout: float | None = None) -> None:
    """Acquire a process-wide lock, optionally bounded by ``timeout`` seconds.

    ``timeout=None`` preserves the original blocking behavior. POSIX supports
    shared reads; Windows upgrades shared requests to an exclusive byte lock.
    """
    if timeout is not None and (not math.isfinite(timeout) or timeout < 0):
        raise ValueError("file lock timeout must be finite and non-negative")
    if timeout is None:
        if fcntl is not None:
            fcntl.flock(fd, fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH)
            return
        if msvcrt is None:  # pragma: no cover - unsupported platform
            raise RuntimeError("当前平台既无 fcntl 也无 msvcrt，无法实现跨进程文件锁")

    deadline = None if timeout is None else time.monotonic() + timeout
    while True:
        try:
            if fcntl is not None:
                operation = fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH
                fcntl.flock(fd, operation | fcntl.LOCK_NB)
            else:
                os.lseek(fd, _WIN_LOCK_OFFSET, os.SEEK_SET)
                msvcrt.locking(fd, msvcrt.LK_NBLCK, _WIN_LOCK_BYTES)
            return
        except (BlockingIOError, OSError) as exc:
            if not isinstance(exc, BlockingIOError) and exc.errno not in _LOCK_RETRY_ERRNOS:
                raise
            if deadline is not None and time.monotonic() >= deadline:
                raise FileLockTimeoutError("timed out acquiring file lock") from exc
            delay = _LOCK_RETRY_INTERVAL
            if deadline is not None:
                delay = min(delay, max(0.0, deadline - time.monotonic()))
            time.sleep(delay)


def release_file_lock(fd: int) -> None:
    """Release a lock acquired by :func:`acquire_file_lock`."""
    if fcntl is not None:
        fcntl.flock(fd, fcntl.LOCK_UN)
        return
    if msvcrt is None:  # pragma: no cover - unsupported platform
        return
    os.lseek(fd, _WIN_LOCK_OFFSET, os.SEEK_SET)
    msvcrt.locking(fd, msvcrt.LK_UNLCK, _WIN_LOCK_BYTES)
