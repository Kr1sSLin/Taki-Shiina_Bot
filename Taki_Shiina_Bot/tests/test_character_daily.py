"""PRD-04 acceptance with synthetic data and explicitly labeled model doubles.

These tests exercise storage/gates, not a real model's semantic accuracy.
"""
import asyncio
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from services.contracts import ChatCommand, Domain
from services.daily_generation import DailyGenerator
from services.daily_policy import DAILY_CHECKS, DailyRejected, RULE_VERSIONS, digest, time_context, validate_candidate
from services.domain_cipher import DomainCipher
from services.durable_worker import DurableWorker
from services.foundation_store import PermissionDenied, VersionConflict
from services.public_policy import PERSONA, PERSONA_VERSION, RELATIONSHIP_VERSION, PUBLIC_POLICY
from services.reliable_chat_store import ReliableChatStore
from services.reply_check import CHECKS
from services.text_generation import TextGenerator


def approved(candidate, snapshot):
    return {"approved": True, "classification": "ordinary", "checks": dict.fromkeys(DAILY_CHECKS, True),
            "violations": [], "rule_versions": dict(RULE_VERSIONS), "digest": digest({"candidate": candidate, "snapshot": snapshot})}


class DailyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-daily-")
        self.addCleanup(self.temp.cleanup)
        self.env = patch.dict("os.environ", {"CHARACTER_DAILY_ENABLED": "1", "CHARACTER_DAILY_MAX_PER_DAY": "2"})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.now = datetime(2026, 10, 17, 2, tzinfo=timezone.utc).timestamp()
        self.path = str(Path(self.temp.name) / "synthetic.sqlite3")
        self.store = ReliableChatStore(self.path, DomainCipher(b"d" * 32), clock=lambda: self.now)

    def candidate(self, **changes):
        return {"event_id": "panda-event", "expected_revision": 0, "event_type": "shopping",
                "participants": ["椎名立希"], "object": "橱窗里的熊猫挂件", "status": "in_progress",
                "summary": "看见橱窗里的熊猫挂件，还没买。", "scheduled_at": None,
                "occurred_at": datetime.fromtimestamp(self.now, timezone.utc).isoformat(),
                "outcome": "not_bought", "previous_refs": [], "chat_refs": [], **changes}

    def save(self, candidate=None, *, slot="first", snapshot=None, approval=None):
        candidate = candidate or self.candidate()
        snapshot = snapshot or self.store.daily_snapshot("owner")
        return self.store.commit_daily("owner", candidate, snapshot, approval or approved(candidate, snapshot), slot=slot)

    def continuation(self, **changes):
        row = self.store.read_fact("owner", "panda-event")
        return self.candidate(expected_revision=row["revision"], previous_refs=[{"event_id": "panda-event", "revision": row["revision"]}],
                              summary="晚间又看了熊猫挂件的价格，仍未买。", **changes)

    def test_a02_same_identity_revision_history_and_stale_concurrent_update(self):
        self.save()
        self.now += 6 * 3600
        snapshot = self.store.daily_snapshot("owner")
        candidate = self.continuation()
        review = approved(candidate, snapshot)
        def commit(slot):
            try:
                return self.save(candidate, slot=slot, snapshot=snapshot, approval=review)
            except VersionConflict:
                return "conflict"
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(commit, ["second-a", "second-b"]))
        self.assertEqual(results.count("conflict"), 1)
        self.assertEqual(self.store.read_fact("owner", "panda-event")["revision"], 2)
        self.assertEqual([r["revision"] for r in self.store.revision_history("owner", "panda-event")], [1, 2])
        refs = self.store.select_memories("owner", "熊猫挂件买了吗")
        self.assertEqual(refs[0]["event"]["outcome"], "not_bought")
        self.assertEqual(refs[0]["revision"], 2)

    def test_a03_plan_never_becomes_completed_by_clock_or_generic_write(self):
        plan = self.candidate(status="planned", scheduled_at=datetime.fromtimestamp(self.now+3600, timezone.utc).isoformat(), occurred_at=None)
        self.save(plan)
        self.now += 2 * 3600
        ref = self.store.select_memories("owner", "在干嘛")[0]
        self.assertEqual(ref["event_state"], "planned")
        self.assertIsNone(ref["event"]["occurred_at"])
        self.assertEqual(ref["event"]["outcome"], "not_bought")
        with self.assertRaises(DailyRejected):
            self.store.put_fact("owner", "unreviewed", Domain.CHARACTER_DAILY, {"text": "买完了", "enabled": True}, expected_revision=0)
        invalid = self.continuation(status="planned", outcome="bought", occurred_at=None)
        with self.assertRaises(DailyRejected):
            self.save(invalid, slot="second")

    def test_a04_tokyo_rollover_rejects_old_candidate_then_continues_same_event(self):
        self.now = datetime(2026, 10, 17, 14, 59, 59, tzinfo=timezone.utc).timestamp()
        self.save()
        snapshot = self.store.daily_snapshot("owner")
        candidate = self.continuation()
        self.now += 2
        self.assertEqual(time_context(self.now)["character_date"], "2026-10-18")
        self.assertEqual(datetime.fromtimestamp(self.now+8*3600, timezone.utc).date().isoformat(), "2026-10-17")
        with self.assertRaises(VersionConflict):
            self.save(candidate, slot="second", snapshot=snapshot)
        self.save(self.continuation(), slot="next-day")
        ref = self.store.select_memories("owner", "昨天那个熊猫挂件")[0]
        self.assertEqual(ref["event_id"], "panda-event")
        self.assertEqual(ref["event"]["character_date"], "2026-10-18")
        self.assertEqual(ref["event"]["outcome"], "not_bought")

    def test_a05_real_user_source_is_required_and_owner_bound(self):
        candidate = self.candidate(participants=["椎名立希", "user"], summary="用户答应送熊猫挂件。")
        with self.assertRaises(DailyRejected):
            self.save(candidate)
        receipt = self.store.accept([ChatCommand("owner", "source-request", "source-client", "device", "我答应送你熊猫挂件")])[0]
        snapshot = self.store.daily_snapshot("owner", source_ids=[receipt["message_id"]])
        candidate["chat_refs"] = [{"source_id": receipt["message_id"], "quote": "我答应送你熊猫挂件"}]
        self.save(candidate, snapshot=snapshot)
        with self.assertRaises(PermissionDenied):
            self.store.daily_snapshot("another-owner", source_ids=[receipt["message_id"]])
        with self.store._transaction(write=False) as conn:
            self.assertEqual(conn.execute("SELECT count(*) FROM memory_evidence").fetchone()[0], 0)
        self.assertEqual(self.store.select_memories("owner", "送熊猫")[0]["domain"], "character_daily")

    def test_false_quote_and_unknown_source_fail_before_review(self):
        candidate = self.candidate(chat_refs=[{"source_id": "invented", "quote": "我答应"}])
        with self.assertRaises(DailyRejected):
            self.save(candidate)

    def test_review_digest_dimensions_and_ordinary_classification_are_mandatory(self):
        for mutation in ("digest", "classification", "checks", "versions", "approved", "violations"):
            with self.subTest(mutation=mutation):
                candidate = self.candidate()
                snapshot = self.store.daily_snapshot("owner")
                review = approved(candidate, snapshot)
                if mutation == "checks":
                    review["checks"].pop("continuity")
                elif mutation == "versions":
                    review["rule_versions"] = {}
                else:
                    review[mutation] = {"digest": "bad", "classification": "uncertain", "approved": False, "violations": ["major"]}[mutation]
                with self.assertRaises(DailyRejected):
                    self.save(candidate, snapshot=snapshot, approval=review)
        self.assertEqual(self.store.daily_snapshot("owner")["events"], [])

    def test_check_then_modify_candidate_cannot_commit(self):
        candidate = self.candidate()
        snapshot = self.store.daily_snapshot("owner")
        review = approved(candidate, snapshot)
        candidate["summary"] = "已经买下了。"
        with self.assertRaises(DailyRejected):
            self.save(candidate, snapshot=snapshot, approval=review)

    def test_switch_disables_new_generation_but_preserves_approved_history(self):
        self.save()
        with patch.dict("os.environ", {"CHARACTER_DAILY_ENABLED": "0"}):
            self.assertIsNone(self.store.schedule_daily("owner"))
            with self.assertRaises(DailyRejected):
                self.save(self.continuation(), slot="second")
            self.assertEqual(self.store.select_memories("owner", "熊猫")[0]["event_state"], "in_progress")

    def test_permission_disable_restore_preserves_review_and_continuation_fence(self):
        self.save()
        snapshot = self.store.daily_snapshot("owner")
        candidate = self.continuation()
        self.store.set_permission("owner", "panda-event", enabled=False, expected_revision=1, targets=())
        self.assertEqual(self.store.select_memories("owner", "熊猫"), [])
        with self.assertRaises(VersionConflict):
            self.save(candidate, snapshot=snapshot, slot="second")
        self.store.set_permission("owner", "panda-event", enabled=True, expected_revision=2, targets=())
        self.save(self.continuation(), slot="second")
        data = self.store.read_fact("owner", "panda-event")["data"]
        self.assertEqual(data["permission_version"], 3)

    def test_atomic_failure_rolls_back_event_revision_quota_and_journal(self):
        with patch.object(self.store, "_change", side_effect=OSError("synthetic disk failure")):
            with self.assertRaises(OSError):
                self.save()
        self.assertEqual(self.store.daily_snapshot("owner")["events"], [])
        with self.store._transaction(write=False) as conn:
            for table in ("facts", "memory_revisions", "daily_commits", "events", "outbox"):
                self.assertEqual(conn.execute("SELECT count(*) FROM " + table).fetchone()[0], 0)
        self.save()

    def test_daily_quota_dedupe_does_not_require_any_new_event(self):
        first = self.store.schedule_daily("owner")
        self.assertEqual(first, self.store.schedule_daily("owner"))
        self.save()
        self.save(self.continuation(), slot="second")
        with self.assertRaises(DailyRejected):
            self.save(self.continuation(), slot="third")
        self.assertEqual(self.store.read_fact("owner", "panda-event")["revision"], 2)

    def test_backup_preserves_daily_history_and_quota_but_starts_permission_closed(self):
        self.save()
        self.save(self.continuation(), slot="second")
        head = self.store.change_head()
        backup = str(Path(self.temp.name) / "backup.sqlite3")
        self.store.backup(backup)
        restored = ReliableChatStore(backup, self.store.cipher, clock=lambda: self.now)
        with self.assertRaises(PermissionDenied):
            restored.daily_snapshot("owner")
        restored.verify_restored_permissions(head)
        ref = restored.select_memories("owner", "熊猫挂件")[0]
        self.assertEqual(ref["revision"], 2)
        self.assertEqual(ref["event"]["outcome"], "not_bought")
        self.assertEqual(len(restored.revision_history("owner", "panda-event")), 2)
        with restored._transaction(write=False) as conn:
            self.assertEqual(conn.execute("SELECT count(*) FROM daily_commits").fetchone()[0], 2)
        self.assertNotIn("橱窗里的熊猫挂件".encode(), Path(backup).read_bytes())

    def test_a08_followup_uses_latest_version_without_user_evidence_reinforcement(self):
        self.save()
        self.store.accept([ChatCommand("owner", "first", "first", "device", "那个熊猫挂件呢")])
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        refs = self.store.prepare_memories(snapshot, "熊猫挂件")
        result = self.store.commit_reply(job, snapshot, {"reply": "还没买。"})
        self.store.mark_dispatched("owner", result)
        self.assertEqual(len(self.store.daily_references("owner", "panda-event")), 1)
        self.assertEqual(self.store.read_fact("owner", "panda-event")["data"]["weight"], 0)
        with self.store._transaction(write=False) as conn:
            self.assertEqual(conn.execute("SELECT count(*) FROM memory_ledger").fetchone()[0], 0)
        self.now += 6*3600
        self.save(self.continuation(), slot="second")
        followup = self.store.select_memories("owner", "下午那段呢")
        self.assertEqual(followup[0]["fact_id"], refs[0]["fact_id"])
        self.assertEqual(followup[0]["revision"], 2)
        self.assertEqual(followup[0]["event"]["outcome"], "not_bought")
        reopened = ReliableChatStore(self.path, self.store.cipher, clock=lambda: self.now)
        self.assertEqual(reopened.select_memories("owner", "后来呢")[0]["revision"], 2)

    def test_same_summary_state_update_still_fences_old_checked_reply(self):
        self.save()
        self.store.accept([ChatCommand("owner", "first", "first", "device", "熊猫挂件呢")])
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.store.prepare_memories(snapshot, "熊猫")
        result = self.store.commit_reply(job, snapshot, {"reply": "还没买。"})
        candidate = self.continuation()
        candidate.update(summary=self.candidate()["summary"], status="cancelled")
        self.save(candidate, slot="second")
        with self.assertRaises(VersionConflict):
            self.store.mark_dispatched("owner", result)

    def test_revoked_unsent_reply_is_audited_and_not_a_followup_anchor(self):
        self.save()
        self.store.accept([ChatCommand("owner", "first", "first", "device", "熊猫挂件呢")])
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.store.prepare_memories(snapshot, "熊猫")
        self.store.commit_reply(job, snapshot, {"reply": "还没买。"})
        self.store.accept([ChatCommand("owner", "new", "new", "device", "先别说那个")])
        self.assertTrue(self.store.daily_references("owner", "panda-event")[0]["revoked"])
        self.assertEqual(self.store.select_memories("owner", "后来呢"), [])

    def test_illegal_identity_purchase_time_and_state_transitions(self):
        self.save()
        snapshot = self.store.daily_snapshot("owner")
        for changes in ({"object": "另一个挂件"}, {"participants": ["椎名立希", "灯"]},
                        {"status": "planned"}, {"outcome": "bought"},
                        {"occurred_at": datetime.fromtimestamp(self.now+100, timezone.utc).isoformat()},
                        {"occurred_at": "2026-10-17T12:00:00"},
                        {"event_id": "another-id", "expected_revision": 0, "previous_refs": []}):
            with self.subTest(changes=changes), self.assertRaises(DailyRejected):
                validate_candidate({**self.continuation(), **changes}, snapshot)

    def test_a01_explicit_major_assertion_is_rejected_even_if_reviewer_says_yes(self):
        for summary in ("椎名立希死亡了。", "MyGO!!!!!今天解散了。", "我们正式成为恋人。", "用户答应送我挂件。"):
            with self.subTest(summary=summary), self.assertRaises(DailyRejected):
                self.save(self.candidate(summary=summary))
        self.assertEqual(self.store.daily_snapshot("owner")["events"], [])

    def test_a03_actual_chat_rewrite_retains_plan_and_does_not_generate_activity(self):
        self.save(self.candidate(status="planned", occurred_at=None,
                                 scheduled_at=datetime.fromtimestamp(self.now+3600, timezone.utc).isoformat(),
                                 summary="打算下午去看熊猫挂件，还没买。"))
        calls = []
        drafts = ["买回来了。", "还没买。\n打算下午去看看。"]
        async def model(**kwargs):
            calls.append(kwargs)
            envelope = kwargs["messages"][0]["content"]
            if "task=reply_check\n" in envelope:
                value = json.loads(kwargs["messages"][-1]["content"])
                ref = value["context"]["character_daily"][0]
                self.assertEqual(ref["event_state"], "planned")
                self.assertEqual(ref["event"]["outcome"], "not_bought")
                self.assertIsNone(ref["event"]["occurred_at_utc"])
                ok = value["result"]["reply"] == "还没买。\n打算下午去看看。"
                result = {"approved": ok, "checks": dict.fromkeys(CHECKS, ok), "violations": [] if ok else ["time"],
                          "used_memory_ids": [ref["fact_id"]] if ok else [],
                          "claims": [{"text": value["result"]["reply"], "source_ids": [ref["fact_id"]]}] if ok else []}
                content = json.dumps(result)
            else:
                content = drafts.pop(0)
            return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=content))], usage=None)
        prompt = SimpleNamespace(permission_guard=SimpleNamespace(owner_context_allowed=lambda _: True), get_system_prompt=AsyncMock(return_value="椎名立希"))
        generator = TextGenerator(SimpleNamespace(user_chat_history={}), None, prompt, SimpleNamespace(get_weather_str=AsyncMock(return_value="")),
                                  SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=model))), lambda text: (text, None, None))
        self.store.accept([ChatCommand("owner", "plan", "plan", "device", "熊猫挂件挂包上了吗")])
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        result = asyncio.run(generator(snapshot, self.store))
        committed = self.store.commit_reply(job, snapshot, result)
        self.assertIn("还没买", committed["reply"])
        self.assertEqual(len(calls), 4)
        self.assertEqual(len(self.store.daily_snapshot("owner")["events"]), 1)
        self.assertEqual(self.store.read_fact("owner", "panda-event")["revision"], 1)

    def test_candidate_with_unsanitized_content_cannot_change_after_review(self):
        with self.assertRaises(DailyRejected):
            self.save(self.candidate(summary="在 31.2304, 121.4737 看见挂件，还没买。"))

    def test_daily_generation_never_holds_up_user_chat(self):
        from controlled_model_fixture import checker_response
        from services.chat_runtime import ChatRuntime
        async def scenario():
            entered, release = asyncio.Event(), asyncio.Event()
            async def model(**kwargs):
                envelope = kwargs["messages"][0]["content"]
                if "task=daily_candidate\n" in envelope:
                    entered.set()
                    await release.wait()
                    content = "null"
                else:
                    checked = checker_response(kwargs, use_memories=False)
                    if checked:
                        return checked
                    content = "嗯。"
                return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=content))], usage=None)
            client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=model)))
            prompt = SimpleNamespace(permission_guard=SimpleNamespace(owner_context_allowed=lambda _: True), get_system_prompt=AsyncMock(return_value="椎名立希"))
            generator = TextGenerator(SimpleNamespace(user_chat_history={}), None, prompt,
                                      SimpleNamespace(get_weather_str=AsyncMock(return_value="")), client, lambda text: (text, None, None))
            runtime = ChatRuntime(self.store, generator, AsyncMock(return_value=[]), AsyncMock(), poll_interval=0.002)
            self.store.schedule_daily("owner")
            runtime.start()
            try:
                await asyncio.wait_for(entered.wait(), timeout=2)
                results = await runtime.submit([ChatCommand("owner", "quick", "quick", "device", "喂")], timeout=2)
                self.assertEqual(results[0]["reply"], "嗯。")
                self.assertFalse(release.is_set())
            finally:
                release.set()
                await runtime.stop()
        asyncio.run(scenario())

    def test_a06_reply_checked_before_persona_upgrade_cannot_be_delivered(self):
        from services.public_policy import PUBLIC_POLICY_VERSION
        from services.reply_check import reply_digest
        result = {"reply": "旧人设下通过的回复。"}
        result["check"] = {"approved": True, "policy_version": PUBLIC_POLICY_VERSION, "digest": reply_digest(result)}
        with self.assertRaises(PermissionDenied):
            self.store._require_complete_check(result)


