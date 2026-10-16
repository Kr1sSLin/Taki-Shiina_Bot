"""Synthetic worker failure/restart replay with no network or model calls."""
import asyncio
import copy
import pathlib
import sqlite3
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from services.durable_worker import CommittedJob, DurableWorker
from services.contracts import ChatCommand, Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import VersionConflict
from services.reliable_chat_store import ReliableChatStore


class Clock:
    now = 1000.0

    def __call__(self):
        return self.now


class ManualSleep:
    def __init__(self):
        self.waiting = asyncio.Event()
        self.steps = asyncio.Queue()
        self.delays = []

    async def __call__(self, delay):
        self.delays.append(delay)
        self.waiting.set()
        await self.steps.get()


class SyntheticStore:
    """Thread-safe lease semantics; timing advances only under test control."""

    def __init__(self, clock, *, kinds=("extract",), max_attempts=3):
        self.clock = clock
        self.lock = threading.RLock()
        self.jobs = [{"job_id": f"job-{i}", "payload": {"kind": kind}, "attempt": 0,
                      "status": "pending", "available": clock(), "max_attempts": max_attempts}
                     for i, kind in enumerate(kinds)]
        self.finishes = []
        self.claim_kinds = []
        self.thread_ids = []
        self.renewed = threading.Event()
        self.renew_error = None
        self.finish_error = None

    def claim(self, *, kinds, lease_seconds):
        with self.lock:
            self.thread_ids.append(threading.get_ident())
            self.claim_kinds.append(kinds)
            for job in self.jobs:
                if job["payload"]["kind"] not in kinds:
                    continue
                expired = job["status"] == "running" and job["lease_until"] <= self.clock()
                if expired and job["attempt"] >= job["max_attempts"]:
                    job["status"] = "failed"
                if (job["status"] in ("pending", "retry_wait") and job["available"] <= self.clock()) or (expired and job["status"] != "failed"):
                    job["attempt"] += 1
                    job.update(status="running", lease_token=f"token-{job['attempt']}", lease_until=self.clock() + lease_seconds)
                    return copy.deepcopy(job)
        return None

    def _current(self, job_id, token):
        job = next(job for job in self.jobs if job["job_id"] == job_id)
        if job["status"] != "running" or job["lease_token"] != token or job["lease_until"] <= self.clock():
            raise VersionConflict("synthetic lease lost")
        return job

    def finish(self, job_id, token, *, succeeded, retry_delay):
        with self.lock:
            self.thread_ids.append(threading.get_ident())
            if self.finish_error:
                raise self.finish_error
            job = self._current(job_id, token)
            self.finishes.append(succeeded)
            job["status"] = "succeeded" if succeeded else ("failed" if job["attempt"] >= job["max_attempts"] else "retry_wait")
            job["available"] = self.clock() + retry_delay

    def renew(self, job_id, token, *, lease_seconds):
        with self.lock:
            self.thread_ids.append(threading.get_ident())
            if self.renew_error:
                raise self.renew_error
            self._current(job_id, token)["lease_until"] = self.clock() + lease_seconds
            self.renewed.set()


class DurableWorkerTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.clock = Clock()
        self.store = SyntheticStore(self.clock)

    def worker(self, handler, **options):
        return DurableWorker(self.store, {"extract": handler}, clock=self.clock, **options)

    async def test_success_uses_short_repository_calls_on_threads(self):
        main_thread = threading.get_ident()

        async def handler(job):
            self.assertEqual(job["job_id"], "job-0")
            return {"result": "synthetic"}

        self.assertTrue(await self.worker(handler).run_once())
        self.assertEqual(self.store.finishes, [True])
        self.assertTrue(all(identity != main_thread for identity in self.store.thread_ids))

    async def test_failure_has_finite_repository_retry_budget_and_private_logs(self):
        self.store = SyntheticStore(self.clock, max_attempts=2)
        logger = Mock()

        async def handler(job):
            raise RuntimeError("synthetic-private-marker 31.234567,121.765432")

        worker = self.worker(handler, logger=logger)
        self.assertTrue(await worker.run_once())
        self.assertEqual(self.store.jobs[0]["status"], "retry_wait")
        self.assertFalse(await worker.run_once())
        self.clock.now += 1
        self.assertTrue(await worker.run_once())
        self.assertEqual(self.store.jobs[0]["status"], "failed")
        self.clock.now += 100
        self.assertFalse(await worker.run_once())
        self.assertEqual(self.store.finishes, [False, False])
        logged = str(logger.mock_calls)
        self.assertIn("RuntimeError", logged)
        self.assertNotIn("synthetic-private-marker", logged)
        self.assertNotIn("31.234567", logged)

    async def test_cancellation_leaves_lease_recoverable_without_success(self):
        started, cancelled = asyncio.Event(), asyncio.Event()

        async def interrupted(job):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        task = asyncio.create_task(self.worker(interrupted, lease_seconds=6).run_once())
        await asyncio.wait_for(started.wait(), 1)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertTrue(cancelled.is_set())
        self.assertEqual(self.store.finishes, [])

        async def recovered(job):
            self.assertEqual(job["attempt"], 2)

        self.clock.now += 7
        self.assertTrue(await self.worker(recovered).run_once())
        self.assertEqual(self.store.finishes, [True])

    async def test_double_workers_compete_for_one_lease(self):
        started, release = asyncio.Event(), asyncio.Event()
        calls = []

        async def handler(job):
            calls.append(job["job_id"])
            started.set()
            await release.wait()

        first = asyncio.create_task(self.worker(handler).run_once())
        await asyncio.wait_for(started.wait(), 1)
        self.assertFalse(await self.worker(handler).run_once())
        release.set()
        self.assertTrue(await first)
        self.assertEqual(calls, ["job-0"])
        self.assertEqual(self.store.finishes, [True])

    async def test_renewal_retains_lease_during_long_model_call(self):
        started, release = asyncio.Event(), asyncio.Event()
        sleeper = ManualSleep()

        async def handler(job):
            started.set()
            await release.wait()

        worker = self.worker(handler, lease_seconds=6, renew_interval=2, sleep=sleeper)
        task = asyncio.create_task(worker.run_once())
        await asyncio.wait_for(started.wait(), 1)
        await asyncio.wait_for(sleeper.waiting.wait(), 1)
        self.clock.now += 4
        sleeper.steps.put_nowait(None)
        self.assertTrue(await asyncio.to_thread(self.store.renewed.wait, 1))
        self.clock.now += 3  # Past the original lease, inside its renewed lease.
        self.assertFalse(await self.worker(handler).run_once())
        release.set()
        self.assertTrue(await task)
        self.assertEqual(self.store.jobs[0]["attempt"], 1)
        self.assertEqual(self.store.finishes, [True])
        self.assertEqual(sleeper.delays[0], 2)

    async def test_renewal_failure_cancels_local_handler_and_never_finishes(self):
        sleeper = ManualSleep()
        cancelled = asyncio.Event()
        self.store.renew_error = VersionConflict("sensitive body must not be logged")

        async def handler(job):
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        task = asyncio.create_task(self.worker(handler, sleep=sleeper).run_once())
        await asyncio.wait_for(sleeper.waiting.wait(), 1)
        sleeper.steps.put_nowait(None)
        self.assertTrue(await asyncio.wait_for(task, 1))
        self.assertTrue(cancelled.is_set())
        self.assertEqual(self.store.finishes, [])

    async def test_atomic_commit_marker_does_not_finish_a_second_time(self):
        async def handler(job):
            await asyncio.to_thread(self.store.finish, job["job_id"], job["lease_token"], succeeded=True, retry_delay=0)
            return CommittedJob({"fact_id": "synthetic-fact"})

        self.assertTrue(await self.worker(handler).run_once())
        self.assertEqual(self.store.finishes, [True])

    async def test_unknown_kinds_remain_available_for_their_own_worker(self):
        self.store = SyntheticStore(self.clock, kinds=("deliver", "extract"))

        async def handler(job):
            self.assertEqual(job["payload"]["kind"], "extract")

        self.assertTrue(await self.worker(handler).run_once())
        self.assertEqual(self.store.jobs[0]["status"], "pending")
        self.assertEqual(self.store.claim_kinds, [("extract",)])
        self.assertFalse(await DurableWorker(self.store, {}).run_once())
        self.assertEqual(len(self.store.claim_kinds), 1)

    async def test_start_is_idempotent_and_stop_cancels_active_work(self):
        started, cancelled = asyncio.Event(), asyncio.Event()

        async def handler(job):
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()

        worker = self.worker(handler)
        first = worker.start()
        self.assertIs(first, worker.start())
        await asyncio.wait_for(started.wait(), 1)
        self.assertTrue(await worker.stop(timeout=1))
        self.assertTrue(cancelled.is_set())
        self.assertEqual(self.store.finishes, [])
        self.assertTrue(await worker.stop(timeout=0))

    async def test_stop_is_bounded_even_when_handler_defers_cancellation(self):
        started, cancelled, release = asyncio.Event(), asyncio.Event(), asyncio.Event()

        async def handler(job):
            started.set()
            try:
                await asyncio.Event().wait()
            except asyncio.CancelledError:
                cancelled.set()
                await release.wait()

        worker = self.worker(handler)
        task = worker.start()
        await asyncio.wait_for(started.wait(), 1)
        self.assertFalse(await worker.stop(timeout=0.01))
        await asyncio.wait_for(cancelled.wait(), 1)
        self.assertEqual(self.store.finishes, [])
        release.set()
        with self.assertRaises(asyncio.CancelledError):
            await asyncio.wait_for(task, 1)

    async def test_finish_storage_failure_leaves_job_for_lease_recovery(self):
        self.store.finish_error = OSError("sensitive disk error")
        logger = Mock()

        async def handler(job):
            return "synthetic"

        self.assertTrue(await self.worker(handler, logger=logger).run_once())
        self.assertEqual(self.store.jobs[0]["status"], "running")
        self.assertEqual(self.store.finishes, [])
        self.assertIn("OSError", str(logger.mock_calls))
        self.assertNotIn("sensitive disk error", str(logger.mock_calls))

    async def test_expired_handler_cannot_acknowledge_a_new_workers_lease(self):
        newer_lease = None

        async def stale_handler(job):
            nonlocal newer_lease
            self.clock.now += 31
            newer_lease = await asyncio.to_thread(self.store.claim, kinds=("extract",), lease_seconds=30)

        logger = Mock()
        self.assertTrue(await self.worker(stale_handler, logger=logger).run_once())
        self.assertEqual(newer_lease["attempt"], 2)
        self.assertEqual(self.store.finishes, [])
        self.assertEqual(self.store.jobs[0]["lease_token"], newer_lease["lease_token"])
        self.assertIn("VersionConflict", str(logger.mock_calls))

    async def test_background_claim_error_recovers_on_next_poll(self):
        sleeper = ManualSleep()
        completed = threading.Event()
        logger = Mock()
        original_claim = self.store.claim
        original_finish = self.store.finish
        first_claim = True

        def claim(**kwargs):
            nonlocal first_claim
            if first_claim:
                first_claim = False
                raise OSError("private disk details")
            return original_claim(**kwargs)

        self.store.claim = claim

        def finish(*args, **kwargs):
            original_finish(*args, **kwargs)
            completed.set()

        self.store.finish = finish

        async def handler(job):
            return None

        worker = self.worker(handler, sleep=sleeper, logger=logger)
        worker.start()
        await asyncio.wait_for(sleeper.waiting.wait(), 1)
        sleeper.steps.put_nowait(None)
        self.assertTrue(await asyncio.to_thread(completed.wait, 1))
        self.assertTrue(await worker.stop(timeout=1))
        self.assertEqual(self.store.finishes, [True])
        self.assertIn("OSError", str(logger.mock_calls))
        self.assertNotIn("private disk details", str(logger.mock_calls))

    async def test_invalid_timing_is_rejected_before_start(self):
        async def handler(job):
            pass

        for options in ({"lease_seconds": 0}, {"renew_interval": 30}, {"poll_interval": 0},
                        {"retry_delay": -1}, {"lease_seconds": float("inf")}, {"renew_interval": float("nan")}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                self.worker(handler, **options)


class WorkerRepositoryIntegrationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-worker-synthetic-")
        self.addCleanup(self.temp.cleanup)
        self.clock = Clock()
        self.path = pathlib.Path(self.temp.name) / "worker.db"
        self.store = ReliableChatStore(str(self.path), DomainCipher(b"s" * 32), clock=self.clock)
        self.receipt = self.store.accept([ChatCommand("synthetic-owner", "request-1", "client-1", "http", "synthetic input")])[0]

    def job_statuses(self):
        conn = sqlite3.connect(self.path)
        try:
            return [row[0] for row in conn.execute("SELECT status FROM jobs")]
        finally:
            conn.close()

    async def test_chat_handler_atomically_commits_result_before_marker(self):
        self.store.finish = Mock(wraps=self.store.finish)

        async def handler(job):
            snapshot = await asyncio.to_thread(self.store.begin_turn, job)
            result = await asyncio.to_thread(self.store.commit_reply, job, snapshot, {"reply": "synthetic reply"})
            return CommittedJob(result)

        worker = DurableWorker(self.store, {"chat": handler})
        self.assertTrue(await worker.run_once())
        self.assertFalse(await worker.run_once())
        self.store.finish.assert_not_called()
        result = self.store.turn_result("synthetic-owner", self.receipt["turn_id"])
        self.assertEqual(result["status"], "completed")
        self.assertEqual(result["job_status"], "succeeded")
        self.assertEqual(len(self.store.history("synthetic-owner")), 2)
        self.assertIn("pending", self.job_statuses())  # Extract/outbox remain for their own handlers.

    async def test_postcommit_exception_cannot_repeat_chat_or_rewrite_success(self):
        logger = Mock()

        async def handler(job):
            snapshot = await asyncio.to_thread(self.store.begin_turn, job)
            await asyncio.to_thread(self.store.commit_reply, job, snapshot, {"reply": "synthetic reply"})
            raise RuntimeError("synthetic interruption after atomic commit")

        worker = DurableWorker(self.store, {"chat": handler}, logger=logger)
        self.assertTrue(await worker.run_once())
        self.assertFalse(await worker.run_once())
        result = self.store.turn_result("synthetic-owner", self.receipt["turn_id"])
        self.assertEqual(result["job_status"], "succeeded")
        self.assertEqual(len(self.store.history("synthetic-owner")), 2)
        self.assertIn("VersionConflict", str(logger.mock_calls))

    async def test_permission_changed_during_model_call_rejects_reply_and_retries(self):
        self.store.put_fact("synthetic-owner", "synthetic-fact", Domain.USER_FACT,
                            {"text": "synthetic fact", "enabled": True}, expected_revision=0, targets=())
        started, release = asyncio.Event(), asyncio.Event()

        async def handler(job):
            snapshot = await asyncio.to_thread(self.store.begin_turn, job)
            started.set()
            await release.wait()  # A model call holds no write transaction.
            result = await asyncio.to_thread(self.store.commit_reply, job, snapshot, {"reply": "stale reply"})
            return CommittedJob(result)

        task = asyncio.create_task(DurableWorker(self.store, {"chat": handler}).run_once())
        await asyncio.wait_for(started.wait(), 1)
        await asyncio.to_thread(self.store.set_permission, "synthetic-owner", "synthetic-fact", enabled=False, expected_revision=1, targets=())
        release.set()
        self.assertTrue(await asyncio.wait_for(task, 1))
        result = self.store.turn_result("synthetic-owner", self.receipt["turn_id"])
        self.assertEqual(result["job_status"], "retry_wait")
        self.assertIsNone(result["result"])
        self.assertEqual(self.store.history("synthetic-owner"), [])


if __name__ == "__main__":
    unittest.main()
