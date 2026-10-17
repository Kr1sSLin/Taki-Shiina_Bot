"""Bounded, process-safe ordering of owner changes and network dispatch.

No SQL transaction spans a network operation. The OS releases locks on crash.
Acquisition in async code is nonblocking, so cancellation cannot leak a lock.
"""
import asyncio
from contextlib import asynccontextmanager, contextmanager
from functools import wraps
import os
import time

from services.file_lock import acquire_file_lock, release_file_lock, FileLockTimeoutError
from services.foundation_store import StorageBusy


@contextmanager
def owner_fence(store, key, *, timeout=0.5):
    directory = store.path + ".owners"
    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, key + ".lock"), "a+b") as handle:
        try:
            acquire_file_lock(handle.fileno(), exclusive=True, timeout=timeout)
        except FileLockTimeoutError as error:
            raise StorageBusy("owner dispatch is busy") from error
        try:
            yield
        finally:
            release_file_lock(handle.fileno())


@asynccontextmanager
async def async_owner_fence(store, owner, *, timeout=5):
    deadline = time.monotonic() + timeout
    while True:
        manager = owner_fence(store, store.cipher.index("owner", owner), timeout=0)
        try:
            manager.__enter__()
            break
        except StorageBusy:
            if time.monotonic() >= deadline:
                raise
            await asyncio.sleep(0.01)
    try:
        yield
    finally:
        manager.__exit__(None, None, None)


def serialized_owner(kind="owner"):
    def decorate(method):
        @wraps(method)
        def wrapped(self, value, *args, **kwargs):
            if kind == "commands":
                if not value:
                    return method(self, value, *args, **kwargs)
                key = self.cipher.index("owner", value[0].owner)
            elif kind == "job":
                with self._transaction(write=False) as conn:
                    key = self._lease(conn, value)["owner"]
            else:
                key = self.cipher.index("owner", value)
            with owner_fence(self, key):
                return method(self, value, *args, **kwargs)
        return wrapped
    return decorate


async def start_guarded_call(store, owner, validate, factory):
    """Order call initiation with input/permission writes, then release the lock.

    An in-flight provider request can finish after revocation; its output still
    needs another check before any subsequent request or business commit.
    """
    task = None
    try:
        async with async_owner_fence(store, owner):
            await validate()
            task = asyncio.create_task(factory())
            await asyncio.sleep(0)
        return await task
    finally:
        if task is not None and not task.done():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
