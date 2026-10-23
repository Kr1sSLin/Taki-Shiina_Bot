"""Real synthetic SQL plus the existing points ledger; no external model or push."""
import asyncio
import os
import threading
from datetime import date
import json
import pathlib
import sqlite3
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from gamification_fixture import TEST_USER, make_rig
from handlers.interaction_handler import CODE_STORAGE_FAILED_REFUNDED, parse_timer_instruction
from services.chat_runtime import ChatRuntime
from services.chat_runtime import ChatFailed
from services.contracts import ChatCommand, Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import VersionConflict
from services.progress_store import PointsStore, REASON_ITEM_REFUND, REASON_ITEM_SEND, REASON_DAILY_FIRST_CHAT, REASON_STREAK_3_DAY
from services.reliable_chat_store import ReliableChatStore
from services.text_generation import TextGenerator
from controlled_model_fixture import checker_response
from services.points_service import InsufficientPointsError, InteractionLimitError, LedgerReconciliationError
from services.interaction_config_service import ConfigUnavailableError
from services.file_lock import FileLockTimeoutError, acquire_file_lock, release_file_lock


class SafePrompt:
    def __init__(self):
        self.allowed = True
        self.calls = []
        self.permission_guard = SimpleNamespace(owner_context_allowed=lambda owner: self.allowed)

    async def get_system_prompt(self, owner, *, allow_private_context=True):
        self.calls.append(allow_private_context)
        return "public-character" + (" private-profile-marker" if allow_private_context else "")


class ReliableInteractionTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp, self.rig = make_rig(ai_outcomes=["啧，收下了。"], retry_rules={"max_retries": 2, "total_timeout_seconds": 1.5, "attempt_timeout_seconds": 0.2})
        with self.rig.gamification.points_store.transaction() as data:
            PointsStore.ensure_account(data, TEST_USER)["balance"] = 20
        self.store = ReliableChatStore(str(pathlib.Path(self.temp.name) / "foundation.sqlite3"), DomainCipher(b"r" * 32))
        self.prompt = SafePrompt()
        self.messages = []
        create = self.rig.client.completions.create

        async def recorded_create(**kwargs):
            check = checker_response(kwargs)
            if check:
                return check
            self.messages.append(kwargs["messages"])
            return await create(**kwargs)

        self.rig.client.completions.create = recorded_create
        self.generator = TextGenerator(self.rig.state, self.rig.history_store, self.prompt, self.rig.handler.weather_service,
                                       self.rig.client, parse_timer_instruction,
                                       interaction_retry_provider=lambda: self.rig.gamification.config.get_points_rules()["deepseek_retry"])
        self.deliveries = []

        async def extract(text):
            return []

        async def deliver(value):
            self.deliveries.append(value)

        self.runtime = ChatRuntime(self.store, self.generator, extract, deliver, poll_interval=0.005)
        for worker in self.runtime.workers:
            worker.retry_delay = 0
        self.rig.handler.runtime_provider = lambda: self.runtime
        self.runtime.configure_interactions(self.rig.handler.reconcile_reliable)

    async def asyncTearDown(self):
        await self.runtime.stop()
        self.temp.cleanup()

    def ledger_count(self, reason):
        return sum(entry["reason_code"] == reason for entry in self.rig.ledger())

    async def test_success_and_restart_replay_use_one_charge_and_sql_result(self):
        first = await self.rig.handler.send(TEST_USER, "coffee", "gift-1", attachment="synthetic attachment")
        self.assertTrue(first.ok)
        self.assertEqual(first.balance, 16)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.rig.timeline, [])
        self.assertEqual(self.rig.history_store.save_count, 0)
        self.assertEqual(len(self.store.history(TEST_USER)), 2)
        self.rig.handler._recent_results.clear()
        # Changes after acceptance cannot alter the original template/item snapshot.
        items = self.rig.gamification.config.get_config()["interaction_items"]
        self.rig.gamification.config.update_sections({"interaction_items": [{**item, "cost_points": 9, "is_active": False} if item["id"] == "coffee" else item for item in items]})
        replay = await self.rig.handler.send(TEST_USER, "coffee", "gift-1", attachment="synthetic attachment")
        self.assertTrue(replay.ok)
        self.assertTrue(replay.duplicate)
        self.assertEqual(replay.message_id, first.message_id)
        self.assertEqual(replay.reply, first.reply)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(len(self.messages), 1)
        source = self.store.request(TEST_USER, "gift-1")["source"]
        self.assertEqual(source["metadata"]["item"]["cost_points"], 5)
        self.assertNotIn("history", source["metadata"])
        self.assertIn("interaction", self.store.turn_result(TEST_USER, self.store.request(TEST_USER, "gift-1")["turn_id"])["result"])

    async def test_pending_text_and_gift_share_one_reply_without_duplicate_sources(self):
        self.store.accept([ChatCommand(TEST_USER, "text-1", "text-1", "device", "synthetic pending text", channel="ws")], delay=20, merge_pending=True)

        async def flush(owner):
            return [{"requestId": "text-1", "content": "synthetic pending text"}]

        self.rig.handler.pending_flush_waiter = flush
        result = await self.rig.handler.send(TEST_USER, "coffee", "gift-merge")
        self.assertTrue(result.ok)
        self.assertEqual(result.merged_request_ids, ["text-1"])
        self.assertEqual(len(self.messages), 1)
        self.assertEqual(len(self.store.history(TEST_USER)), 3)
        self.assertIn("synthetic pending text", str(self.messages[0]))
        self.assertEqual(self.store.request(TEST_USER, "text-1")["turn_id"], self.store.request(TEST_USER, "gift-merge")["turn_id"])

    async def test_unpaid_rejections_never_cancel_or_move_pending_text(self):
        self.rig.client.completions.outcomes = ["原文字的回复。"] * 4
        for index, error in enumerate((InsufficientPointsError("竞争后余额不足"), InteractionLimitError("限次"), LedgerReconciliationError("核账"), ConfigUnavailableError("配置"))):
            with self.subTest(error=type(error).__name__):
                text_id = f"unpaid-text-{index}"
                receipt = self.store.accept([ChatCommand(TEST_USER, text_id, text_id, "device", "原文字", channel="ws")], delay=20, merge_pending=True)[0]
                method = "interaction_snapshot" if isinstance(error, ConfigUnavailableError) else "deduct_for_item"
                target = self.rig.gamification.config if method == "interaction_snapshot" else self.rig.points
                with patch.object(target, method, side_effect=error):
                    result = await self.rig.handler.send(TEST_USER, "coffee", f"unpaid-gift-{index}")
                self.assertFalse(result.ok)
                self.assertFalse(result.refunded)
                self.assertEqual(self.store.request(TEST_USER, text_id)["turn_id"], receipt["turn_id"])
                self.assertEqual(self.store.turn_result(TEST_USER, receipt["turn_id"])["status"], "pending")
                await self.runtime.ready(TEST_USER, [receipt["turn_id"]])
                reply = await self.runtime.wait(TEST_USER, receipt["turn_id"], timeout=5)
                self.assertEqual(reply["requestIds"], [text_id])
                self.store.mark_dispatched(TEST_USER, reply)
        self.assertEqual(len(self.messages), 4)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 0)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)

    async def test_paid_mixed_failure_survives_restart_and_original_id_replay(self):
        text = self.store.accept([ChatCommand(TEST_USER, "mixed-text", "mixed-text", "device", "保留原文字", channel="ws")], delay=20, merge_pending=True)[0]
        self.rig.client.completions.outcomes = [RuntimeError("合成失败")] * 3
        result = await self.rig.handler.send(TEST_USER, "coffee", "mixed-gift")
        self.assertFalse(result.ok)
        self.assertTrue(result.refunded)
        self.assertEqual(set(result.operation["requestIds"]), {"mixed-text", "mixed-gift"})
        self.assertEqual(result.operation["recoveryAction"], "resend_new_request")
        self.assertEqual(self.store.request(TEST_USER, "mixed-gift")["turn_id"], text["turn_id"])
        self.assertEqual(set(self.rig.events_of("bot.error")[-1]["payload"]["requestIds"]), {"mixed-text", "mixed-gift"})
        await self.runtime.stop()
        self.reopen_runtime()
        operation = self.store.request(TEST_USER, "mixed-text")["operation"]
        self.assertEqual(operation["refundStatus"], "refunded")
        self.assertEqual(operation["sources"][0]["content"], "保留原文字")
        self.assertEqual(self.store.failed_operations("other-owner"), [])
        replay = await self.rig.handler.send(TEST_USER, "coffee", "mixed-gift")
        self.assertFalse(replay.ok)
        self.assertEqual(replay.operation, operation)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)
        self.rig.client.completions.outcomes = ["新的文字回复。"]
        resent = (await self.runtime.submit([ChatCommand(TEST_USER, "new-text", "new-text", "device", "保留原文字")]))[0]
        self.assertEqual(resent["requestIds"], ["new-text"])
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)

    async def test_confirm_attachment_failure_refunds_gift_without_touching_text(self):
        receipt = self.store.accept([ChatCommand(TEST_USER, "safe-text", "safe-text", "device", "原文", channel="ws")], delay=20, merge_pending=True)[0]
        with patch.object(self.store, "confirm_interaction", side_effect=OSError("合成挂接失败")):
            result = await self.rig.handler.send(TEST_USER, "coffee", "attach-failed")
        self.assertFalse(result.ok)
        self.assertTrue(result.refunded)
        self.assertEqual(self.store.turn_result(TEST_USER, receipt["turn_id"])["status"], "pending")
        await self.runtime.ready(TEST_USER, [receipt["turn_id"]])
        self.assertEqual((await self.runtime.wait(TEST_USER, receipt["turn_id"], timeout=5))["requestIds"], ["safe-text"])

    async def test_completed_text_is_not_answered_again_with_gift(self):
        self.rig.client.completions.outcomes = ["文字回复。", "礼物回复。"]
        text = ChatCommand(TEST_USER, "old-text", "old-text", "device", "previous text", channel="ws")
        original = (await self.runtime.submit([text]))[0]
        self.store.mark_dispatched(TEST_USER, original)
        gift = await self.rig.handler.send(TEST_USER, "coffee", "gift-after", attachment="new gift input")
        self.assertTrue(gift.ok)
        self.assertEqual(gift.merged_request_ids, [])
        self.assertEqual(len(self.messages), 2)
        self.assertEqual(self.store.turn_result(TEST_USER, self.store.request(TEST_USER, "old-text")["turn_id"])["result"]["messageId"], original["messageId"])

    async def test_accept_write_failure_never_charges_and_retry_can_succeed(self):
        with patch.object(self.store, "accept", side_effect=OSError("synthetic write failure")):
            failed = await self.rig.handler.send(TEST_USER, "coffee", "gift-write")
        self.assertFalse(failed.ok)
        self.assertEqual(failed.error_code, CODE_STORAGE_FAILED_REFUNDED)
        self.assertFalse(failed.refunded)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 0)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)
        self.assertEqual(self.store.history(TEST_USER), [])
        self.assertFalse(any(event.get("payload", {}).get("done") for _, event in self.rig.events))
        replay = await self.rig.handler.send(TEST_USER, "coffee", "gift-write")
        self.assertFalse(replay.duplicate)
        self.assertTrue(replay.ok)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)

    async def test_reply_commit_write_failure_cannot_publish_success(self):
        self.rig.client.completions.outcomes = ["合成回复。"] * 3
        # The assertion is about a reached SQL commit failure. The shared 1.5 s
        # model deadline can expire first under full-suite load and report a
        # generation timeout before the patched commit is ever attempted.
        rules = self.rig.gamification.config.get_points_rules()
        self.rig.gamification.config.update_sections({"points_rules": {
            **rules, "deepseek_retry": {**rules["deepseek_retry"], "total_timeout_seconds": 5}
        }})
        with patch.object(self.store, "commit_reply", side_effect=OSError("synthetic disk failure")) as commit:
            result = await self.rig.handler.send(TEST_USER, "coffee", "gift-commit", attachment="synthetic")
        self.assertGreater(commit.call_count, 0)
        self.assertFalse(result.ok)
        self.assertTrue(result.refunded)
        self.assertEqual(result.error_code, CODE_STORAGE_FAILED_REFUNDED)
        self.assertEqual(self.store.history(TEST_USER), [])
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)
        self.assertEqual(self.ledger_count(REASON_DAILY_FIRST_CHAT), 0)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertFalse(any(value.get("target") == "chat" for value in self.deliveries))

    async def test_timeout_racing_committed_reply_returns_result_without_refund(self):
        wait = self.runtime.wait

        async def completed_then_timeout(*args, **kwargs):
            await wait(*args, **kwargs)
            raise TimeoutError("waiter lost its completed response")

        self.runtime.wait = completed_then_timeout
        result = await self.rig.handler.send(TEST_USER, "coffee", "gift-race", attachment="synthetic")
        self.assertTrue(result.ok)
        self.assertIsNotNone(result.message_id)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 16)

    async def test_retry_after_charge_before_accept_resumes_without_second_charge(self):
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "gift-charge")
        item = self.rig.gamification.config.get_item("coffee")
        metadata = {"item": {key: item.get(key) for key in ("id", "name", "icon", "cost_points")}, "attachment": "", "prompt_template": self.rig.gamification.config.render_prompt(item)}
        self.store.accept([ChatCommand(TEST_USER, "gift-charge", "interaction_user_gift-charge", "device", "☕ 咖啡", channel="ws", intent="interaction", metadata=metadata)], delay=20)
        result = await self.rig.handler.send(TEST_USER, "coffee", "gift-charge")
        self.assertTrue(result.ok)
        self.assertTrue(result.duplicate)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)

    async def test_disabled_sql_or_legacy_context_cannot_reenter_gift_prompt(self):
        self.rig.state.user_chat_history[TEST_USER] = [{"role": "user", "content": "disabled-old-history-marker"}]
        self.store.put_fact(TEST_USER, "disabled-fact", Domain.USER_FACT, {"text": "disabled-fact-marker", "enabled": False}, expected_revision=0, targets=())
        self.prompt.allowed = False
        result = await self.rig.handler.send(TEST_USER, "coffee", "gift-private", attachment="current-gift-input")
        self.assertTrue(result.ok)
        model_input = str(self.messages[0])
        for forbidden in ("disabled-old-history-marker", "disabled-fact-marker", "private-profile-marker"):
            self.assertNotIn(forbidden, model_input)
        self.assertIn("public-character", model_input)
        self.assertIn("current-gift-input", model_input)
        self.assertEqual(self.prompt.calls, [False])

    async def test_legacy_guard_alone_quarantines_old_history(self):
        self.rig.state.user_chat_history[TEST_USER] = [{"role": "user", "content": "disabled-legacy-marker"}]
        self.prompt.allowed = False
        result = await self.rig.handler.send(TEST_USER, "coffee", "gift-oldguard", attachment="current input")
        self.assertTrue(result.ok)
        self.assertNotIn("disabled-legacy-marker", str(self.messages[0]))

    async def test_legacy_prompt_preview_obeys_the_same_history_permission_guard(self):
        self.rig.handler.runtime_provider = None
        self.rig.handler.prompt_service = self.prompt
        self.rig.state.user_chat_history[TEST_USER] = [{"role": "user", "content": "disabled-preview-marker"}]
        self.prompt.allowed = False
        preview = await self.rig.handler.build_prompt_preview(TEST_USER, "coffee", attachment="current input")
        self.assertNotIn("disabled-preview-marker", str(preview["messages"]))
        self.assertNotIn("private-profile-marker", str(preview["messages"]))
        self.assertIn("current input", str(preview["messages"]))

    async def test_legacy_request_cache_does_not_replay_another_owners_result(self):
        self.rig.handler.runtime_provider = None
        self.rig.client.completions.outcomes = ["第一位用户的回复。", "第二位用户的回复。"]
        other = "synthetic-other-owner"
        with self.rig.gamification.points_store.transaction() as data:
            PointsStore.ensure_account(data, other)["balance"] = 20
        first = await self.rig.handler.send(TEST_USER, "coffee", "same-client-key")
        second = await self.rig.handler.send(other, "coffee", "same-client-key")
        self.assertTrue(first.ok)
        self.assertTrue(second.ok)
        self.assertEqual(second.user_id, other)
        self.assertFalse(second.duplicate)
        self.assertNotEqual(second.message_id, first.message_id)
        self.assertIsNotNone(self.rig.points.find_ledger_entry(other, f"item_send:{other}:same-client-key"))

    async def test_disconnected_waiter_does_not_refund_an_accepted_gift(self):
        started, release = asyncio.Event(), asyncio.Event()
        create = self.rig.client.completions.create

        async def delayed_create(**kwargs):
            started.set()
            await release.wait()
            return await create(**kwargs)

        self.rig.client.completions.create = delayed_create
        waiting = asyncio.create_task(self.rig.handler.send(TEST_USER, "coffee", "gift-disconnect", attachment="synthetic"))
        try:
            # This is a worker-scheduling guard, not a one-second product SLA.
            # Full-suite Windows disk/thread scheduling can exceed one second.
            await asyncio.wait_for(started.wait(), 5)
            waiting.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await waiting
            self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)
            release.set()
            turn = self.store.request(TEST_USER, "gift-disconnect")["turn_id"]
            durable = await self.runtime.wait(TEST_USER, turn, timeout=5)
            replay = await self.rig.handler.send(TEST_USER, "coffee", "gift-disconnect", attachment="synthetic")
            self.assertEqual(replay.message_id, durable["messageId"])
            self.assertTrue(replay.duplicate)
            self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
            self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)
        finally:
            release.set()

    async def test_reusing_request_with_different_input_is_rejected_without_second_charge(self):
        first = await self.rig.handler.send(TEST_USER, "coffee", "gift-collision", attachment="original")
        self.assertTrue(first.ok)
        second = await self.rig.handler.send(TEST_USER, "coffee", "gift-collision", attachment="changed")
        self.assertFalse(second.ok)
        self.assertEqual(second.error_code, 40200)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)

    def accepted_gift(self, request_id, retry_rules=None):
        item = self.rig.gamification.config.get_item("coffee")
        metadata = {"item": {key: item.get(key) for key in ("id", "name", "icon", "cost_points")},
                    "attachment": "", "prompt_template": self.rig.gamification.config.render_prompt(item), "business_date": "2026-10-16"}
        if retry_rules is not None:
            metadata["retry_rules"] = retry_rules
        receipt = self.store.accept([ChatCommand(TEST_USER, request_id, "interaction_user_" + request_id, "device", "☕ 咖啡",
                                                 channel="ws", intent="interaction", metadata=metadata)])[0]
        return receipt

    async def wait_for_reconciliation(self, turn_id):
        original_now = self.store.clock()
        self.store.clock = lambda: original_now + 16
        self.runtime.start()
        with self.assertRaises(ChatFailed):
            await self.runtime.wait(TEST_USER, turn_id, timeout=1)
        await self.wait_for_reconciliation_completion(turn_id)

    async def wait_for_reconciliation_completion(self, turn_id):
        async with asyncio.timeout(2):
            while True:
                conn = sqlite3.connect(self.store.path)
                try:
                    jobs = conn.execute("SELECT id,owner,status,body FROM jobs").fetchall()
                finally:
                    conn.close()
                for identity, owner, status, body in jobs:
                    payload = self.store.cipher.decrypt(Domain.JOB, owner, identity, body)
                    if payload.get("kind") == "interaction_reconcile" and payload.get("turn_id") == turn_id:
                        self.assertNotEqual(status, "failed", "reconciliation exhausted its retry budget")
                        if status == "succeeded":
                            return
                await asyncio.sleep(0.005)

    async def test_crash_after_intent_before_charge_cancels_without_creating_money(self):
        receipt = self.accepted_gift("crash-before-charge")
        self.assertIsNone(self.store.claim(kinds=("chat",)))
        await self.wait_for_reconciliation(receipt["turn_id"])
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 0)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)
        self.assertEqual(self.store.history(TEST_USER), [])

    async def test_crash_after_charge_before_confirm_refunds_once_after_restart(self):
        receipt = self.accepted_gift("crash-after-charge")
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "crash-after-charge")
        await self.wait_for_reconciliation(receipt["turn_id"])
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)
        replay = await self.rig.handler.send(TEST_USER, "coffee", "crash-after-charge")
        self.assertFalse(replay.ok)
        self.assertTrue(replay.duplicate)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)

    async def test_crash_after_confirmation_recovers_the_original_reply(self):
        receipt = self.accepted_gift("crash-confirmed")
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "crash-confirmed")
        self.store.confirm_interaction(TEST_USER, "crash-confirmed")
        self.runtime.start()
        # Bound recovery without conflating SQL worker scheduling with an SLA.
        result = await self.runtime.wait(TEST_USER, receipt["turn_id"], timeout=5)
        await self.wait_for_reconciliation_completion(receipt["turn_id"])
        self.assertIsNotNone(result["messageId"])
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 16)

    async def test_reconciliation_retry_after_refund_before_cancel_does_not_add_money_again(self):
        receipt = self.accepted_gift("crash-refund")
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "crash-refund")
        cancel = self.runtime.cancel
        calls = 0

        async def fail_cancel_once(*args, **kwargs):
            nonlocal calls
            calls += 1
            if calls == 1:
                raise OSError("synthetic crash after refund")
            return await cancel(*args, **kwargs)

        self.runtime.cancel = fail_cancel_once
        await self.wait_for_reconciliation(receipt["turn_id"])
        self.assertGreaterEqual(calls, 2)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)

    async def test_cancelled_payment_gate_rejects_a_late_deduction_callback(self):
        receipt = self.accepted_gift("late-charge")
        self.store.cancel(TEST_USER, receipt["turn_id"])
        callback = Mock(side_effect=lambda: self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "late-charge"))
        with self.assertRaises(VersionConflict):
            await self.runtime.charge_interaction(TEST_USER, "late-charge", callback)
        callback.assert_not_called()
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 0)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)

    async def test_cancelled_charge_and_refund_wait_for_thread_before_recovery(self):
        for action in ("charge", "refund"):
            for phase in ("file_wait", "save"):
                with self.subTest(action=action, phase=phase):
                    request_id = f"cancel-{action}-{phase}"
                    self.accepted_gift(request_id)
                    if action == "refund":
                        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, request_id)
                    entered, release = threading.Event(), threading.Event()
                    points_store = self.rig.gamification.points_store
                    original_save = points_store._save
                    def save(data):
                        if phase == "save":
                            entered.set()
                            if not release.wait(2):
                                raise TimeoutError("synthetic save release")
                        return original_save(data)
                    fd = None
                    if phase == "file_wait":
                        fd = os.open(points_store._lock_path, os.O_CREAT | os.O_RDWR, 0o600)
                        acquire_file_lock(fd, exclusive=True, timeout=1)
                    def charge():
                        entered.set() if phase == "file_wait" else None
                        return self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, request_id)
                    async def refund():
                        entered.set() if phase == "file_wait" else None
                        return await self.rig.handler._refund_charge(TEST_USER, {"id": "coffee", "name": "咖啡"}, request_id, 5)
                    with patch.object(points_store, "_save", side_effect=save):
                        task = asyncio.create_task(self.runtime.charge_interaction(TEST_USER, request_id, charge) if action == "charge" else refund())
                        try:
                            self.assertTrue(await asyncio.to_thread(entered.wait, 1))
                            task.cancel()
                            await asyncio.sleep(0)
                            task.cancel()
                            await asyncio.sleep(0.02)
                            self.assertFalse(task.done(), "线程未结束时不能传播取消并启动冲突补偿")
                        finally:
                            release.set()
                            if fd is not None:
                                release_file_lock(fd)
                                os.close(fd)
                        with self.assertRaises(asyncio.CancelledError):
                            await task
                    # 恢复始终按真实账本补偿；重放不能再扣或再退。
                    source = (await self.runtime.request(TEST_USER, request_id))["source"]
                    source["turn_id"] = (await self.runtime.request(TEST_USER, request_id))["turn_id"]
                    await self.rig.handler.reconcile_reliable(source)
                    await self.rig.handler.reconcile_reliable(source)
                    entries = [entry for entry in self.rig.ledger() if entry["idempotency_key"].endswith(request_id)]
                    self.assertEqual(sum(entry["reason_code"] == REASON_ITEM_SEND for entry in entries), 1)
                    self.assertEqual(sum(entry["reason_code"] == REASON_ITEM_REFUND for entry in entries), 1)
                    self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)

    async def test_payment_lock_timeout_is_storage_failure_not_ai_timeout(self):
        with patch.object(self.runtime, "charge_interaction", side_effect=FileLockTimeoutError("private lock path")):
            result = await self.rig.handler.send(TEST_USER, "coffee", "lock-timeout", attachment="synthetic")
        self.assertFalse(result.ok)
        self.assertEqual(result.error_code, CODE_STORAGE_FAILED_REFUNDED)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 0)
        self.assertEqual(result.operation["failureCategory"], "storage")

    async def test_sql_failure_after_json_charge_refunds_only_that_real_charge(self):
        with patch.object(self.store, "_confirm_payment", side_effect=OSError("synthetic SQL commit failure")):
            result = await self.rig.handler.send(TEST_USER, "coffee", "charge-then-sql-fail", attachment="synthetic")
        self.assertFalse(result.ok)
        self.assertTrue(result.refunded)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.store.history(TEST_USER), [])

    def reopen_runtime(self, *, generator=None):
        previous = self.runtime
        self.store = ReliableChatStore(str(pathlib.Path(self.temp.name) / "foundation.sqlite3"), DomainCipher(b"r" * 32))
        self.runtime = ChatRuntime(self.store, generator or previous.generator, previous.extractor, previous.deliver, poll_interval=0.005)
        self.runtime.configure_interactions(self.rig.handler.reconcile_reliable)
        for worker in self.runtime.workers:
            worker.retry_delay = 0

    async def wait_for_refund_without_a_request_waiter(self):
        now = self.store.clock()
        self.store.clock = lambda: now + 16
        self.runtime.start()
        async with asyncio.timeout(2):
            while self.ledger_count(REASON_ITEM_REFUND) != 1:
                await asyncio.sleep(0.005)

    async def test_confirmed_gift_that_fails_after_restart_refunds_without_original_waiter(self):
        receipt = self.accepted_gift("confirmed-model-failed")
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "confirmed-model-failed")
        self.store.confirm_interaction(TEST_USER, "confirmed-model-failed")

        async def always_fails(snapshot, store):
            raise RuntimeError("synthetic model unavailable")

        self.reopen_runtime(generator=always_fails)
        await self.wait_for_refund_without_a_request_waiter()
        await self.wait_for_reconciliation_completion(receipt["turn_id"])
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)
        self.assertEqual(self.store.history(TEST_USER), [])
        self.assertEqual(self.store.turn_result(TEST_USER, receipt["turn_id"])["job_status"], "cancelled")

    async def test_cancel_then_crash_before_refund_recovers_without_original_waiter(self):
        receipt = self.accepted_gift("cancel-then-crash")
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "cancel-then-crash")
        self.store.confirm_interaction(TEST_USER, "cancel-then-crash")
        self.store.cancel(TEST_USER, receipt["turn_id"])
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)
        self.reopen_runtime()
        await self.wait_for_refund_without_a_request_waiter()
        await self.wait_for_reconciliation_completion(receipt["turn_id"])
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)
        self.assertEqual(self.store.history(TEST_USER), [])

    async def test_generation_budget_is_persisted_across_retry_restart_and_expiry(self):
        now = [1000.0]
        self.store.clock = lambda: now[0]
        receipt = self.accepted_gift("fixed-generation-budget")
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "fixed-generation-budget")
        self.store.confirm_interaction(TEST_USER, "fixed-generation-budget")
        now[0] += 8  # Debounce time precedes the model budget.
        first = self.store.claim(kinds=("chat",), lease_seconds=30)
        original_snapshot = self.store.begin_turn(first)
        deadline = original_snapshot["deadline"]
        self.assertEqual(deadline, 1023.0)
        self.store.finish(first["job_id"], first["lease_token"], succeeded=False, retry_delay=0)
        now[0] += 2
        self.reopen_runtime()
        self.store.clock = lambda: now[0]
        retried = self.store.claim(kinds=("chat",), lease_seconds=30)
        self.assertEqual(retried["attempt"], 2)
        resumed_snapshot = self.store.begin_turn(retried)
        self.assertEqual(resumed_snapshot["deadline"], deadline)
        conn = sqlite3.connect(self.store.path)
        try:
            owner, stored_deadline, body = conn.execute("SELECT owner,deadline,body FROM chat_turns WHERE id=?", (receipt["turn_id"],)).fetchone()
        finally:
            conn.close()
        stored_body = self.store.cipher.decrypt(Domain.DERIVED, owner, receipt["turn_id"], body)
        self.assertEqual(stored_deadline, deadline)
        self.assertEqual(stored_body["generation_deadline"], deadline)
        now[0] = deadline + 1  # Its current lease is still valid; the budget has expired.
        with self.assertRaises(VersionConflict):
            self.store.begin_turn(retried)
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(retried, resumed_snapshot, {"reply": "late synthetic reply"})
        self.assertEqual(self.store.history(TEST_USER), [])
        self.runtime.start()
        await self.wait_for_reconciliation_completion(receipt["turn_id"])
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 20)
        self.assertEqual(self.ledger_count(REASON_ITEM_SEND), 1)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 1)

    async def test_configured_sixty_second_budget_survives_restart_and_config_change(self):
        now = [1000.0]
        self.store.clock = lambda: now[0]
        receipt = self.accepted_gift("configured-budget", {"total_timeout_seconds": 60, "attempt_timeout_seconds": 20, "max_retries": 5})
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "configured-budget")
        self.store.confirm_interaction(TEST_USER, "configured-budget")
        job = self.store.claim(kinds=("chat",))
        snapshot = self.store.begin_turn(job)
        self.assertEqual(snapshot["deadline"], 1060)
        self.store.finish(job["job_id"], job["lease_token"], succeeded=False, retry_delay=0)
        self.reopen_runtime()
        self.store.clock = lambda: now[0]
        now[0] += 20
        rules = self.rig.gamification.config.get_points_rules()
        self.rig.gamification.config.update_sections({"points_rules": {**rules, "deepseek_retry": {"total_timeout_seconds": 90}}})
        retried = self.store.claim(kinds=("chat",))
        self.assertEqual(self.store.begin_turn(retried)["deadline"], 1060)
        self.assertEqual(self.store.request(TEST_USER, "configured-budget")["source"]["metadata"]["retry_rules"]["total_timeout_seconds"], 60)

    async def test_failure_does_not_award_third_day_streak_or_prevent_later_success(self):
        for day in (date(2026, 10, 18), date(2026, 10, 19)):
            self.rig.points.record_user_activity(TEST_USER, today=day)
        before = self.rig.points.get_balance(TEST_USER)
        self.rig.client.completions.outcomes = [RuntimeError("synthetic failure")] * 3
        with patch("handlers.interaction_handler.get_business_today_str", return_value="2026-10-20"):
            failed = await self.rig.handler.send(TEST_USER, "coffee", "failed-third-day", attachment="synthetic")
            self.assertFalse(failed.ok)
            self.assertTrue(failed.refunded)
            self.assertEqual(self.rig.points.get_balance(TEST_USER), before)
            self.assertEqual(self.ledger_count(REASON_STREAK_3_DAY), 0)
            self.assertFalse(any(row["activity_date"] == "2026-10-20" for row in self.rig.gamification.progress_store.snapshot()["daily_activity_log"].values()))
            error = self.rig.events_of("bot.error")[-1]["payload"]
            self.assertGreater(error["timestamp"], 0)
            self.rig.client.completions.outcomes = ["收下了。"]
            success = await self.rig.handler.send(TEST_USER, "coffee", "successful-third-day", attachment="synthetic")
            self.assertTrue(success.ok)
            self.assertEqual(success.balance, before - 5 + 4)
            self.assertEqual(self.ledger_count(REASON_STREAK_3_DAY), 1)
            await self.rig.handler.send(TEST_USER, "coffee", "successful-third-day", attachment="synthetic")
            self.assertEqual(self.rig.points.get_balance(TEST_USER), before - 5 + 4)

    async def test_rewards_recover_after_committed_reply_without_original_waiter(self):
        receipt = self.accepted_gift("reward-recovery")
        self.rig.points.deduct_for_item(TEST_USER, "coffee", 5, "reward-recovery")
        self.store.confirm_interaction(TEST_USER, "reward-recovery")
        job = self.store.claim(kinds=("chat",))
        snapshot = self.store.begin_turn(job)
        self.store.commit_reply(job, snapshot, {"reply": "已收下。"})
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 15)
        self.reopen_runtime()
        self.runtime.start()
        await self.wait_for_reconciliation_completion(receipt["turn_id"])
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 16)
        self.assertEqual(self.ledger_count(REASON_DAILY_FIRST_CHAT), 1)

    async def test_five_item_matrix_keeps_checks_and_rechecks_noodles_rewrite(self):
        with self.rig.gamification.points_store.transaction() as data:
            PointsStore.ensure_account(data, TEST_USER)["balance"] = 100
        rules = self.rig.gamification.config.get_points_rules()
        self.rig.gamification.config.update_sections({"points_rules": {**rules, "deepseek_retry": {"total_timeout_seconds": 10}}})
        original = self.rig.client.completions.create
        checked = []

        async def model(**kwargs):
            response = checker_response(kwargs)
            if response:
                self.assertEqual(kwargs["response_format"], {"type": "json_object"})
                value = json.loads(kwargs["messages"][-1]["content"])
                event = value["context"]["interaction_events"][0]
                self.assertIn(event["source_id"], [source["source_id"] for source in value["context"]["current_input"]])
                item_id = event["item"]["id"]
                if item_id == "noodles" and "noodles" not in checked:
                    response.choices[0].message.content = json.dumps({"approved": False, "checks": {"time": False}, "violations": ["time"]})
                checked.append(item_id)
                return response
            return await original(**kwargs)

        self.rig.client.completions.create = model
        for item in self.rig.gamification.config.get_active_items():
            self.rig.client.completions.outcomes = ["刚才一起吃过了。", "谢了，你喜欢什么口味？"] if item["id"] == "noodles" else ["谢了，收下了。"]
            result = await self.rig.handler.send(TEST_USER, item["id"], "matrix-" + item["id"], attachment="synthetic")
            self.assertTrue(result.ok, item["id"])
        self.assertEqual(checked.count("noodles"), 2)
        self.assertEqual(len(checked), 6)
        self.assertEqual(self.ledger_count(REASON_ITEM_REFUND), 0)


if __name__ == "__main__":
    unittest.main()
