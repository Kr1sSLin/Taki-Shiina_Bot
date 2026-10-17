"""PRD-03 adversarial offline replays using real SQL and actual checker input."""
import asyncio
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from controlled_model_fixture import checker_response
from services.chat_runtime import ChatRuntime, ChatFailed, get_chat_runtime
from services.contracts import ChatCommand, Domain
from services.controlled_response import ControlledJSONResponse
from services.domain_cipher import DomainCipher
from services.foundation_store import PermissionDenied, StorageBusy, VersionConflict
from services.owner_fence import async_owner_fence
from services.public_policy import PUBLIC_POLICY, PUBLIC_POLICY_VERSION, PUBLIC_POLICY_HASH
from services.reliable_chat_store import ReliableChatStore
from services.reply_check import CHECKS, DraftBudgetExceeded, ReplyRejected, hard_violations
from services.text_generation import TextGenerator


class ControlledChatTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-controlled-")
        self.store = ReliableChatStore(str(Path(self.temp.name) / "chat.db"), DomainCipher(b"c" * 32))
        self.calls, self.drafts, self.verdicts, self.frames = [], ["哪场？"], [], []
        self.runtime = None
        self.client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=self.model)))
        guard = SimpleNamespace(owner_context_allowed=lambda _: True)
        prompt = SimpleNamespace(permission_guard=guard, get_system_prompt=AsyncMock(return_value="椎名立希，文字聊天"))
        weather = SimpleNamespace(get_weather_str=AsyncMock(return_value=""))
        state = SimpleNamespace(user_chat_history={"owner": [{"role": "assistant", "content": "unqualified-old-summary"}]})
        self.generator = TextGenerator(state, None, prompt, weather, self.client, lambda text: (text, None, None))

    async def asyncTearDown(self):
        if self.runtime:
            await self.runtime.stop()
        self.temp.cleanup()

    async def model(self, **kwargs):
        self.calls.append(kwargs)
        check = checker_response(kwargs, use_memories=False)
        if check:
            if self.verdicts:
                value = self.verdicts.pop(0)
                if isinstance(value, Exception):
                    raise value
                check.choices[0].message.content = json.dumps(value)
            return check
        if "task=fact_extraction\n" in kwargs["messages"][0]["content"]:
            return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="[]"))])
        value = self.drafts.pop(0)
        if isinstance(value, Exception):
            raise value
        return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=value))], usage=None)

    def command(self, identity="first", text="之前那场演出是周六还是周日？"):
        return ChatCommand("owner", identity, identity, "device", text)

    def begin(self, command=None):
        self.store.accept([command or self.command()])
        job = self.store.claim(kinds={"chat"})
        return job, self.store.begin_turn(job)

    def verdict(self, *, approved=True, used=None, claims=None):
        return {"approved": approved, "checks": dict.fromkeys(CHECKS, approved), "violations": [] if approved else ["evidence"],
                "used_memory_ids": used or [], "claims": claims or []}

    def fact(self, identity="coffee", *, weight=6, enabled=True):
        self.store.put_fact("owner", identity, Domain.USER_FACT,
                            {"text": "用户喜欢咖啡", "enabled": enabled, "subject": "user", "weight": weight,
                             "stage": "formal" if weight >= 6 else "candidate", "truth_status": "accepted"}, expected_revision=0, targets=())

    async def generate(self):
        job, snapshot = self.begin()
        result = await self.generator(snapshot, self.store)
        return job, snapshot, result

    async def runtime_start(self):
        async def deliver(value):
            self.frames.append(value)
        self.runtime = ChatRuntime(self.store, self.generator, AsyncMock(return_value=[]), deliver, poll_interval=0.002)
        for worker in self.runtime.workers:
            worker.retry_delay = 0
        return self.runtime

    async def test_all_actual_calls_include_exact_public_constraints_and_version(self):
        self.drafts = ["第一版。", "哪场？"]
        self.verdicts = [self.verdict(approved=False), self.verdict()]
        await self.generate()
        await self.generator.extract("我喜欢咖啡")
        self.assertEqual(len(self.calls), 5)
        for call in self.calls:
            policy = call["messages"][0]["content"]
            self.assertIn(PUBLIC_POLICY_VERSION, policy)
            self.assertIn(PUBLIC_POLICY_HASH, policy)
            self.assertIn(PUBLIC_POLICY, policy)
        self.assertIn("task=chat_rewrite", self.calls[2]["messages"][0]["content"])

    async def test_later_bubble_failure_and_checker_exception_deliver_zero_draft_characters(self):
        self.drafts = ["嗯。\n你答应今天送我挂件。"]
        self.verdicts = [RuntimeError("checker unavailable")]
        runtime = await self.runtime_start()
        # A checker outage may consume only the bounded draft/retry budget.
        self.drafts += ["另一份未审核草稿。"]
        with self.assertRaises(ChatFailed):
            await runtime.submit([self.command()], timeout=3)
        self.assertEqual(self.frames, [])
        self.assertEqual(self.store.history("owner"), [])
        self.assertLessEqual(sum("task=chat\n" in c["messages"][0]["content"] for c in self.calls), 2)

    async def test_every_rewrite_is_checked_and_rejections_are_terminal(self):
        self.drafts = ["坏草稿一", "坏草稿二"]
        self.verdicts = [self.verdict(approved=False), self.verdict(approved=False)]
        runtime = await self.runtime_start()
        with self.assertRaises(ChatFailed):
            await runtime.submit([self.command()], timeout=3)
        self.assertEqual(len(self.calls), 4)
        self.assertEqual(self.frames, [])

    async def test_checker_cannot_approve_with_missing_dimension_or_unknown_evidence(self):
        for mutation in ("dimension", "unknown_source", "unsupported_usage", "invented_quote"):
            with self.subTest(mutation=mutation):
                verdict = self.verdict()
                if mutation == "dimension":
                    verdict["checks"].pop("negation")
                elif mutation == "unknown_source":
                    verdict["claims"] = [{"text": "哪场？", "source_ids": ["not-permitted"]}]
                elif mutation == "unsupported_usage":
                    verdict["used_memory_ids"] = ["absent"]
                else:
                    verdict["claims"] = [{"text": "not in reply", "source_ids": ["first"]}]
                self.verdicts = [verdict]
                self.drafts = ["哪场？"]
                job, snapshot = self.begin(self.command(mutation))
                with self.assertRaises(ReplyRejected):
                    await self.generator(snapshot, self.store)
                self.store.cancel("owner", snapshot["turn_id"])

    async def test_post_check_mutation_cannot_commit(self):
        job, snapshot, result = await self.generate()
        result["reply"] += "\n未经检查的另一句"
        with self.assertRaises(PermissionDenied):
            self.store.commit_reply(job, snapshot, result)
        self.assertEqual(self.store.history("owner"), [])

    async def test_reminder_text_is_checked_and_cannot_change_after_approval(self):
        self.generator.parse_timer = lambda text: (text, "14:00", "正在检查你的记忆")
        self.drafts = ["好。", "知道了。"]
        job, snapshot = self.begin()
        with self.assertRaises(ReplyRejected):
            await self.generator(snapshot, self.store)
        self.assertEqual(self.store.history("owner"), [])
        self.assertEqual(len(self.calls), 2)  # Both attempts rejected by hard rules.

    async def test_disable_during_context_assembly_prevents_model_outbound(self):
        self.fact()
        original = self.generator.weather_service.get_weather_str
        async def disable(**kwargs):
            await asyncio.to_thread(self.store.set_permission, "owner", "coffee", enabled=False, expected_revision=1)
            return await original(**kwargs)
        self.generator.weather_service.get_weather_str = disable
        job, snapshot = self.begin(self.command(text="咖啡"))
        with self.assertRaises(VersionConflict):
            await self.generator(snapshot, self.store)
        self.assertEqual(self.calls, [])

    async def test_disable_during_checker_prevents_commit_and_subsequent_outbound(self):
        self.fact()
        original = self.model
        async def disable(**kwargs):
            if "task=reply_check\n" in kwargs["messages"][0]["content"]:
                await asyncio.to_thread(self.store.set_permission, "owner", "coffee", enabled=False, expected_revision=1)
            return await original(**kwargs)
        self.client.chat.completions.create = disable
        job, snapshot = self.begin(self.command(text="咖啡"))
        with self.assertRaises(VersionConflict):
            await self.generator(snapshot, self.store)
        self.assertEqual(self.store.history("owner"), [])
        self.assertEqual(len(self.calls), 2)

    async def test_qualified_memory_has_provenance_and_unused_memory_is_not_reinforced(self):
        self.fact()
        self.fact("low", weight=3)
        self.drafts = ["怎么了？"]
        job, snapshot = self.begin(self.command(text="咖啡怎么了"))
        result = await self.generator(snapshot, self.store)
        assembled = self.calls[0]["messages"][1]["content"]
        self.assertIn("formal_memories", assembled)
        self.assertIn('"fact_id": "coffee"', assembled)
        self.assertNotIn('"fact_id": "low"', assembled)
        self.assertNotIn("unqualified-old-summary", assembled)
        self.store.commit_reply(job, snapshot, result)
        self.assertEqual(self.store.read_fact("owner", "coffee")["data"]["weight"], 6)

    async def test_negation_subject_time_and_major_event_verdicts_are_fail_closed(self):
        for dimension in CHECKS:
            with self.subTest(dimension=dimension):
                self.drafts = ["未经依据确认的断言"]
                verdict = self.verdict()
                verdict["checks"][dimension] = False
                self.verdicts = [verdict]
                job, snapshot = self.begin(self.command(dimension))
                with self.assertRaises(ReplyRejected):
                    await self.generator(snapshot, self.store)
                self.store.cancel("owner", snapshot["turn_id"])

    async def test_new_input_invalidates_old_draft_and_combines_sources_once(self):
        runtime = await self.runtime_start()
        entered, release = asyncio.Event(), asyncio.Event()
        original = self.model
        async def paused(**kwargs):
            if "task=chat\n" in kwargs["messages"][0]["content"] and not entered.is_set():
                entered.set()
                await release.wait()
            return await original(**kwargs)
        self.client.chat.completions.create = paused
        self.drafts = ["那就在家。", "那就去吧。"]
        receipts = await runtime.accept([self.command(text="不去了")])
        await asyncio.wait_for(entered.wait(), 2)
        newer = await runtime.accept([self.command("second", "还是去吧")])
        self.assertEqual(receipts[0]["turn_id"], newer[0]["turn_id"])
        release.set()
        result = await runtime.wait("owner", receipts[0]["turn_id"], timeout=3)
        self.assertEqual(result["reply"], "那就去吧。")
        self.assertEqual(result["requestIds"], ["first", "second"])
        self.assertEqual(len([m for m in self.store.history("owner") if m["role"] == "user"]), 2)
        self.assertNotIn("那就在家", str(self.frames))

    async def test_continuous_input_does_not_reset_deadline_or_allow_third_draft(self):
        job, first = self.begin()
        self.store.reserve_draft(first)
        self.store.accept([self.command("second", "补充")], merge_pending=True)
        self.assertTrue(self.store.restart_changed_turn(job, first))
        job = self.store.claim(kinds={"chat"})
        second = self.store.begin_turn(job)
        self.store.reserve_draft(second)
        self.store.accept([self.command("third", "再改口")], merge_pending=True)
        self.assertTrue(self.store.restart_changed_turn(job, second))
        job = self.store.claim(kinds={"chat"})
        third = self.store.begin_turn(job)
        self.assertEqual(first["deadline"], third["deadline"])
        self.assertEqual(len(third["sources"]), 3)
        with self.assertRaises(DraftBudgetExceeded):
            self.store.reserve_draft(third)

    async def test_committed_unsent_reply_is_revoked_and_usage_compensated_once(self):
        self.fact()
        job, snapshot = self.begin(self.command(text="咖啡"))
        self.store.prepare_memories(snapshot, "咖啡")
        first = self.store.commit_reply(job, snapshot, {"reply": "你喜欢咖啡。"})
        self.assertEqual(self.store.read_fact("owner", "coffee")["data"]["weight"], 7)
        self.store.accept([self.command("second", "不说咖啡了")])
        self.assertEqual(self.store.read_fact("owner", "coffee")["data"]["weight"], 6)
        self.store.accept([self.command("second", "不说咖啡了")])
        self.assertEqual(self.store.read_fact("owner", "coffee")["data"]["weight"], 6)
        with self.assertRaises(PermissionDenied):
            self.store.mark_dispatched("owner", first)
        self.assertNotIn(first["reply"], str(self.store.history("owner")))

    async def test_disable_after_commit_revokes_pending_usage_and_blocks_http_bytes(self):
        self.fact()
        job, snapshot = self.begin(self.command(text="咖啡"))
        self.store.prepare_memories(snapshot, "咖啡")
        result = self.store.commit_reply(job, snapshot, {"reply": "我记得你喜欢咖啡。"})
        response = ControlledJSONResponse({"code": 0, "data": result}, store=self.store, owner="owner", results=[result])
        self.store.set_permission("owner", "coffee", enabled=False, expected_revision=2)
        frames = []
        await response({"type": "http"}, AsyncMock(), AsyncMock(side_effect=lambda event: frames.append(event)))
        self.assertNotIn(result["reply"].encode(), b"".join(frame.get("body", b"") for frame in frames))
        self.assertEqual(frames[0]["status"], 503)
        self.assertEqual(self.store.read_fact("owner", "coffee")["data"]["weight"], 6)

    async def test_network_send_and_permission_change_have_process_safe_order(self):
        self.fact()
        job, snapshot = self.begin()
        result = self.store.commit_reply(job, snapshot, {"reply": "哪场？"})
        entered, release = asyncio.Event(), asyncio.Event()
        async def send(event):
            if event["type"] == "http.response.start":
                entered.set()
                await release.wait()
        response = ControlledJSONResponse({"code": 0, "data": result}, store=self.store, owner="owner", results=[result])
        sending = asyncio.create_task(response({"type": "http"}, AsyncMock(), send))
        await asyncio.wait_for(entered.wait(), 2)
        # A second repository instance obeys the same file fence.
        other = ReliableChatStore(self.store.path, self.store.cipher)
        changing = asyncio.create_task(asyncio.to_thread(other.set_permission, "owner", "coffee", enabled=False, expected_revision=1))
        await asyncio.sleep(0.03)
        self.assertFalse(changing.done())
        release.set()
        await sending
        await changing
        self.assertFalse(other.read_fact("owner", "coffee")["data"]["enabled"])

    async def test_storage_failure_and_reconnect_never_regenerate_committed_result(self):
        job, snapshot, result = await self.generate()
        with patch.object(self.store, "_change", side_effect=OSError("disk failure")):
            with self.assertRaises(OSError):
                self.store.commit_reply(job, snapshot, result)
        self.assertEqual(self.store.history("owner"), [])
        committed = self.store.commit_reply(job, snapshot, result)
        reopened = ReliableChatStore(self.store.path, self.store.cipher)
        replay = reopened.turn_result("owner", snapshot["turn_id"])["result"]
        self.assertEqual(replay["messageId"], committed["messageId"])
        self.assertEqual(len(self.calls), 2)

    async def test_pre_upgrade_unchecked_outbox_is_never_released_by_production_store(self):
        job, snapshot = self.begin()
        result = self.store.commit_reply(job, snapshot, {"reply": "旧版本尚未检查的回复"})
        guarded = ReliableChatStore(self.store.path, self.store.cipher, require_checks=True)
        with self.assertRaises(PermissionDenied):
            guarded.mark_dispatched("owner", result)
        items, pending = guarded.history_delivery("owner")
        self.assertFalse(any(item["role"] == "bot" for item in items))
        self.assertEqual(pending, [])

    async def test_pre_upgrade_invalid_outbox_cannot_block_later_checked_reply(self):
        job, snapshot = self.begin()
        legacy = self.store.commit_reply(job, snapshot, {"reply": "旧未检查回复"})
        later = self.store.clock() + 121
        guarded = ReliableChatStore(self.store.path, self.store.cipher, require_checks=True, clock=lambda: later)
        guarded.accept([self.command("later", "现在聊演出")])
        job = guarded.claim(kinds={"chat"})
        current = guarded.begin_turn(job)
        checked = await self.generator(current, guarded)
        final = guarded.commit_reply(job, current, checked)
        guarded.mark_dispatched("owner", final)
        self.assertEqual(guarded.turn_result("owner", snapshot["turn_id"])["status"], "cancelled")
        self.assertNotIn(legacy["reply"], str(guarded.history("owner")))

    async def test_history_recovery_delivery_is_fenced_against_new_input(self):
        job, snapshot, checked = await self.generate()
        result = self.store.commit_reply(job, snapshot, checked)
        items, pending = self.store.history_delivery("owner")
        self.assertEqual(pending[0]["messageId"], result["messageId"])
        response = ControlledJSONResponse({"data": items}, store=self.store, owner="owner", results=pending)
        self.store.accept([self.command("new", "不是那场")])
        frames = []
        await response({"type": "http"}, AsyncMock(), AsyncMock(side_effect=lambda event: frames.append(event)))
        self.assertEqual(frames[0]["status"], 503)
        self.assertNotIn(result["reply"].encode(), b"".join(frame.get("body", b"") for frame in frames))

    async def test_date_rollover_fences_commit_and_first_dispatch(self):
        now = [15*3600 - 1.0]  # One second before Tokyo midnight.
        store = ReliableChatStore(str(Path(self.temp.name) / "date.db"), self.store.cipher, clock=lambda: now[0])
        receipt = store.accept([self.command()])[0]
        job = store.claim(kinds={"chat"})
        snapshot = store.begin_turn(job)
        now[0] += 2
        with self.assertRaises(VersionConflict):
            store.commit_reply(job, snapshot, {"reply": "今天的回复"})
        snapshot = store.begin_turn(job)
        result = store.commit_reply(job, snapshot, {"reply": "更新日期的回复"})
        now[0] += 3600  # Beijing midnight too, within a simulated offline wait.
        with self.assertRaises(PermissionDenied):
            store.mark_dispatched("owner", result)

    async def test_silence_window_has_25_second_ceiling_and_120_second_total(self):
        now = [1000.0]
        store = ReliableChatStore(str(Path(self.temp.name) / "clock.db"), self.store.cipher, clock=lambda: now[0])
        first = store.accept([self.command()], delay=8, merge_pending=True)[0]
        for i in range(1, 7):
            now[0] += 4
            receipt = store.accept([self.command(str(i), "普通补充")], delay=20, merge_pending=True)[0]
            self.assertEqual(receipt["turn_id"], first["turn_id"])
        now[0] = 1025
        job = store.claim(kinds={"chat"})
        snapshot = store.begin_turn(job)
        self.assertEqual(snapshot["deadline"], 1120)
        self.assertEqual(len(snapshot["sources"]), 7)
        now[0] = 1120
        with self.assertRaises(VersionConflict):
            store.assert_snapshot(snapshot)
        self.assertEqual(store.chat_metrics("owner")["timeout_rate"], 1)

    async def test_disabled_chat_cannot_enter_legacy_generator(self):
        with patch.dict("os.environ", {"FOUNDATION_ENABLED": "0"}):
            with self.assertRaises(PermissionDenied):
                get_chat_runtime(self.temp.name, None, None, None)

    def test_fixed_hard_negative_replays(self):
        for text in ("正在检索你的记忆，置信度 0.6。", "那我们现在就是恋人了。", "她低下头，拿起鼓棒。", "刚才我去排练了，所以系统回得慢。", "乐队今天解散了。"):
            with self.subTest(text=text):
                self.assertTrue(hard_violations(text))


if __name__ == "__main__":
    unittest.main()