class DailyGenerationTests(unittest.TestCase):
    setUp = DailyTests.setUp
    candidate = DailyTests.candidate
    def run_worker(self, candidate_factory, *, verdict=None, mutate=None):
        calls = []
        async def create(**kwargs):
            calls.append(kwargs)
            task = kwargs["messages"][0]["content"]
            data = json.loads(kwargs["messages"][-1]["content"])
            if "task=daily_candidate\n" in task:
                value = candidate_factory(data)
            else:
                if mutate:
                    mutate()
                value = verdict or {"approved": True, "classification": "ordinary", "checks": dict.fromkeys(DAILY_CHECKS, True), "violations": []}
            if isinstance(value, Exception):
                raise value
            return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(value, ensure_ascii=False)))])
        client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=create)))
        generator = DailyGenerator(client)
        self.store.schedule_daily("owner")
        worker = DurableWorker(self.store, {"daily": lambda job: generator(job, self.store)})
        asyncio.run(worker.run_once())
        return calls

    def generated(self, data):
        return self.candidate(event_id=data["proposed_event_id"])

    def test_a01_major_original_and_fabricated_user_promise_are_discarded(self):
        for summary in ("立希死亡了。", "MyGO!!!!!今天解散了。", "我们正式成为恋人。", "用户昨天答应陪我见面。", "重病后不能继续上学了。"):
            with self.subTest(summary=summary):
                def candidate(data):
                    return self.candidate(event_id=data["proposed_event_id"], summary=summary)
                # A scripted semantic rejection tests fail-closed plumbing; it
                # is intentionally NOT advertised as real-model classification.
                self.run_worker(candidate, verdict={"approved": False, "classification": "major", "checks": dict.fromkeys(DAILY_CHECKS, False), "violations": ["ordinary_scope"]})
                self.assertEqual(self.store.daily_snapshot("owner")["events"], [])
                self.now += 86400

    def test_accepted_candidate_and_job_complete_atomically_with_public_versions(self):
        calls = self.run_worker(self.generated)
        self.assertEqual(len(calls), 2)
        events = self.store.daily_snapshot("owner")["events"]
        self.assertEqual(len(events), 1)
        for call in calls:
            content = call["messages"][0]["content"]
            for item in (PUBLIC_POLICY, PERSONA, PERSONA_VERSION, RELATIONSHIP_VERSION):
                self.assertIn(item, content)
        with self.store._transaction(write=False) as conn:
            self.assertEqual(conn.execute("SELECT status FROM jobs WHERE id=?", (self.store.schedule_daily("owner"),)).fetchone()[0], "succeeded")

    def test_a08_no_content_or_provider_failure_leaves_no_fallback_event(self):
        for response in (None, RuntimeError("synthetic provider unavailable")):
            self.run_worker(lambda _: response)
            self.assertEqual(self.store.daily_snapshot("owner")["events"], [])
            self.now += 86400

    def test_uncertain_review_never_enters_database(self):
        self.run_worker(self.generated, verdict={"approved": True, "classification": "uncertain", "checks": dict.fromkeys(DAILY_CHECKS, True), "violations": []})
        self.assertEqual(self.store.daily_snapshot("owner")["events"], [])

    def test_semantic_negation_is_not_a_banned_word_list(self):
        self.run_worker(lambda data: self.candidate(event_id=data["proposed_event_id"], summary="练习中聊到假设的话题，没有发生乐队解散。"))
        self.assertEqual(len(self.store.daily_snapshot("owner")["events"]), 1)

    def test_disable_during_review_prevents_commit(self):
        with patch.dict("os.environ", {"CHARACTER_DAILY_ENABLED": "1"}):
            import os
            self.run_worker(self.generated, mutate=lambda: os.environ.__setitem__("CHARACTER_DAILY_ENABLED", "0"))
            self.assertEqual(self.store.daily_snapshot("owner")["events"], [])

    def test_a06_missing_public_contract_blocks_outbound_before_client(self):
        with patch("services.public_policy.PERSONA_VERSION", ""):
            calls = self.run_worker(self.generated)
        self.assertEqual(calls, [])
        self.assertEqual(self.store.daily_snapshot("owner")["events"], [])


if __name__ == "__main__":
    unittest.main()
