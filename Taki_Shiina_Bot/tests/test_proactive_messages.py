"""Synthetic PRD-05 eligibility, transaction and recovery checks."""
from concurrent.futures import ThreadPoolExecutor
import asyncio
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from services.contracts import ChatCommand
from services.daily_policy import DAILY_CHECKS, RULE_VERSIONS, digest
from services.domain_cipher import DomainCipher
from services.foundation_store import PermissionDenied, VersionConflict
from services.proactive_generation import ProactiveGenerator, REVIEW_FIELDS
from services.proactive_policy import eligible, in_window, period_keys
from services.public_policy import PUBLIC_POLICY_VERSION, PROMPT_VERSIONS
from services.reliable_chat_store import ReliableChatStore
from services.reply_check import reply_digest


def at(day, hour, minute=0, second=0):
    return datetime(2026, 10, day, hour - 8, minute, second, tzinfo=timezone.utc).timestamp()


class ProactivePolicyTests(unittest.TestCase):
    def test_beijing_window_and_unanswered_boundaries(self):
        state = {"enabled": True, "unanswered": 0, "last_sent_at": None, "version": 0}
        for hour, minute, expected in ((8, 59, False), (9, 0, True), (22, 59, True), (23, 0, False)):
            self.assertEqual(in_window(at(19, hour, minute)), expected)
            self.assertEqual(eligible(at(19, hour, minute), state, 0, 0), expected)
        state.update(unanswered=1, last_sent_at=at(19, 20))
        self.assertFalse(eligible(at(21, 19, 59, 59), state, 0, 0))
        self.assertTrue(eligible(at(21, 20), state, 0, 0))
        state["unanswered"] = 2
        self.assertFalse(eligible(at(26, 20), state, 0, 0))
        state["unanswered"] = 0
        self.assertFalse(eligible(at(26, 20), state, 1, 0))
        self.assertFalse(eligible(at(26, 20), state, 0, 4))
        self.assertEqual(period_keys(at(25, 20))[1], "2026-10-19")
        self.assertEqual(period_keys(at(26, 20))[1], "2026-10-26")


class ProactiveStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-proactive-")
        self.addCleanup(self.temp.cleanup)
        self.env = patch.dict(os.environ, {"PROACTIVE_MESSAGES_ENABLED": "1", "CHARACTER_DAILY_ENABLED": "1"})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.now = at(19, 20)
        self.path = str(Path(self.temp.name) / "synthetic.sqlite3")
        self.store = ReliableChatStore(self.path, DomainCipher(b"p" * 32), clock=lambda: self.now, require_checks=True)

    def daily(self, identity):
        snapshot = self.store.daily_snapshot("owner")
        candidate = {"event_id": identity, "expected_revision": 0, "event_type": "composition",
                     "participants": ["椎名立希"], "object": identity + "的编曲", "status": "completed",
                     "summary": "改好了" + identity + "的编曲。", "scheduled_at": None,
                     "occurred_at": datetime.fromtimestamp(self.now, timezone.utc).isoformat(),
                     "outcome": "none", "previous_refs": [], "chat_refs": []}
        approval = {"approved": True, "classification": "ordinary", "checks": dict.fromkeys(DAILY_CHECKS, True),
                    "violations": [], "rule_versions": dict(RULE_VERSIONS),
                    "digest": digest({"candidate": candidate, "snapshot": snapshot})}
        return self.store.commit_daily("owner", candidate, snapshot, approval, slot=identity)

    def claimed(self, identity):
        day, _ = period_keys(self.now)
        self.store.enqueue("owner", "proactive", identity, {"owner": "owner", "day": day,
                                                             "candidate_id": identity}, max_attempts=1)
        return self.store.claim(kinds=("proactive",))

    @staticmethod
    def reviewed(reply):
        result = {"reply": reply}
        check = {"approved": True, "used_memory_ids": [], "policy_version": PUBLIC_POLICY_VERSION,
                 "prompt_versions": dict(PROMPT_VERSIONS), "digest": reply_digest(result)}
        review = {"approved": True, "checks": {}, "policy_version": PUBLIC_POLICY_VERSION,
                  "prompt_versions": dict(PROMPT_VERSIONS), "digest": reply_digest(result)}
        return {**result, "check": check, "proactive_review": review}

    def commit(self, identity):
        self.daily(identity)
        snapshot = self.store.proactive_snapshot("owner")
        self.assertIsNotNone(snapshot)
        return self.store.commit_proactive(self.claimed(identity), snapshot, self.reviewed("那段编曲改好了。"))

    def test_atomic_inbox_quota_and_unanswered_pause_survive_restart(self):
        first = self.commit("first")
        self.assertEqual(self.store.proactive_status("owner")["unanswered"], 1)
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 1)
        items, pending = self.store.history_delivery("owner")
        self.assertEqual((len(items), len(pending)), (1, 1))
        self.assertEqual(items[0]["messageId"], first["messageId"])
        self.now = at(21, 19, 59, 59)
        self.assertIsNone(self.store.proactive_snapshot("owner"))
        self.now = at(21, 20)
        second = self.commit("second")
        self.assertNotEqual(first["messageId"], second["messageId"])
        self.now = at(26, 20)
        reopened = ReliableChatStore(self.path, DomainCipher(b"p" * 32), clock=lambda: self.now)
        self.assertEqual(reopened.proactive_status("owner")["unanswered"], 2)
        self.assertIsNone(reopened.proactive_snapshot("owner"))

    def test_backup_restore_keeps_quota_and_pause(self):
        self.commit("first")
        backup_path = str(Path(self.temp.name) / "backup.sqlite3")
        sequence = self.store.change_head()
        self.store.backup(backup_path)
        restored = ReliableChatStore(backup_path, DomainCipher(b"p" * 32), clock=lambda: self.now)
        with self.assertRaises(PermissionDenied):
            restored.proactive_status("owner")
        restored.verify_restored_permissions(sequence)
        self.assertEqual(restored.proactive_status("owner")["unanswered"], 1)
        self.assertEqual(restored.proactive_status("owner")["daily_used"], 1)

    def test_user_message_cancels_unsent_reminder_but_keeps_inbox(self):
        result = self.commit("first")
        command = ChatCommand("owner", "request-1", "client-1", "device-1", "你好")
        self.store.accept([command])
        self.assertEqual(self.store.proactive_status("owner")["unanswered"], 0)
        items, pending = self.store.history_delivery("owner")
        self.assertEqual(items[0]["messageId"], result["messageId"])
        self.assertEqual(pending, [])

    def test_user_message_cancels_retry_after_transport_failure(self):
        result = self.commit("first")
        self.store.mark_dispatched("owner", result)
        self.store.accept([ChatCommand("owner", "reply-after-failure", "client-after-failure", "device-1", "你好")])
        with self.assertRaises(PermissionDenied):
            self.store.mark_dispatched("owner", result)
        items, pending = self.store.history_delivery("owner")
        self.assertEqual(items[0]["messageId"], result["messageId"])
        self.assertEqual(pending, [])

    def test_disable_command_and_settings_do_not_erase_quota(self):
        self.commit("first")
        self.store.accept([ChatCommand("owner", "request-off", "client-off", "device-1", "关闭主动消息")])
        self.assertFalse(self.store.proactive_status("owner")["enabled"])
        self.assertEqual(self.store.proactive_status("owner")["unanswered"], 1)
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 1)
        self.store.set_proactive_enabled("owner", True)
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 1)

    def test_user_input_and_source_disable_fence_old_candidate(self):
        self.daily("first")
        snapshot = self.store.proactive_snapshot("owner")
        job = self.claimed("first")
        self.store.accept([ChatCommand("owner", "request-1", "client-1", "device-1", "你好")])
        with self.assertRaises(VersionConflict):
            self.store.commit_proactive(job, snapshot, self.reviewed("编曲改好了。"))
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 0)

    def test_source_disable_and_global_stop_keep_inbox_without_reminder(self):
        result = self.commit("first")
        self.store.set_permission("owner", "first", enabled=False, expected_revision=1)
        items, pending = self.store.history_delivery("owner")
        self.assertEqual(items[0]["messageId"], result["messageId"])
        self.assertEqual(pending, [])
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 1)
        with patch.dict(os.environ, {"PROACTIVE_MESSAGES_ENABLED": "0"}):
            self.assertFalse(self.store.proactive_status("owner")["globally_enabled"])
            with self.assertRaises(PermissionDenied):
                self.store.mark_dispatched("owner", result)

    def test_failed_transaction_leaves_no_message_or_quota(self):
        self.daily("first")
        snapshot = self.store.proactive_snapshot("owner")
        job = self.claimed("first")
        with patch.object(self.store, "_change", side_effect=OSError("synthetic disk failure")):
            with self.assertRaises(OSError):
                self.store.commit_proactive(job, snapshot, self.reviewed("编曲改好了。"))
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 0)
        self.assertEqual(self.store.history("owner"), [])

    def test_two_workers_only_one_commit_and_failure_rolls_back(self):
        self.daily("first")
        snapshot = self.store.proactive_snapshot("owner")
        job = self.claimed("first")
        def attempt(_):
            try:
                return self.store.commit_proactive(job, snapshot, self.reviewed("编曲改好了。"))
            except (VersionConflict, PermissionDenied):
                return None
        with ThreadPoolExecutor(max_workers=2) as pool:
            outcomes = list(pool.map(attempt, range(2)))
        self.assertEqual(sum(value is not None for value in outcomes), 1)
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 1)

    def test_fifth_eligible_day_still_hits_weekly_cap(self):
        for day in range(19, 23):
            self.now = at(day, 20)
            self.commit("event-" + str(day))
            receipt = self.store.accept([ChatCommand("owner", "reply-" + str(day), "client-" + str(day),
                                                     "device-1", "收到")])[0]
            self.store.cancel("owner", receipt["turn_id"])
        self.now = at(23, 20)
        self.daily("event-23")
        self.assertEqual(self.store.proactive_status("owner")["weekly_used"], 4)
        self.assertIsNone(self.store.proactive_snapshot("owner"))

    def test_outbox_failure_does_not_change_topic_count(self):
        result = self.commit("first")
        while True:
            job = self.store.claim(kinds=("deliver",))
            self.assertIsNotNone(job)
            if self.store.delivery(job)["target"] == "chat":
                break
            self.store.finish(job["job_id"], job["lease_token"], succeeded=True)
        self.assertEqual(self.store.delivery(job)["result"]["messageId"], result["messageId"])
        self.store.finish(job["job_id"], job["lease_token"], succeeded=False)
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 1)

    def test_generation_uses_reviewed_source_and_rejects_pressure(self):
        self.daily("first")
        job = self.claimed("first")

        class FakeCompletions:
            def __init__(self, approve):
                self.approve = approve
                self.tasks = []

            async def create(self, **kwargs):
                system = kwargs["messages"][0]["content"]
                self.tasks.append(system)
                if "task=proactive_chat" in system:
                    data = {"reply": "编曲改好了一小段，想给你听听。"}
                else:
                    data = {"approved": self.approve, "checks": dict.fromkeys(REVIEW_FIELDS, self.approve),
                            "violations": [] if self.approve else ["no_pressure"]}
                return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(data, ensure_ascii=False)))])

        async def checker(result, context, validate):
            self.assertEqual(context["character_daily"][0]["fact_id"], "first")
            await validate()
            return {"approved": True, "used_memory_ids": [], "policy_version": PUBLIC_POLICY_VERSION,
                    "prompt_versions": dict(PROMPT_VERSIONS), "digest": reply_digest(result)}

        completions = FakeCompletions(False)
        client = SimpleNamespace(chat=SimpleNamespace(completions=completions))
        self.assertIsNone(asyncio.run(ProactiveGenerator(client, checker)(job, self.store)))
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 0)
        completions.approve = True
        committed = asyncio.run(ProactiveGenerator(client, checker)(job, self.store))
        self.assertEqual(committed.result["messageKind"], "proactive")
        self.assertEqual(self.store.proactive_status("owner")["daily_used"], 1)
        self.assertTrue(all(PUBLIC_POLICY_VERSION in task for task in completions.tasks))
