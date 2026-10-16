"""Lease-based execution; model calls never run inside a storage transaction.

Handlers with durable business effects must commit those effects and job completion
in one repository transaction, then return ``CommittedJob``. Other handlers must be
idempotent: losing a process after an external effect can cause a later retry.
Only explicitly registered kinds are claimed; importing this module starts nothing.
"""
from __future__ import annotations

import asyncio
from dataclasses import dataclass
import logging
import math
import time
from typing import Awaitable, Callable, Mapping


@dataclass(frozen=True)
class CommittedJob:
    """The repository already committed the business result and terminal status.

    This also covers an atomic permission-based cancellation with no effect.
    """

    result: object = None


class DurableWorker:
    def __init__(
        self,
        store,
        handlers: Mapping[str, Callable[[dict], Awaitable[object]]],
        *,
        lease_seconds: float = 30,
        renew_interval: float | None = None,
        poll_interval: float = 1,
        retry_delay: float = 1,
        sleep=asyncio.sleep,
        clock=time.monotonic,
        logger=None,
    ):
        renew_interval = lease_seconds / 3 if renew_interval is None else renew_interval
        for value in (lease_seconds, renew_interval, poll_interval, retry_delay):
            if not math.isfinite(value):
                raise ValueError("worker timing must be finite")
        if not 0 < lease_seconds <= 300 or not 0 < renew_interval < lease_seconds:
            raise ValueError("renewal must occur within the bounded lease")
        if poll_interval <= 0 or not 0 <= retry_delay <= 3600:
            raise ValueError("invalid worker wait duration")
        if any(not isinstance(kind, str) or not kind or not callable(handler) for kind, handler in handlers.items()):
            raise ValueError("worker requires named callable handlers")
        self.store = store
        self.handlers = dict(handlers)
        self.lease_seconds = lease_seconds
        self.renew_interval = renew_interval
        self.poll_interval = poll_interval
        self.retry_delay = retry_delay
        self.sleep = sleep
        self.clock = clock
        self.logger = logger or logging.getLogger(__name__)
        self._task = None
        self._stopping = asyncio.Event()
        self._run_lock = asyncio.Lock()

    def _log_failure(self, phase, error):
        # Exception messages/tracebacks may contain model input or coordinates.
        self.logger.warning("durable worker %s failed (%s)", phase, type(error).__name__)

    def start(self):
        """Start once in the current event loop and return its background task."""
        if self._task is None or self._task.done():
            self._stopping.clear()
            self._task = asyncio.create_task(self._run(), name="durable-worker")
        return self._task

    async def stop(self, *, timeout=5):
        """Cancel local work and wait at most timeout; its lease remains retryable.

        Returns False if an uncooperative handler still needs to finish cancelling.
        A cancelled to_thread operation may finish its short transaction; no new
        success is requested for a cancelled handler.
        """
        if not math.isfinite(timeout) or timeout < 0:
            raise ValueError("invalid worker shutdown timeout")
        self._stopping.set()
        task = self._task
        if task is None:
            return True
        if not task.done():
            task.cancel()
            await asyncio.wait({task}, timeout=timeout)
        if task.done() and not task.cancelled():
            task.exception()  # Retrieve any unexpected runner exception.
        return task.done()

    async def _run(self):
        while not self._stopping.is_set():
            try:
                claimed = await self.run_once()
            except asyncio.CancelledError:
                raise
            except Exception as error:
                self._log_failure("claim", error)
                claimed = False
            if not claimed and not self._stopping.is_set():
                await self.sleep(self.poll_interval)

    async def run_once(self):
        """Run at most one eligible job; return False when no registered job exists."""
        if not self.handlers:
            return False
        async with self._run_lock:
            job = await asyncio.to_thread(
                self.store.claim, kinds=tuple(self.handlers), lease_seconds=self.lease_seconds
            )
            if job is None:
                return False
            handler = self.handlers.get(job["payload"].get("kind"))
            if handler is None:
                # A repository contract violation must never acknowledge unknown work.
                self._log_failure("unregistered kind", ValueError())
                return True
            await self._execute(job, handler)
            return True

    async def _renew(self, job):
        deadline = self.clock() + self.renew_interval
        while True:
            await self.sleep(max(0, deadline - self.clock()))
            await asyncio.to_thread(
                self.store.renew,
                job["job_id"],
                job["lease_token"],
                lease_seconds=self.lease_seconds,
            )
            deadline = self.clock() + self.renew_interval

    @staticmethod
    async def _cancel_tasks(*tasks):
        for task in tasks:
            if not task.done():
                task.cancel()
        return await asyncio.gather(*tasks, return_exceptions=True)

    async def _execute(self, job, handler):
        async def invoke():
            return await handler(job)

        work = asyncio.create_task(invoke(), name="durable-handler")
        renewal = asyncio.create_task(self._renew(job), name="durable-renewal")
        try:
            done, _ = await asyncio.wait({work, renewal}, return_when=asyncio.FIRST_COMPLETED)
            if work not in done:
                # Every renewal failure closes this local execution. In particular,
                # a stale token must not commit or mark a new worker's job failed.
                try:
                    renewal.result()
                except Exception as error:
                    self._log_failure("renewal", error)
                return
            error = None
            try:
                result = work.result()
            except asyncio.CancelledError:
                raise
            except Exception as failure:
                error = failure
                result = None
            if isinstance(result, CommittedJob):
                return
            renewal_result, = await self._cancel_tasks(renewal)
            if isinstance(renewal_result, Exception):
                self._log_failure("renewal", renewal_result)
                return
            if error is not None:
                self._log_failure("handler", error)
            try:
                await asyncio.to_thread(
                    self.store.finish,
                    job["job_id"],
                    job["lease_token"],
                    succeeded=error is None,
                    retry_delay=self.retry_delay,
                )
            except Exception as failure:
                # Includes lost lease, lock/disk failure, or uncertain completion.
                # Recovery and retry budget remain the repository's responsibility.
                self._log_failure("finish", failure)
        finally:
            await self._cancel_tasks(work, renewal)
