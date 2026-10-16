"""PRD-02 deterministic acceptance, using synthetic evidence and fake time."""
from copy import deepcopy
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from services.contracts import ChatCommand, Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import PermissionDenied, VersionConflict
from services.reliable_chat_store import ReliableChatStore


class MemoryStageBTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-memory-b-")
        self.addCleanup(self.temp.cleanup)
        self.path = str(Path(self.temp.name) / "memory.sqlite3")
        self.now = 1000.0
        self.count = 0
        self.cipher = DomainCipher(b"m" * 32)
        self.store = self.reopen()

    def reopen(self, path=None):
        return ReliableChatStore(path or self.path, self.cipher, clock=lambda: self.now)

    def command(self, text="我喜欢咖啡", owner="owner"):
        self.count += 1
        return ChatCommand(owner, f"request-{self.count}", f"client-{self.count}", "device", text)

    def evidence(self, text="用户喜欢咖啡", fragment="我喜欢咖啡", **changes):
        return {"text": text, "evidence": fragment, "subject": "user", "assertion": "explicit", "temporal_type": "preference", **changes}

    def temporary(self, drink="咖啡"):
        return self.evidence(f"用户今天想喝{drink}", f"我今天想喝{drink}", temporal_type="current_state",
                             temporal_validity={"from": 900, "until": 2000, "expression": "今天", "timezone": "Asia/Shanghai", "precision": "day"})

    def express(self, evidence=None, *, text="我喜欢咖啡", use=False, complete=True):
        self.now += 1
        receipt = self.store.accept([self.command(text)])[0]
        extract = self.store.claim(kinds={"extract"})
        ids = self.store.commit_extraction(extract, [evidence or self.evidence()])
        chat = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(chat)
        if use:
            self.store.prepare_memories(snapshot, text)
        if complete:
            self.store.commit_reply(chat, snapshot, {"reply": "知道了。"})
        return ids, receipt, chat, snapshot

    def formal(self):
        ids = []
        for _ in range(4):
            ids, *_ = self.express()
        return ids[0]

    def data(self, fact_id):
        return self.store.read_fact("owner", fact_id)["data"]

    def test_a01_temporary_expressions_promote_at_exact_weights(self):
        ids = []
        for expected in (3, 4, 5, 6):
            result, *_ = self.express(self.temporary(), text="我今天想喝咖啡")
            ids += result
            data = self.data(result[0])
            self.assertEqual(data["weight"], expected)
            self.assertEqual(data["score"], expected/10)
            self.assertEqual(data["stage"], "formal" if expected == 6 else "candidate")
            self.assertEqual(data["truth_status"], "accepted")
        self.assertEqual(len(set(ids)), 1)

    def test_a02_batch_and_duplicate_fact_never_manufacture_evidence(self):
        commands = [self.command("我今天想喝咖啡"), self.command("我今天想喝咖啡")]
        receipts = self.store.accept(commands)
        self.assertEqual(len({r["turn_id"] for r in receipts}), 1)
        all_ids = []
        for _ in commands:
            job = self.store.claim(kinds={"extract"})
            all_ids += self.store.commit_extraction(job, [self.temporary(), self.temporary()])
        self.assertEqual(len(set(all_ids)), 1)
        self.assertEqual(self.data(all_ids[0])["weight"], 3)
        self.assertTrue(all(r["duplicate"] for r in self.store.accept(commands)))
        with self.assertRaises(VersionConflict):
            self.store.commit_extraction(job, [self.evidence()])
        self.assertEqual(self.data(all_ids[0])["weight"], 3)

    def test_a03_promotion_turn_cannot_also_score_usage(self):
        for _ in range(3):
            self.express(self.temporary(), text="我今天想喝咖啡")
        ids, _, _, snapshot = self.express(self.temporary(), text="我今天想喝咖啡", use=True)
        self.assertEqual(len(snapshot["memory_refs"]), 1)
        self.assertEqual(self.data(ids[0])["weight"], 6)
        self.express(self.temporary(), text="我今天想喝咖啡", use=True)
        self.assertEqual(self.data(ids[0])["weight"], 7)

    def test_a04_candidate_and_raw_chat_history_are_not_reply_context(self):
        for _ in range(3):
            ids, *_ = self.express(self.temporary(), text="我今天想喝咖啡")
        self.assertEqual(self.data(ids[0])["weight"], 5)
        self.assertEqual(self.store.select_memories("owner", "咖啡"), [])
        self.assertEqual(self.store.history("owner", for_model=True), [])
        self.assertEqual(len(self.store.history("owner")), 6)
        self.store.accept([self.command()])
        job = self.store.claim(kinds={"extract"})
        self.assertIn("用户今天想喝咖啡", str(self.store.extraction_candidates(job)))

    def test_a05_failed_and_unselected_drafts_do_not_score(self):
        fact = self.formal()
        ids, receipt, job, snapshot = self.express(use=True, complete=False)
        with patch.object(self.store, "_change", side_effect=OSError("synthetic disk failure")):
            with self.assertRaises(OSError):
                self.store.commit_reply(job, snapshot, {"reply": "草稿"})
        self.assertEqual(self.data(fact)["weight"], 6)
        self.assertEqual(self.store.turn_result("owner", receipt["turn_id"])["status"], "running")
        self.store.commit_reply(job, snapshot, {"reply": "已保存"})
        self.assertEqual(self.data(fact)["weight"], 7)
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(job, snapshot, {"reply": "重复"})
        self.express(use=False)
        self.assertEqual(self.data(fact)["weight"], 7)

    def test_a06_high_weight_cannot_override_independent_states(self):
        fact = self.formal()
        original = self.store.read_fact("owner", fact)
        for changes in ({"truth_status": "disputed"}, {"revision_status": "retracted"}, {"truth_status": "rejected"}, {"stage": "candidate"}, {"enabled": False, "model_permission": "blocked"}):
            with self.subTest(changes=changes):
                row = self.store.read_fact("owner", fact)
                data = {**original["data"], "weight": 10, **changes}
                if data["enabled"] != row["data"]["enabled"]:
                    self.store.set_permission("owner", fact, enabled=data["enabled"], expected_revision=row["revision"])
                else:
                    self.store.put_fact("owner", fact, Domain.USER_FACT, data, expected_revision=row["revision"])
                self.assertEqual(self.store.select_memories("owner", "咖啡"), [])
                with self.assertRaises(PermissionDenied):
                    self.store.read_fact("owner", fact, for_model=True)

    def test_a07_block_survives_new_id_restart_and_backup(self):
        fact = self.formal()
        row = self.store.read_fact("owner", fact)
        revision = self.store.set_permission("owner", fact, enabled=False, expected_revision=row["revision"])
        before = self.data(fact)
        self.store = self.reopen()
        self.assertEqual(self.store.select_memories("owner", "咖啡"), [])
        with self.assertRaises(PermissionDenied):
            self.store.put_fact("owner", "new-id", Domain.USER_FACT, {**before, "enabled": True, "model_permission": "allowed"}, expected_revision=0)
        ids, *_ = self.express()
        self.assertEqual(ids, [])
        self.assertEqual(self.data(fact)["weight"], 6)
        backup = str(Path(self.temp.name) / "backup.sqlite3")
        self.store.backup(backup)
        recovered = self.reopen(backup)
        with self.assertRaises(PermissionDenied):
            recovered.select_memories("owner", "咖啡")
        with self.assertRaises(PermissionDenied):
            recovered.verify_restored_permissions(self.store.change_head()+1)
        self.store.set_permission("owner", fact, enabled=True, expected_revision=revision)
        self.assertEqual(self.data(fact)["weight"], 6)

    def test_a08_trip_dates_and_inference_stay_separate(self):
        first = self.evidence("用户去过京都", "我去过京都", temporal_type="past_event", temporal_validity={"from": 100, "until": 200})
        second = {**first, "temporal_validity": {"from": 300, "until": 400}}
        a, *_ = self.express(first, text="我去过京都")
        b, *_ = self.express(second, text="我去过京都")
        self.assertNotEqual(a, b)
        for _ in range(5):
            inferred, *_ = self.express(self.evidence("用户喜欢茶", "朋友喜欢茶", assertion="inferred", subject="friend"), text="朋友喜欢茶")
        self.assertEqual(self.data(inferred[0])["truth_status"], "pending")
        self.assertEqual(self.data(inferred[0])["weight"], 0)

    def test_a09_expired_plan_and_sickness_never_become_current_facts(self):
        for temporal in ("plan", "current_state"):
            item = self.evidence("用户明天去旅行" if temporal == "plan" else "用户今天感冒", "我喜欢咖啡", temporal_type=temporal, temporal_validity={"from": 900, "until": 1100, "expression": "明天" if temporal == "plan" else "今天", "timezone": "Asia/Shanghai", "precision": "day"})
            for _ in range(4):
                ids, *_ = self.express(item)
            self.assertEqual(len(self.store.select_memories("owner", item["text"])), 1)
            self.now = 1200
            self.assertEqual(self.store.select_memories("owner", item["text"]), [])
            self.assertEqual(self.data(ids[0])["temporal_type"], temporal)
            self.now = 1000

    def test_a10_bot_and_unsubstantiated_fragments_never_confirm_user(self):
        for _ in range(5):
            ids, *_ = self.express(self.evidence(fragment="not in source"))
        self.assertEqual(self.data(ids[0])["truth_status"], "pending")
        self.assertEqual(self.data(ids[0])["weight"], 0)
        self.assertEqual(self.store.select_memories("owner", "咖啡"), [])
        self.assertEqual(self.store.select_memories("other-owner", "咖啡"), [])

    def test_a11_stale_revision_and_transaction_failure_leave_no_half_effect(self):
        fact = self.formal()
        _, _, job, snapshot = self.express(use=True, complete=False)
        current = self.store.read_fact("owner", fact)
        self.store.set_permission("owner", fact, enabled=False, expected_revision=current["revision"])
        with self.assertRaises(VersionConflict):
            self.store.set_permission("owner", fact, enabled=True, expected_revision=current["revision"])
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(job, snapshot, {"reply": "越权草稿"})
        self.assertEqual(self.data(fact)["weight"], 6)

    def test_explicit_retention_and_transient_unknown_time_fail_closed(self):
        fact = self.formal()
        row = self.store.read_fact("owner", fact)
        for changes in ({"retention_until": self.now-1}, {"temporal_type": "current_state"}, {"temporal_type": "plan"}):
            self.store.put_fact("owner", fact, Domain.USER_FACT, {**row["data"], **changes}, expected_revision=self.store.read_fact("owner", fact)["revision"])
            self.assertEqual(self.store.select_memories("owner", "咖啡"), [])

    def test_disable_after_commit_blocks_undelivered_reply(self):
        fact = self.formal()
        self.express(use=True)
        self.store.set_permission("owner", fact, enabled=False, expected_revision=self.store.read_fact("owner", fact)["revision"])
        blocked = False
        while job := self.store.claim(kinds={"deliver"}):
            try:
                self.store.delivery(job)
            except PermissionDenied:
                blocked = True
            self.store.finish(job["job_id"], job["lease_token"], succeeded=True)
        self.assertTrue(blocked)

    def test_permission_commands_ask_on_ambiguity_and_preserve_score(self):
        fact = self.formal()
        self.store.accept([self.command("以后别再提我喜欢咖啡")])
        job = self.store.claim(kinds={"chat"})
        result = self.store.handle_memory_control(job)
        self.assertIn("不再使用", result["reply"])
        self.assertFalse(self.data(fact)["enabled"])
        self.assertEqual(self.data(fact)["weight"], 6)
        self.assertEqual(self.store.handle_memory_control(job), result)
        self.store.commit_reply(job, self.store.begin_turn(job), result)
        self.store.accept([self.command("恢复使用不确定是哪件事")])
        job = self.store.claim(kinds={"chat"})
        self.assertIn("哪条", self.store.handle_memory_control(job)["reply"])
        self.assertFalse(self.data(fact)["enabled"])

    def test_lore_staging_review_publication_is_atomic_and_keeps_actors(self):
        source = {"work": "synthetic animation", "locator": "episode 1", "language": "ja", "url": "https://example.invalid/official", "acquired_at": 900, "acquisition": "synthetic test fixture"}
        entries = [{"entry_id": "scene-1", "subject": "爱音", "text": "爱音把立希叫到 RiNG", "source": source}, {"entry_id": "scene-2", "subject": "立希", "text": "待核验条目", "source": source}]
        self.store.stage_lore("owner", "v1", entries)
        with self.assertRaises(PermissionDenied):
            self.store.publish_lore("owner", "v1")
        self.assertEqual(self.store.select_memories("owner", "RiNG"), [])
        self.store.review_lore("owner", "v1", {"scene-1": "verified", "scene-2": "conflict"}, reviewer="fixture-reviewer", verification_record="fixture-only", replay=[{"case": "actor-preservation", "passed": True}])
        with patch.object(self.store, "_change", side_effect=OSError("synthetic publication failure")):
            with self.assertRaises(OSError):
                self.store.publish_lore("owner", "v1")
        self.assertEqual(self.store.select_memories("owner", "RiNG"), [])
        result = self.store.publish_lore("owner", "v1")
        self.assertEqual((result["published"], result["isolated"]), (1, 1))
        memory = self.store.select_memories("owner", "RiNG")[0]
        self.assertEqual(memory["weight"], 10)
        self.assertEqual(self.data(memory["fact_id"])["subject"], "爱音")
        with self.assertRaises(VersionConflict):
            self.store.publish_lore("owner", "v1")

    def test_corrections_reset_formal_to_six_and_candidate_to_three(self):
        fact = self.formal()
        for _ in range(4):
            self.express(use=True)
        self.assertEqual(self.data(fact)["weight"], 10)
        old = self.store.read_fact("owner", fact)
        statement = "之前说错了，我喜欢茶"
        correction = self.evidence("用户喜欢茶", statement, operation="correct", fact_id=fact, expected_revision=old["revision"])
        ids, _, _, snapshot = self.express(correction, text=statement, use=True)
        self.assertEqual(ids, [fact])
        self.assertEqual(self.data(fact)["weight"], 6)
        self.assertNotEqual(self.data(fact)["revision_id"], old["data"]["revision_id"])
        history = self.store.revision_history("owner", fact)
        self.assertEqual(history[0]["revisionStatus"], "superseded")
        self.assertEqual(history[-1]["revisionStatus"], "active")
        ids, *_ = self.express(self.temporary("可可"), text="我今天想喝可可")
        candidate = self.store.read_fact("owner", ids[0])
        statement = "之前说错了，我今天想喝牛奶"
        self.express({**self.temporary("牛奶"), "evidence": statement, "operation": "correct", "fact_id": ids[0], "expected_revision": candidate["revision"]}, text=statement)
        self.assertEqual(self.data(ids[0])["weight"], 3)
        self.assertEqual(self.data(ids[0])["stage"], "candidate")

    def test_blocked_correction_inherits_permission_without_model_reading_body(self):
        fact = self.formal()
        row = self.store.read_fact("owner", fact)
        self.store.set_permission("owner", fact, enabled=False, expected_revision=row["revision"])
        statement = "之前说我喜欢咖啡是错的，我喜欢茶"
        correction = self.evidence("用户喜欢茶", statement, operation="correct", target_text="我喜欢咖啡")
        ids, *_ = self.express(correction, text=statement)
        self.assertEqual(ids, [fact])
        self.assertEqual(self.data(fact)["text"], "用户喜欢茶")
        self.assertEqual(self.data(fact)["model_permission"], "blocked")
        self.assertEqual(self.data(fact)["weight"], 6)
        self.assertEqual(self.store.select_memories("owner", "茶"), [])

    def test_dispute_then_explicit_confirmation_preserves_score_stage_permission(self):
        fact = self.formal()
        old = self.store.read_fact("owner", fact)
        ambiguous = "咖啡也就那样"
        dispute = self.evidence("用户喜欢咖啡", ambiguous, operation="dispute", assertion="inferred", fact_id=fact, expected_revision=old["revision"])
        _, _, _, snapshot = self.express(dispute, text=ambiguous)
        self.assertTrue(snapshot["memory_clarification"])
        self.assertEqual(self.data(fact)["truth_status"], "disputed")
        self.assertEqual(self.store.select_memories("owner", "咖啡"), [])
        row = self.store.read_fact("owner", fact)
        statement = "我明确确认，我一直喜欢咖啡"
        confirmation = self.evidence("用户喜欢咖啡", statement, operation="confirm", fact_id=fact, expected_revision=row["revision"])
        self.express(confirmation, text=statement, use=True)
        self.assertEqual(self.data(fact)["truth_status"], "accepted")
        self.assertEqual(self.data(fact)["weight"], 6)
        self.assertEqual(self.data(fact)["stage"], "formal")

    def test_normal_stable_preference_change_keeps_past_and_starts_new_at_six(self):
        old_fact = self.formal()
        row = self.store.read_fact("owner", old_fact)
        statement = "最近我改成喜欢茶了"
        changed = self.evidence("用户喜欢茶", statement, operation="change", fact_id=old_fact, expected_revision=row["revision"], temporal_validity={"from": self.now+1, "until": None, "expression": "最近", "timezone": "Asia/Shanghai", "precision": "unknown"})
        ids, *_ = self.express(changed, text=statement)
        self.assertNotEqual(ids, [old_fact])
        self.assertEqual(self.data(old_fact)["text"], "用户喜欢咖啡")
        self.assertEqual(self.data(old_fact)["temporal_validity"]["until"], self.now)
        self.assertEqual(self.data(ids[0])["weight"], 6)
        self.assertEqual(self.store.select_memories("owner", "咖啡"), [])

    def test_failed_correction_rolls_back_revision_evidence_and_old_eligibility(self):
        fact = self.formal()
        row = self.store.read_fact("owner", fact)
        statement = "之前说错了，我喜欢茶"
        self.store.accept([self.command(statement)])
        job = self.store.claim(kinds={"extract"})
        correction = self.evidence("用户喜欢茶", statement, operation="correct", fact_id=fact, expected_revision=row["revision"])
        with patch.object(self.store, "_change", side_effect=OSError("synthetic revision failure")):
            with self.assertRaises(OSError):
                self.store.commit_extraction(job, [correction])
        self.assertEqual(self.store.read_fact("owner", fact), row)
        self.store.commit_extraction(job, [correction])
        self.assertEqual(self.data(fact)["text"], "用户喜欢茶")

    def test_existing_allowed_candidates_can_strengthen_while_unknown_ids_wait_for_review(self):
        fact = self.formal()
        ids, *_ = self.express(self.temporary("牛奶"), text="我今天想喝牛奶")
        other = ids[0]
        self.store.set_permission("owner", fact, enabled=False, expected_revision=self.store.read_fact("owner", fact)["revision"])
        self.express(self.temporary("牛奶"), text="我今天想喝牛奶")
        self.assertEqual(self.data(other)["weight"], 4)
        ids, *_ = self.express(self.evidence("用户喜欢未知饮料", "我喜欢未知饮料"), text="我喜欢未知饮料")
        self.assertEqual(ids, [])
        with closing(sqlite3.connect(self.path)) as conn:
            self.assertEqual(conn.execute("SELECT COUNT(*) FROM memory_pending").fetchone()[0], 1)

    def test_new_user_rule_stable_explicit_starts_formal_at_six_without_same_turn_bonus(self):
        ids, _, _, snapshot = self.express(use=True)
        self.assertEqual(self.data(ids[0])["weight"], 6)
        self.assertEqual(self.data(ids[0])["stage"], "formal")
        self.assertEqual(len(snapshot["memory_refs"]), 1)
        self.express(use=True)
        self.assertEqual(self.data(ids[0])["weight"], 7)

    def test_feature_switches_stop_new_effects_but_never_permission_filters(self):
        fact = self.formal()
        with patch.dict("os.environ", {"MEMORY_COLLECTION_ENABLED": "0", "MEMORY_REINFORCEMENT_ENABLED": "0"}):
            ids, *_ = self.express(use=True)
            self.assertEqual(ids, [])
            self.assertEqual(self.data(fact)["weight"], 6)
            row = self.store.read_fact("owner", fact)
            self.store.set_permission("owner", fact, enabled=False, expected_revision=row["revision"])
            self.assertEqual(self.store.select_memories("owner", "咖啡"), [])
            with self.assertRaises(PermissionDenied):
                self.store.read_fact("owner", fact, for_model=True)

    def test_explicit_remember_request_does_not_skip_temporary_evidence_threshold(self):
        statement = "你记住我明天去京都"
        item = self.evidence("用户计划明天去京都", statement, temporal_type="plan", temporal_validity={"from": 1100, "until": 2000, "expression": "明天", "timezone": "Asia/Shanghai", "precision": "day"})
        ids, *_ = self.express(item, text=statement)
        self.assertEqual(self.data(ids[0])["weight"], 3)
        self.assertEqual(self.data(ids[0])["stage"], "candidate")


if __name__ == "__main__":
    unittest.main()
