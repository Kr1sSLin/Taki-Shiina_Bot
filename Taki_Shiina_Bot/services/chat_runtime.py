"""Application orchestration shared by HTTP and WS, with durable recovery workers."""
from services.async_storage import store_call
import asyncio
import json
import logging
import os
import time

from secure_storage import DataReadError, load_encryption_config
from services.contracts import ChatCommand
from services.domain_cipher import DomainCipher
from services.durable_worker import CommittedJob, DurableWorker
from services.foundation_store import PermissionDenied, VersionConflict
from services.owner_fence import async_owner_fence, start_guarded_call
from services.reply_check import ReplyRejected, reply_digest
from services.public_policy import PUBLIC_POLICY_VERSION, PROMPT_VERSIONS
from services.memory_policy import memory_control
from services.reliable_chat_store import ReliableChatStore
from services.daily_generation import DailyGenerator
from services.daily_store import daily_enabled
from services.proactive_generation import ProactiveGenerator
from services.proactive_store import proactive_enabled


class ChatFailed(RuntimeError):
    def __init__(self, message, *, category="generation"):
        super().__init__(message)
        self.category = category


class ChatRuntime:
    def __init__(self, store, generator, extractor, deliver, *, poll_interval=0.05):
        self.store = store
        self.generator = generator
        self.extractor = extractor
        self.deliver = deliver
        self.poll_interval = poll_interval
        self.interaction_reconciler = None
        self.delivery_available = None
        self.workers = [DurableWorker(store, {"chat": self._chat, "extract": self._extract, "deliver": self._deliver}, poll_interval=poll_interval) for _ in range(2)]
        self.daily_worker = DurableWorker(store, {"daily": self._daily}, poll_interval=max(1, poll_interval))
        self._daily_schedule_task = None
        self.proactive_worker = DurableWorker(store, {"proactive": self._proactive}, poll_interval=max(1, poll_interval))
        self._proactive_schedule_task = None

    def start(self):
        for worker in self.workers:
            worker.start()
        if daily_enabled() and getattr(self.generator, "client", None) is not None:
            self.daily_worker.start()
            if self._daily_schedule_task is None or self._daily_schedule_task.done():
                self._daily_schedule_task = asyncio.create_task(self._schedule_daily(), name="daily-scheduler")
        if proactive_enabled() and getattr(self.generator, "client", None) is not None:
            self.proactive_worker.start()
            if self._proactive_schedule_task is None or self._proactive_schedule_task.done():
                self._proactive_schedule_task = asyncio.create_task(self._schedule_proactive(), name="proactive-scheduler")

    async def stop(self):
        for task in (self._daily_schedule_task, self._proactive_schedule_task):
            if task is None or task.done():
                continue
            if task.get_loop() is asyncio.get_running_loop():
                task.cancel()
                await asyncio.gather(task, return_exceptions=True)
            elif task.get_loop().is_running():
                task.get_loop().call_soon_threadsafe(task.cancel)
        return all(await asyncio.gather(*(worker.stop() for worker in [*self.workers, self.daily_worker, self.proactive_worker])))

    async def _schedule_daily(self):
        while True:
            try:
                if daily_enabled():
                    for owner in await store_call(self.store.daily_known_owners):
                        await store_call(self.store.schedule_daily, owner)
            except Exception as error:
                logging.getLogger(__name__).warning("daily scheduling unavailable (%s)", type(error).__name__)
            await asyncio.sleep(30)

    async def _daily(self, job):
        return await DailyGenerator(self.generator.client)(job, self.store)

    async def _schedule_proactive(self):
        while True:
            try:
                if proactive_enabled():
                    for owner in await store_call(self.store.daily_known_owners):
                        await store_call(self.store.schedule_proactive, owner)
            except Exception as error:
                logging.getLogger(__name__).warning("proactive scheduling unavailable (%s)", type(error).__name__)
            await asyncio.sleep(30)

    async def _proactive(self, job):
        return await ProactiveGenerator(self.generator.client)(job, self.store)

    async def accept(self, commands, *, delay=0, merge_pending=False):
        receipts = await store_call(self.store.accept, commands, delay=delay, merge_pending=merge_pending, memory_first=True)
        self.start()
        return receipts

    async def wait(self, owner, turn_id, *, timeout=125):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = await store_call(self.store.turn_result, owner, turn_id)
            if value["status"] == "completed":
                return value["result"]
            if value["status"] in ("failed", "cancelled") or value["job_status"] in ("failed", "cancelled") or self.store.clock() >= value["deadline"]:
                raise ChatFailed("chat did not complete within its retry/deadline budget", category=value.get("failure_category", "generation"))
            await asyncio.sleep(self.poll_interval)
        raise TimeoutError("chat wait budget exceeded; retry with the same request identity")

    async def submit(self, commands, *, delay=0, merge_pending=False, timeout=125):
        receipts = await self.accept(commands, delay=delay, merge_pending=merge_pending)
        turns = list(dict.fromkeys(receipt["turn_id"] for receipt in receipts))
        return [await self.wait(commands[0].owner, identity, timeout=timeout) for identity in turns]

    async def ready(self, owner, turn_ids):
        await store_call(self.store.ready, owner, turn_ids)
        self.start()

    async def cancel(self, owner, turn_id):
        return await store_call(self.store.cancel, owner, turn_id)

    async def request(self, owner, request_id):
        return await store_call(self.store.request, owner, request_id)

    def configure_interactions(self, reconciler):
        self.interaction_reconciler = reconciler
        for worker in self.workers:
            worker.handlers["interaction_reconcile"] = self._reconcile_interaction

    async def confirm_interaction(self, owner, request_id):
        await store_call(self.store.confirm_interaction, owner, request_id)

    async def charge_interaction(self, owner, request_id, charge):
        return await store_call(self.store.charge_interaction, owner, request_id, charge)

    async def _reconcile_interaction(self, job):
        source = await store_call(self.store.begin_interaction_reconciliation, job)
        if source is not None:
            await self.interaction_reconciler(source)

    async def _chat(self, job):
        category = job["payload"].get("failure_category", "generation")
        snapshot = None
        try:
            control = await store_call(self.store.handle_memory_control, job)
            snapshot = await store_call(self.store.begin_turn, job)
            budget = snapshot["deadline"] - self.store.clock()
            result = control or await asyncio.wait_for(self.generator(snapshot, self.store), timeout=max(0.01, budget))
            if control:
                result = {**result, "check": {"approved": True, "policy_version": PUBLIC_POLICY_VERSION, "prompt_versions": dict(PROMPT_VERSIONS), "digest": reply_digest(result), "used_memory_ids": [], "fixed_reply": True}}
            category = "storage"
            committed = await store_call(self.store.commit_reply, job, snapshot, result)
        except Exception as error:
            if isinstance(error, VersionConflict) and snapshot is not None:
                if await store_call(self.store.restart_changed_turn, job, snapshot):
                    return CommittedJob()
            if isinstance(error, ReplyRejected):
                await store_call(self.store.fail_closed, job)
                return CommittedJob()
            try:
                await store_call(self.store.record_failure, job, category)
            except Exception:
                pass  # An unavailable disk cannot be used to promise diagnostics.
            raise
        return CommittedJob(committed)

    async def _extract(self, job):
        if os.getenv("MEMORY_COLLECTION_ENABLED", "0") != "1":
            return CommittedJob(await store_call(self.store.commit_extraction, job, []))
        if not await store_call(self.store.extraction_context_allowed, job):
            return CommittedJob(await store_call(self.store.commit_extraction, job, []))
        source = await store_call(self.store.source_for_job, job)
        text = (source.get("metadata") or {}).get("attachment", "") if source.get("intent") == "interaction" else source["text"]
        if (source.get("metadata") or {}).get("images") and text == "[图片]":
            text = ""  # The image placeholder is not a user assertion.
        if memory_control(text):
            return CommittedJob(await store_call(self.store.commit_extraction, job, []))
        contextual = getattr(getattr(self.extractor, "__self__", None), "extract_with_context", None)
        candidates = []
        async def validate():
            await store_call(self.store.assert_extraction_snapshot, job, candidates)
        if text.strip() and contextual:
            candidates = await store_call(self.store.extraction_candidates, job)
            # Recheck after reading controlled input and immediately before call.
            if not await store_call(self.store.extraction_context_allowed, job):
                return CommittedJob(await store_call(self.store.commit_extraction, job, []))
            facts = await asyncio.wait_for(start_guarded_call(self.store, source["owner"], validate, lambda: contextual(text, candidates, source.get("received_at"))), timeout=60)
        else:
            facts = await asyncio.wait_for(start_guarded_call(self.store, source["owner"], validate, lambda: self.extractor(text)), timeout=60) if text.strip() else []
        if not isinstance(facts, list):
            raise ValueError("fact extraction must return a list")
        return CommittedJob(await store_call(self.store.commit_extraction, job, facts))

    async def _deliver(self, job):
        delivery = await store_call(self.store.delivery, job)
        if delivery["target"] == "chat":
            async with async_owner_fence(self.store, delivery["owner"]):
                # Re-read after acquiring the process-safe fence, never dispatch
                # a copy fetched before a new input/permission transaction.
                delivery = await store_call(self.store.delivery, job)
                available = self.delivery_available
                if available is not None and not available(delivery["owner"]):
                    raise ConnectionError("no live recipient")
                await store_call(self.store.mark_dispatched, delivery["owner"], delivery["result"])
                await asyncio.wait_for(self.deliver(delivery), timeout=5)
                await store_call(self.store.mark_delivered, delivery["owner"], delivery["result"])
        else:
            await self.deliver(delivery)

    async def replay(self, owner, result):
        async with async_owner_fence(self.store, owner):
            available = self.delivery_available
            if available is not None and not available(owner):
                raise ConnectionError("no live recipient")
            await store_call(self.store.mark_dispatched, owner, result)
            await asyncio.wait_for(self.deliver({"owner": owner, "target": "chat", "result": result}), timeout=5)
            await store_call(self.store.mark_delivered, owner, result)


_runtimes = {}


def get_chat_runtime(base_dir, generator, extractor, deliver):
    path = os.path.join(os.path.abspath(base_dir), "foundation.sqlite3")
    # Once the journal exists, a business switch cannot bypass its permission
    # state or resume old JSON writes. Initial opt-in never imports old files.
    if path in _runtimes and not os.path.lexists(path):
        raise DataReadError("the existing reliable journal is unavailable")
    if os.getenv("FOUNDATION_ENABLED", "1") != "1":
        raise PermissionDenied("controlled chat is disabled")
    if path not in _runtimes:
        config = load_encryption_config()
        store = ReliableChatStore(path, DomainCipher(config.key, os.getenv("FOUNDATION_KEY_ID", "primary-v1")), require_checks=True)
        _runtimes[path] = ChatRuntime(store, generator, extractor, deliver)
    runtime = _runtimes[path]
    runtime.generator, runtime.extractor, runtime.deliver = generator, extractor, deliver
    runtime.delivery_available = getattr(deliver, "available", None)
    return runtime
