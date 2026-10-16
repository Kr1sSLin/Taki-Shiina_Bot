"""Application orchestration shared by HTTP and WS, with durable recovery workers."""
import asyncio
import json
import logging
import os
import time

from secure_storage import DataReadError, load_encryption_config
from services.contracts import ChatCommand
from services.domain_cipher import DomainCipher
from services.durable_worker import CommittedJob, DurableWorker
from services.foundation_store import PermissionDenied
from services.memory_policy import memory_control
from services.reliable_chat_store import ReliableChatStore


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
        self.workers = [DurableWorker(store, {"chat": self._chat, "extract": self._extract, "deliver": self._deliver}, poll_interval=poll_interval) for _ in range(2)]

    def start(self):
        for worker in self.workers:
            worker.start()

    async def stop(self):
        return all(await asyncio.gather(*(worker.stop() for worker in self.workers)))

    async def accept(self, commands, *, delay=0, merge_pending=False):
        receipts = await asyncio.to_thread(self.store.accept, commands, delay=delay, merge_pending=merge_pending, memory_first=True)
        self.start()
        return receipts

    async def wait(self, owner, turn_id, *, timeout=125):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            value = await asyncio.to_thread(self.store.turn_result, owner, turn_id)
            if value["status"] == "completed":
                return value["result"]
            if value["job_status"] in ("failed", "cancelled") or self.store.clock() >= value["deadline"]:
                raise ChatFailed("chat did not complete within its retry/deadline budget", category=value.get("failure_category", "generation"))
            await asyncio.sleep(self.poll_interval)
        raise TimeoutError("chat wait budget exceeded; retry with the same request identity")

    async def submit(self, commands, *, delay=0, merge_pending=False, timeout=125):
        receipts = await self.accept(commands, delay=delay, merge_pending=merge_pending)
        turns = list(dict.fromkeys(receipt["turn_id"] for receipt in receipts))
        return [await self.wait(commands[0].owner, identity, timeout=timeout) for identity in turns]

    async def ready(self, owner, turn_ids):
        await asyncio.to_thread(self.store.ready, owner, turn_ids)
        self.start()

    async def cancel(self, owner, turn_id):
        return await asyncio.to_thread(self.store.cancel, owner, turn_id)

    async def request(self, owner, request_id):
        return await asyncio.to_thread(self.store.request, owner, request_id)

    def configure_interactions(self, reconciler):
        self.interaction_reconciler = reconciler
        for worker in self.workers:
            worker.handlers["interaction_reconcile"] = self._reconcile_interaction

    async def confirm_interaction(self, owner, request_id):
        await asyncio.to_thread(self.store.confirm_interaction, owner, request_id)

    async def charge_interaction(self, owner, request_id, charge):
        return await asyncio.to_thread(self.store.charge_interaction, owner, request_id, charge)

    async def _reconcile_interaction(self, job):
        source = await asyncio.to_thread(self.store.begin_interaction_reconciliation, job)
        if source is not None:
            await self.interaction_reconciler(source)

    async def _chat(self, job):
        category = "generation"
        try:
            control = await asyncio.to_thread(self.store.handle_memory_control, job)
            snapshot = await asyncio.to_thread(self.store.begin_turn, job)
            budget = snapshot["deadline"] - self.store.clock()
            result = control or await asyncio.wait_for(self.generator(snapshot, self.store), timeout=max(0.01, budget))
            category = "storage"
            committed = await asyncio.to_thread(self.store.commit_reply, job, snapshot, result)
        except Exception:
            try:
                await asyncio.to_thread(self.store.record_failure, job, category)
            except Exception:
                pass  # An unavailable disk cannot be used to promise diagnostics.
            raise
        return CommittedJob(committed)

    async def _extract(self, job):
        if os.getenv("MEMORY_COLLECTION_ENABLED", "0") != "1":
            return CommittedJob(await asyncio.to_thread(self.store.commit_extraction, job, []))
        if not await asyncio.to_thread(self.store.extraction_context_allowed, job):
            return CommittedJob(await asyncio.to_thread(self.store.commit_extraction, job, []))
        source = await asyncio.to_thread(self.store.source_for_job, job)
        text = (source.get("metadata") or {}).get("attachment", "") if source.get("intent") == "interaction" else source["text"]
        if (source.get("metadata") or {}).get("images") and text == "[图片]":
            text = ""  # The image placeholder is not a user assertion.
        if memory_control(text):
            return CommittedJob(await asyncio.to_thread(self.store.commit_extraction, job, []))
        contextual = getattr(getattr(self.extractor, "__self__", None), "extract_with_context", None)
        if text.strip() and contextual:
            candidates = await asyncio.to_thread(self.store.extraction_candidates, job)
            # Recheck after reading controlled input and immediately before call.
            if not await asyncio.to_thread(self.store.extraction_context_allowed, job):
                return CommittedJob(await asyncio.to_thread(self.store.commit_extraction, job, []))
            facts = await asyncio.wait_for(contextual(text, candidates, source.get("received_at")), timeout=60)
        else:
            facts = await asyncio.wait_for(self.extractor(text), timeout=60) if text.strip() else []
        if not isinstance(facts, list):
            raise ValueError("fact extraction must return a list")
        return CommittedJob(await asyncio.to_thread(self.store.commit_extraction, job, facts))

    async def _deliver(self, job):
        delivery = await asyncio.to_thread(self.store.delivery, job)
        await self.deliver(delivery)


_runtimes = {}


def get_chat_runtime(base_dir, generator, extractor, deliver):
    path = os.path.join(os.path.abspath(base_dir), "foundation.sqlite3")
    # Once the journal exists, a business switch cannot bypass its permission
    # state or resume old JSON writes. Initial opt-in never imports old files.
    if path in _runtimes and not os.path.lexists(path):
        raise DataReadError("the existing reliable journal is unavailable")
    if os.getenv("FOUNDATION_ENABLED", "0") != "1" and not os.path.lexists(path):
        return None
    if path not in _runtimes:
        config = load_encryption_config()
        store = ReliableChatStore(path, DomainCipher(config.key, os.getenv("FOUNDATION_KEY_ID", "primary-v1")))
        _runtimes[path] = ChatRuntime(store, generator, extractor, deliver)
    runtime = _runtimes[path]
    runtime.generator, runtime.extractor, runtime.deliver = generator, extractor, deliver
    return runtime
