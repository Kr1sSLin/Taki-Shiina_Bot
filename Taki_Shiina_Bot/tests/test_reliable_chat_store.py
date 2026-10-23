"""Synthetic durable-chat failure, concurrency, identity, and recovery replays."""
import base64
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from copy import deepcopy
from dataclasses import replace
import hashlib
import pathlib
import sqlite3
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from services.contracts import ChatCommand, Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import PermissionDenied, VersionConflict
from services.reliable_chat_store import ReliableChatStore


class ReliableChatStoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-reliable-chat-")
        self.addCleanup(self.temp.cleanup)
        self.path = str(pathlib.Path(self.temp.name) / "chat.db")
        self.cipher = DomainCipher(b"s" * 32)
        self.now = 1000.0
        self.store = self.reopen()

    def reopen(self, path=None):
        return ReliableChatStore(path or self.path, self.cipher, busy_timeout=1, clock=lambda: self.now)

    def command(self, identity="1", *, owner="owner", text=None):
        return ChatCommand(owner, f"request-{identity}", f"client-{identity}", "http-device", text or f"synthetic input {identity}")

    def counts(self, store=None):
        with closing(sqlite3.connect((store or self.store).path)) as conn:
            return {table: conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0] for table in ("requests", "chat_turns", "chat_messages", "facts", "events", "jobs", "outbox")}

    def jobs(self, store=None):
        with closing(sqlite3.connect((store or self.store).path)) as conn:
            return conn.execute("SELECT id,status,attempts,lease_until,lease_token FROM jobs ORDER BY id").fetchall()

    def begin(self, identity="1", *, owner="owner", lease_seconds=30):
        receipt = self.store.accept([self.command(identity, owner=owner)])[0]
        job = self.store.claim(kinds={"chat"}, lease_seconds=lease_seconds)
        return receipt, job, self.store.begin_turn(job)

    def complete(self, identity="1", *, owner="owner"):
        receipt, job, snapshot = self.begin(identity, owner=owner)
        result = self.store.commit_reply(job, snapshot, {"reply": f"synthetic reply {identity}"})
        self.store.mark_dispatched(owner, result)
        return receipt, result

    def crash_reply_process(self, mode):
        script = """
import os, sys
sys.path.insert(0, sys.argv[2])
from services.domain_cipher import DomainCipher
from services.reliable_chat_store import ReliableChatStore
store = ReliableChatStore(sys.argv[1], DomainCipher(b's' * 32), clock=lambda: 1000.0)
job = store.claim(kinds={'chat'}, lease_seconds=1)
snapshot = store.begin_turn(job)
if sys.argv[3] == 'during-transaction':
    original = store._change
    def crash(*args, **kwargs):
        original(*args, **kwargs)
        os._exit(77)
    store._change = crash
store.commit_reply(job, snapshot, {'reply': 'synthetic subprocess reply'})
os._exit(78)
"""
        return subprocess.run([sys.executable, "-B", "-c", script, self.path, str(pathlib.Path(__file__).resolve().parents[1]), mode], capture_output=True, timeout=15)

    def test_text_budget_splits_batch_without_losing_sources_and_old_ids_replay(self):
        from services.input_budget import InputTooLong, turn_fits
        before = self.counts()
        with self.assertRaises(InputTooLong):
            self.store.accept([self.command("too-long", text="😀" * 8001)])
        self.assertEqual(self.counts(), before)
        commands = [self.command(f"bounded-{i}", text="中" * 6000) for i in range(3)]
        receipts = self.store.accept(commands, delay=8, merge_pending=True)
        self.assertEqual(receipts[0]["turn_id"], receipts[1]["turn_id"])
        self.assertNotEqual(receipts[1]["turn_id"], receipts[2]["turn_id"])
        job = self.store.claim(kinds={"chat"})
        self.assertIsNone(job)
        self.now += 8
        for turn in dict.fromkeys(receipt["turn_id"] for receipt in receipts):
            job = self.store.claim(kinds={"chat"})
            snapshot = self.store.begin_turn(job)
            self.assertTrue(turn_fits([source["text"] for source in snapshot["sources"]]))
            result = self.store.commit_reply(job, snapshot, {"reply": "已保留完整来源"})
            self.store.mark_dispatched("owner", result)
        with patch("services.input_budget.TEXT_CODEPOINTS", 1):
            self.assertTrue(self.store.accept([commands[0]])[0]["duplicate"])
        self.assertEqual(self.counts()["requests"], 3)

    def test_merge_source_count_and_paid_gift_cannot_overflow_text_turn(self):
        commands = [self.command(f"source-{i}") for i in range(9)]
        receipts = self.store.accept(commands, delay=8, merge_pending=True)
        self.assertEqual(len({r["turn_id"] for r in receipts[:8]}), 1)
        self.assertNotEqual(receipts[7]["turn_id"], receipts[8]["turn_id"])
        full = self.store.accept([self.command("full-a", text="中" * 6000), self.command("full-b", text="中" * 6000)], delay=8, merge_pending=True)
        gift = ChatCommand("owner", "bounded-gift", "bounded-gift", "device", "咖啡", intent="interaction", metadata={"merge_after_payment": True})
        isolated = self.store.accept([gift], delay=8)[0]
        self.assertEqual(self.store.confirm_interaction("owner", "bounded-gift"), isolated["turn_id"])
        self.assertNotEqual(isolated["turn_id"], full[0]["turn_id"])
        self.assertEqual(self.store.request("owner", "request-full-a")["turn_id"], full[0]["turn_id"])


    def test_concurrent_cross_protocol_retry_has_one_source_turn_and_extraction(self):
        command = self.command()
        barrier = threading.Barrier(2)

        def receive(device):
            store = self.reopen()
            barrier.wait(timeout=5)
            return store.accept([replace(command, device_id=device)])[0]

        with ThreadPoolExecutor(max_workers=2) as workers:
            receipts = list(workers.map(receive, ("http-device", "ws-device")))
        self.assertEqual({r["message_id"] for r in receipts}, {receipts[0]["message_id"]})
        self.assertEqual({r["turn_id"] for r in receipts}, {receipts[0]["turn_id"]})
        self.assertEqual(sorted(r["duplicate"] for r in receipts), [False, True])
        self.assertEqual((self.counts()["requests"], self.counts()["chat_turns"], self.counts()["jobs"]), (1, 1, 2))
        for changed in (replace(command, text="changed"), replace(command, context_revision=1), replace(command, client_message_id="other-client"), replace(command, request_id="other-request")):
            with self.subTest(changed=changed):
                with self.assertRaises(VersionConflict):
                    self.reopen().accept([changed])

    def test_image_source_round_trips_exact_bytes_without_leaking_into_history_or_delivery(self):
        raw = b"\xff\xd8\xff\xe1Exif\x00\x00GPSLatitude=31.2304;GPSLongitude=121.4737\xff\xd9"
        image = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()}
        command = replace(self.command("image", text="[图片]"), metadata={"images": [image]})
        receipt = self.store.accept([command])[0]
        retry = self.reopen().accept([command])[0]
        self.assertTrue(retry["duplicate"])
        self.assertEqual((retry["turn_id"], retry["message_id"]), (receipt["turn_id"], receipt["message_id"]))
        reopened = self.reopen()
        job = reopened.claim(kinds={"chat"})
        snapshot = reopened.begin_turn(job)
        self.assertEqual(base64.b64decode(snapshot["sources"][0]["metadata"]["images"][0]["dataBase64"]), raw)
        result = reopened.commit_reply(job, snapshot, {"reply": "synthetic image response"})
        self.assertEqual((result["contentType"], result["imageCount"]), ("image", 1))
        history = self.reopen().history("owner")
        self.assertEqual((history[0]["contentType"], history[0]["imageCount"], history[0]["content"]), ("image", 1, "[图片]"))
        self.assertEqual((history[1]["content"], history[1]["contentType"], history[1]["imageCount"]), ("synthetic image response", result["contentType"], 1))
        self.assertNotIn("images", str(history))
        self.assertNotIn(image["dataBase64"], str(result))
        self.assertNotIn(image["dataBase64"], str(history[1]))
        with closing(sqlite3.connect(self.path)) as conn:
            for table in ("requests", "chat_messages", "events", "outbox"):
                for (encrypted_body,) in conn.execute(f"SELECT body FROM {table}"):
                    self.assertNotIn(image["dataBase64"].encode(), encrypted_body)
                    self.assertNotIn(raw, encrypted_body)

    def test_image_retry_changed_original_conflicts_and_caption_sets_mixed_type(self):
        raw = b"Exif\x00\x00GPSLatitude=31.2304"
        image = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()}
        command = replace(self.command("caption", text="Here is the photograph"), metadata={"images": [image]})
        self.store.accept([command])
        changed_raw = raw + b";GPSLongitude=121.4737"
        changed = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(changed_raw).decode("ascii"), "sha256": hashlib.sha256(changed_raw).hexdigest()}
        with self.assertRaises(VersionConflict):
            self.reopen().accept([replace(command, metadata={"images": [changed]})])
        with self.assertRaises(VersionConflict):
            self.reopen().accept([replace(command, metadata=None)])
        self.assertTrue(self.reopen().accept([command])[0]["duplicate"])
        job = self.store.claim(kinds={"chat"})
        result = self.store.commit_reply(job, self.store.begin_turn(job), {"reply": "caption response"})
        self.assertEqual((result["contentType"], result["imageCount"]), ("mixed", 1))
        history = self.reopen().history("owner")
        self.assertEqual((history[0]["contentType"], history[1]["contentType"]), (result["contentType"], result["contentType"]))
        self.assertEqual(history[1]["imageCount"], 1)
        self.assertNotIn(image["dataBase64"], str(history))

    def test_image_contract_rejects_changed_hash_or_noncanonical_base64(self):
        raw = b"Exif\x00\x00GPSLatitude=31.2304"
        image = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()}
        for changed in ({**image, "sha256": "0" * 64}, {**image, "dataBase64": image["dataBase64"] + "\n"}, {**image, "mimeType": "text/plain"}):
            with self.subTest(changed=changed):
                with self.assertRaises(ValueError):
                    replace(self.command(), metadata={"images": [changed]})

    def test_image_sources_are_scoped_to_the_authenticated_owner(self):
        first = b"Exif\x00\x00GPSLatitude=31.2304"
        second = b"Exif\x00\x00GPSLatitude=40.7128"
        def image(raw):
            return {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()}
        first_receipt = self.store.accept([replace(self.command("a", owner="owner-a", text="[图片]"), metadata={"images": [image(first)]})])[0]
        second_receipt = self.store.accept([replace(self.command("b", owner="owner-b", text="[图片]"), metadata={"images": [image(second)]})])[0]
        with self.assertRaises(PermissionDenied):
            self.reopen().turn_result("owner-b", first_receipt["turn_id"])
        with self.assertRaises(PermissionDenied):
            self.reopen().turn_result("owner-a", second_receipt["turn_id"])
        jobs = [self.reopen().claim(kinds={"extract"}), self.reopen().claim(kinds={"extract"})]
        sources = [self.reopen().source_for_job(job) for job in jobs]
        self.assertEqual({source["owner"]: base64.b64decode(source["metadata"]["images"][0]["dataBase64"]) for source in sources}, {"owner-a": first, "owner-b": second})

    def test_consecutive_image_messages_split_at_turn_limit_and_retries_keep_their_turn(self):
        commands = []
        for index in range(5):
            raw = f"Exif GPS synthetic picture {index}".encode()
            image = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()}
            commands.append(replace(self.command(f"picture-{index}", text="[图片]"), channel="ws", metadata={"images": [image]}))
        with patch.dict("os.environ", {"VISION_MAX_IMAGE_COUNT": "3"}):
            receipts = [self.store.accept([command], delay=8, merge_pending=True)[0] for command in commands]
            self.assertEqual(len({receipt["turn_id"] for receipt in receipts}), 2)
            self.assertEqual({receipt["turn_id"] for receipt in receipts[:3]}, {receipts[0]["turn_id"]})
            self.assertEqual({receipt["turn_id"] for receipt in receipts[3:]}, {receipts[3]["turn_id"]})
            self.assertNotEqual(receipts[0]["turn_id"], receipts[3]["turn_id"])
            self.assertEqual(self.counts()["requests"], 5)
            reopened = self.reopen()
            for command, receipt in zip(commands, receipts):
                duplicate = reopened.accept([command], delay=8, merge_pending=True)[0]
                self.assertTrue(duplicate["duplicate"])
                self.assertEqual((duplicate["turn_id"], duplicate["message_id"]), (receipt["turn_id"], receipt["message_id"]))
                self.assertEqual(reopened.request("owner", command.request_id)["turn_id"], receipt["turn_id"])
            self.now += 8
            generated_counts = []
            for _ in range(2):
                job = reopened.claim(kinds={"chat"})
                snapshot = reopened.begin_turn(job)
                generated_counts.append(len(snapshot["sources"]))
                result = reopened.commit_reply(job, snapshot, {"reply": "synthetic grouped image reply"})
                self.assertEqual(result["imageCount"], len(snapshot["sources"]))
            self.assertEqual(sorted(generated_counts), [2, 3])
            self.assertEqual(self.counts()["chat_messages"], 7)

    def test_one_image_batch_exceeding_turn_limit_rolls_back_all_sources(self):
        commands = []
        for index in range(4):
            raw = f"synthetic picture {index}".encode()
            image = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()}
            commands.append(replace(self.command(f"batch-picture-{index}", text="[图片]"), metadata={"images": [image]}))
        with patch.dict("os.environ", {"VISION_MAX_IMAGE_COUNT": "3"}):
            with self.assertRaises(ValueError):
                self.store.accept(commands, delay=8, merge_pending=True)
        self.assertTrue(all(value == 0 for value in self.counts().values()))

    def test_accept_batch_failure_rolls_back_all_sources_and_tasks(self):
        original = self.store._enqueue

        def fail_second_source(conn, owner, kind, key, payload, **kwargs):
            if kind == "extract" and conn.execute("SELECT COUNT(*) FROM requests").fetchone()[0] == 2:
                raise OSError("synthetic disk failure")
            return original(conn, owner, kind, key, payload, **kwargs)

        with patch.object(self.store, "_enqueue", side_effect=fail_second_source):
            with self.assertRaises(OSError):
                self.store.accept([self.command("1"), self.command("2")])
        self.assertTrue(all(value == 0 for value in self.counts().values()))
        self.assertEqual(len(self.reopen().accept([self.command("1"), self.command("2")])), 2)

    def test_ws_pending_merge_waits_for_last_quiet_window_and_keeps_source_rows(self):
        first = self.store.accept([self.command("1")], delay=8, merge_pending=True)[0]
        self.now += 4
        second = self.reopen().accept([replace(self.command("2"), device_id="ws-device")], delay=8, merge_pending=True)[0]
        self.assertEqual(first["turn_id"], second["turn_id"])
        self.now += 7
        self.assertIsNone(self.store.claim(kinds={"chat"}))
        self.now += 1
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.assertEqual(snapshot["generation"], 2)
        self.assertEqual([item["request_id"] for item in snapshot["sources"]], ["request-1", "request-2"])
        self.store.commit_reply(job, snapshot, {"reply": "merged synthetic response"})
        history = self.store.history("owner")
        self.assertEqual([item["messageId"] for item in history[:2]], ["client-1", "client-2"])
        self.assertLess(history[0]["timestamp"], history[1]["timestamp"])
        self.assertLess(history[1]["timestamp"], history[2]["timestamp"])

    def test_two_workers_serialize_one_owner_but_other_owner_can_generate(self):
        self.store.accept([self.command("1")])
        self.store.accept([self.command("2")])
        barrier = threading.Barrier(2)

        def claim(_):
            store = self.reopen()
            barrier.wait(timeout=5)
            return store.claim(kinds={"chat"})

        with ThreadPoolExecutor(max_workers=2) as workers:
            claims = list(workers.map(claim, range(2)))
        self.assertEqual(sum(job is not None for job in claims), 1)
        active = next(job for job in claims if job)
        self.store.accept([self.command("3", owner="other")])
        other = self.store.claim(kinds={"chat"})
        self.assertEqual(self.store.begin_turn(other)["owner"], "other")
        snapshot = self.store.begin_turn(active)
        self.store.commit_reply(active, snapshot, {"reply": "first owner"})
        self.assertIsNotNone(self.store.claim(kinds={"chat"}))

    def test_failed_reply_transaction_rolls_back_history_completion_event_and_outbox(self):
        receipt, job, snapshot = self.begin()
        before = self.counts()
        original = self.store._change

        def crash_after_outbox(*args, **kwargs):
            original(*args, **kwargs)
            raise OSError("synthetic failure after outbox insert")

        with patch.object(self.store, "_change", side_effect=crash_after_outbox):
            with self.assertRaises(OSError):
                self.store.commit_reply(job, snapshot, {"reply": "synthetic response"})
        self.assertEqual(self.counts(), before)
        self.assertEqual(self.reopen().turn_result("owner", receipt["turn_id"])["status"], "running")
        result = self.store.commit_reply(job, snapshot, {"reply": "synthetic response"})
        self.assertEqual(self.counts()["chat_messages"], 2)
        self.assertEqual((self.counts()["events"], self.counts()["outbox"]), (1, 1))
        self.assertEqual(self.reopen().turn_result("owner", receipt["turn_id"])["result"], result)
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(job, snapshot, {"reply": "must not duplicate"})
        self.assertEqual(self.counts()["chat_messages"], 2)

    def test_expired_lease_reclaims_original_turn_and_fences_old_completion(self):
        receipt, old_job, old_snapshot = self.begin(lease_seconds=5)
        self.now += 6
        current_job = self.reopen().claim(kinds={"chat"}, lease_seconds=5)
        self.assertEqual((current_job["job_id"], current_job["attempt"]), (old_job["job_id"], 2))
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(old_job, old_snapshot, {"reply": "stale"})
        with self.assertRaises(VersionConflict):
            self.store.renew(old_job["job_id"], old_job["lease_token"])
        snapshot = self.store.begin_turn(current_job)
        self.assertEqual(snapshot["turn_id"], receipt["turn_id"])
        self.store.commit_reply(current_job, snapshot, {"reply": "current"})
        self.assertEqual(len(self.store.history("owner")), 2)

    def test_process_exit_inside_reply_transaction_recovers_without_partial_effect(self):
        receipt = self.store.accept([self.command()])[0]
        child = self.crash_reply_process("during-transaction")
        self.assertEqual(child.returncode, 77, child.stderr.decode(errors="replace"))
        recovered = self.reopen()
        self.assertEqual((self.counts()["chat_messages"], self.counts()["events"], self.counts()["outbox"]), (0, 0, 0))
        self.assertEqual(recovered.turn_result("owner", receipt["turn_id"])["job_status"], "running")
        self.now += 2
        job = recovered.claim(kinds={"chat"})
        self.assertEqual(job["attempt"], 2)
        recovered.commit_reply(job, recovered.begin_turn(job), {"reply": "recovered reply"})
        self.assertEqual((self.counts()["chat_messages"], self.counts()["events"], self.counts()["outbox"]), (2, 1, 1))

    def test_process_exit_after_commit_preserves_original_reply_and_retryable_delivery(self):
        receipt = self.store.accept([self.command()])[0]
        child = self.crash_reply_process("after-commit")
        self.assertEqual(child.returncode, 78, child.stderr.decode(errors="replace"))
        recovered = self.reopen()
        completed = recovered.turn_result("owner", receipt["turn_id"])
        self.assertEqual((completed["status"], completed["job_status"]), ("completed", "succeeded"))
        self.assertIsNone(recovered.claim(kinds={"chat"}))
        first = recovered.claim(kinds={"deliver"})
        stable_delivery = recovered.delivery(first)
        self.assertEqual(stable_delivery["result"]["messageId"], completed["result"]["messageId"])
        self.assertEqual(recovered.finish(first["job_id"], first["lease_token"], succeeded=False, retry_delay=2), "retry_wait")
        self.assertIsNone(recovered.claim(kinds={"deliver"}))
        self.now += 3
        retry = self.reopen().claim(kinds={"deliver"})
        self.assertEqual((retry["job_id"], retry["attempt"]), (first["job_id"], 2))
        self.assertEqual(recovered.delivery(retry), stable_delivery)
        recovered.finish(retry["job_id"], retry["lease_token"], succeeded=True)
        self.assertEqual((self.counts()["chat_messages"], self.counts()["events"], self.counts()["outbox"]), (2, 1, 1))

    def test_renewed_lease_prevents_competing_claim_until_new_expiry(self):
        _, job, _ = self.begin(lease_seconds=5)
        self.now += 4
        self.store.renew(job["job_id"], job["lease_token"], lease_seconds=5)
        self.now += 2
        self.assertIsNone(self.reopen().claim(kinds={"chat"}))
        self.now += 4
        self.assertEqual(self.store.claim(kinds={"chat"})["attempt"], 2)

    def test_snapshot_generation_change_rejects_without_partial_history(self):
        receipt, job, snapshot = self.begin()
        with closing(sqlite3.connect(self.path)) as conn:
            conn.execute("UPDATE chat_turns SET generation=generation+1 WHERE id=?", (receipt["turn_id"],))
            conn.commit()
        before = self.counts()
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(job, snapshot, {"reply": "stale generation"})
        self.assertEqual(self.counts(), before)

    def test_deadline_rejects_reply_even_with_a_renewed_live_lease(self):
        _, job, snapshot = self.begin(lease_seconds=300)
        self.now = snapshot["deadline"] + 1
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(job, snapshot, {"reply": "too late"})
        self.assertEqual(self.counts()["chat_messages"], 0)

    def test_permission_change_during_generation_rejects_reply_and_hides_old_context(self):
        self.store.put_fact("owner", "fact-1", Domain.USER_FACT, {"text": "synthetic", "enabled": True}, expected_revision=0, targets=())
        _, job, snapshot = self.begin()
        self.store.set_permission("owner", "fact-1", enabled=False, expected_revision=1, targets=())
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(job, snapshot, {"reply": "stale permission"})
        self.assertEqual(self.store.history("owner", for_model=True), [])
        self.assertEqual(self.counts()["chat_messages"], 0)

    def test_extraction_crash_rolls_back_all_facts_and_replay_cannot_repeat_effect(self):
        self.store.accept([self.command()])
        job = self.store.claim(kinds={"extract"})
        source = self.store.source_for_job(job)
        self.assertEqual(source["owner"], "owner")
        before = self.counts()
        original = self.store._change

        def fail_after_first_effect(*args, **kwargs):
            original(*args, **kwargs)
            raise OSError("synthetic crash after fact/event/outbox")

        with patch.object(self.store, "_change", side_effect=fail_after_first_effect):
            with self.assertRaises(OSError):
                self.store.commit_extraction(job, ["synthetic fact one", "synthetic fact two"])
        self.assertEqual(self.counts(), before)
        identities = self.reopen().commit_extraction(job, ["synthetic fact one", "synthetic fact two"])
        self.assertEqual(len(set(identities)), 2)
        self.assertEqual((self.counts()["facts"], self.counts()["events"], self.counts()["outbox"]), (2, 2, 2))
        with self.assertRaises(VersionConflict):
            self.store.commit_extraction(job, ["must not repeat"])
        self.assertEqual(self.counts()["facts"], 2)

    def test_chat_lease_cannot_be_acknowledged_by_empty_extraction(self):
        receipt, job, snapshot = self.begin()
        with self.assertRaises((PermissionDenied, VersionConflict)):
            self.store.commit_extraction(job, [])
        self.assertEqual(self.store.turn_result("owner", receipt["turn_id"])["job_status"], "running")
        self.store.commit_reply(job, snapshot, {"reply": "still valid chat"})

    def test_extraction_permission_change_cancels_atomically_without_new_fact(self):
        self.store.put_fact("owner", "fact-1", Domain.USER_FACT, {"text": "synthetic", "enabled": True}, expected_revision=0, targets=())
        self.store.accept([self.command()])
        job = self.store.claim(kinds={"extract"})
        self.assertTrue(self.store.extraction_context_allowed(job))
        self.store.set_permission("owner", "fact-1", enabled=False, expected_revision=1, targets=())
        before = self.counts()
        self.assertEqual(self.store.commit_extraction(job, ["must not be published"]), [])
        self.assertEqual(self.counts(), before)
        status = next(row[1] for row in self.jobs() if row[0] == job["job_id"])
        self.assertEqual(status, "cancelled")

    def test_delivery_retry_and_reopen_keep_reply_message_event_and_delivery_ids(self):
        receipt, result = self.complete()
        first = self.store.claim(kinds={"deliver"}, lease_seconds=1)
        initial_delivery = self.store.delivery(first)
        self.now += 2
        second = self.reopen().claim(kinds={"deliver"})
        self.assertEqual(first["job_id"], second["job_id"])
        self.assertEqual(first["payload"], second["payload"])
        self.assertEqual(self.store.delivery(second), initial_delivery)
        self.assertEqual(initial_delivery["result"]["messageId"], result["messageId"])
        with self.assertRaises(VersionConflict):
            self.store.finish(first["job_id"], first["lease_token"], succeeded=True)
        self.store.finish(second["job_id"], second["lease_token"], succeeded=True)
        self.assertEqual(self.store.turn_result("owner", receipt["turn_id"])["result"], result)
        self.assertIsNone(self.store.claim(kinds={"deliver"}))

    def test_history_owner_and_model_permission_are_isolated(self):
        own_turn, _ = self.complete("1", owner="owner")
        self.complete("2", owner="other")
        self.store.put_fact("owner", "disabled", Domain.USER_FACT, {"text": "synthetic blocked", "enabled": False}, expected_revision=0, targets=())
        self.assertEqual(self.store.history("owner", for_model=True), [])
        self.assertEqual(len(self.store.history("owner")), 2)
        self.assertEqual(self.store.history("other", for_model=True), [])
        self.assertEqual(len(self.store.history("other")), 2)
        self.assertTrue(all(item["userId"] == "other" for item in self.store.history("other")))
        with self.assertRaises(PermissionDenied):
            self.store.turn_result("other", own_turn["turn_id"])

    def test_running_lease_does_not_authorize_another_task_or_turn_payload(self):
        first = self.store.accept([self.command("1")])[0]
        second = self.store.accept([self.command("2")])[0]
        job = self.store.claim(kinds={"chat"})
        other_turn = second["turn_id"] if job["payload"]["turn_id"] == first["turn_id"] else first["turn_id"]
        forged = deepcopy(job)
        forged["payload"]["turn_id"] = other_turn
        with self.assertRaises((PermissionDenied, VersionConflict)):
            self.store.begin_turn(forged)
        extract = self.store.claim(kinds={"extract"})
        forged = deepcopy(extract)
        forged["payload"]["turn_id"] = first["turn_id"]
        with self.assertRaises((PermissionDenied, VersionConflict)):
            self.store.begin_turn(forged)

    def test_delivery_lease_cannot_read_another_owner_event(self):
        self.complete("1", owner="owner")
        self.complete("2", owner="other")
        delivery_job = self.store.claim(kinds={"deliver"})
        actual = self.store.delivery(delivery_job)
        other_owner_key = self.cipher.index("owner", "other" if actual["owner"] == "owner" else "owner")
        with closing(sqlite3.connect(self.path)) as conn:
            other_event = conn.execute("SELECT id FROM events WHERE owner=?", (other_owner_key,)).fetchone()[0]
        forged = deepcopy(delivery_job)
        forged["payload"]["event_id"] = other_event
        with self.assertRaises((PermissionDenied, VersionConflict)):
            self.store.delivery(forged)

    def test_restored_permissions_pause_jobs_and_block_sequence_chasing_writes(self):
        self.store.put_fact("owner", "fact-1", Domain.USER_FACT, {"text": "synthetic", "enabled": True}, expected_revision=0, targets=())
        self.store.accept([self.command()])
        running = self.store.claim(kinds={"extract"})
        backup = str(pathlib.Path(self.temp.name) / "backup.db")
        self.store.backup(backup)
        self.store.set_permission("owner", "fact-1", enabled=False, expected_revision=1, targets=())
        restored = self.reopen(backup)
        before_jobs, before_counts = self.jobs(restored), self.counts(restored)
        self.assertIsNone(restored.claim())
        operations = (
            lambda: restored.finish(running["job_id"], running["lease_token"], succeeded=True),
            lambda: restored.renew(running["job_id"], running["lease_token"]),
            lambda: restored.commit_extraction(running, ["must stay blocked"]),
            lambda: restored.accept([self.command("new")]),
            lambda: restored.receive(self.command("new-foundation")),
            lambda: restored.enqueue("owner", "derive", "new", {}),
            lambda: restored.put_fact("owner", "unrelated", Domain.USER_FACT, {"text": "unrelated", "enabled": True}, expected_revision=0),
            lambda: restored.set_permission("owner", "fact-1", enabled=False, expected_revision=1),
        )
        for operation in operations:
            with self.subTest(operation=operation):
                with self.assertRaises(PermissionDenied):
                    operation()
        self.assertEqual(self.jobs(restored), before_jobs)
        self.assertEqual(self.counts(restored), before_counts)
        self.assertEqual(restored.change_head(), 1)
        with self.assertRaises(PermissionDenied):
            restored.verify_restored_permissions(self.store.change_head())
        self.assertIsNone(self.reopen(backup).claim())
        # With a trusted matching watermark, the unchanged original jobs resume.
        restored.verify_restored_permissions(1)
        self.assertEqual(restored.source_for_job(running)["text"], "synthetic input 1")
        self.assertIsNotNone(restored.claim(kinds={"chat"}))

    def test_fact_cursor_pages_over_500_same_time_events_without_reviving_disabled_fact(self):
        self.complete("page-preface")  # Real reply events must not consume fact-page slots.
        expected_sequences = []
        for index in range(521):
            self.store.put_fact("owner", f"page-{index:04}", Domain.USER_FACT, {"text": f"synthetic page fact {index}", "enabled": True}, expected_revision=0, targets=())
            expected_sequences.append(self.store.change_head())
            if index in (199, 399):
                self.complete(f"page-reply-{index}")
            if index % 79 == 0:
                self.store.put_fact("other", f"other-{index}", Domain.USER_FACT, {"text": "synthetic other owner", "enabled": True}, expected_revision=0, targets=())
        self.store.put_fact("owner", "page-0000", Domain.USER_FACT, {"text": "synthetic corrected fact", "enabled": True}, expected_revision=1, targets=())
        expected_sequences.append(self.store.change_head())
        self.store.set_permission("owner", "page-0000", enabled=False, expected_revision=2, targets=())
        expected_sequences.append(self.store.change_head())
        owner_head = expected_sequences[-1]
        self.store.put_fact("other", "other-final", Domain.USER_FACT, {"text": "synthetic final other fact", "enabled": True}, expected_revision=0, targets=())

        cursor, collected, page_sizes = 0, [], []
        for _ in range(5):
            page = self.reopen().fact_page("owner", cursor=cursor, since=1_000_000, limit=200)
            self.assertGreater(page["nextCursor"], cursor)
            cursor = page["nextCursor"]
            collected.extend(page["items"])
            page_sizes.append(len(page["items"]))
            if not page["hasMore"]:
                break
        else:
            self.fail("fact cursor did not terminate")
        self.assertEqual(page_sizes, [200, 200, 123])
        self.assertEqual([item["sequence"] for item in collected], expected_sequences)
        self.assertEqual(len({item["eventId"] for item in collected}), 523)
        self.assertEqual({item["timestamp"] for item in collected}, {1_000_000})
        self.assertTrue(all(item["userId"] == "owner" and item["action"] != "reply_completed" for item in collected))
        self.assertEqual(cursor, owner_head)
        disabled_replays = [item for item in collected if item["factId"] == "page-0000"]
        self.assertEqual([item["eventRevision"] for item in disabled_replays], [1, 2, 3])
        self.assertEqual([item["revision"] for item in disabled_replays], [1, 2, 3])
        self.assertEqual([item["action"] for item in disabled_replays], ["created", "updated", "disabled"])
        self.assertEqual([item["permissionVersion"] for item in disabled_replays], [2, 2, 2])
        self.assertTrue(all(not item["enabled"] and item["fact"] == "" for item in disabled_replays))
        self.assertEqual(collected[-1]["action"], "disabled")
        self.assertEqual(self.store.fact_page("owner", cursor=cursor), {"items": [], "nextCursor": cursor, "hasMore": False})
        self.assertEqual(len(self.store.fact_page("owner", limit=1000)["items"]), 500)
        filtered = self.store.fact_page("owner", since=1_000_001)
        self.assertEqual(filtered, {"items": [], "nextCursor": owner_head, "hasMore": False})
        self.assertEqual({item["userId"] for item in self.store.fact_page("other")["items"]}, {"other"})

    def test_pending_text_merges_one_gift_but_two_gifts_keep_distinct_paid_turns(self):
        def gift(identity):
            return replace(self.command(identity), channel="ws", intent="interaction",
                           metadata={"item": {"id": identity}, "attachment": "", "merge_after_payment": True})

        text = self.store.accept([self.command("text-before")], delay=8, merge_pending=True)[0]
        self.now += 1
        first = self.store.accept([gift("gift-1")], delay=20, merge_pending=True)[0]
        second = self.store.accept([gift("gift-2")], delay=20, merge_pending=True)[0]
        self.assertNotEqual(first["turn_id"], text["turn_id"])
        self.assertNotEqual(first["turn_id"], second["turn_id"])
        later_text = self.store.accept([self.command("text-after")], delay=8, merge_pending=True)[0]
        self.assertEqual(later_text["turn_id"], text["turn_id"])
        first_turn = self.store.confirm_interaction("owner", "request-gift-1")
        second_turn = self.store.confirm_interaction("owner", "request-gift-2")
        self.assertEqual(first_turn, text["turn_id"])
        self.assertNotEqual(first_turn, second_turn)
        self.assertEqual(self.store.request("owner", "request-gift-1")["turn_id"], first_turn)
        self.store.ready("owner", [first_turn, second_turn])
        snapshots = {}
        for _ in range(2):
            job = self.store.claim(kinds={"chat"})
            snapshot = self.store.begin_turn(job)
            snapshots[snapshot["turn_id"]] = snapshot
            self.assertEqual(sum(source["intent"] == "interaction" for source in snapshot["sources"]), 1)
            reply = self.store.commit_reply(job, snapshot, {"reply": "synthetic gift response"})
            self.store.mark_dispatched("owner", reply)
        self.assertEqual({source["request_id"] for source in snapshots[first_turn]["sources"]},
                         {"request-text-before", "request-text-after", "request-gift-1"})
        self.assertEqual({source["request_id"] for source in snapshots[second_turn]["sources"]}, {"request-gift-2"})

    def test_two_gifts_in_one_incoming_batch_are_rejected_atomically(self):
        commands = [replace(self.command(identity), intent="interaction", metadata={"item": {"id": identity}}) for identity in ("gift-1", "gift-2")]
        with self.assertRaises(ValueError):
            self.store.accept(commands, merge_pending=True)
        self.assertTrue(all(count == 0 for count in self.counts().values()))

    def test_unpaid_intent_does_not_block_text_and_paid_gift_cannot_steal_running_text(self):
        gift = replace(self.command("unpaid"), intent="interaction", metadata={"merge_after_payment": True})
        unpaid = self.store.accept([gift], delay=20, merge_pending=True)[0]
        text = self.store.accept([self.command("free-text")], merge_pending=True)[0]
        self.assertNotEqual(unpaid["turn_id"], text["turn_id"])
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.assertEqual(snapshot["turn_id"], text["turn_id"])
        paid_turn = self.store.confirm_interaction("owner", "request-unpaid")
        self.assertEqual(paid_turn, unpaid["turn_id"])
        result = self.store.commit_reply(job, snapshot, {"reply": "文字正常回复"})
        self.assertEqual(result["requestIds"], ["request-free-text"])

    def test_pre_upgrade_unpaid_mixed_intent_is_split_before_a_failed_charge(self):
        text = self.store.accept([self.command("legacy-text")], delay=8, merge_pending=True)[0]
        gift = self.store.accept([replace(self.command("legacy-gift"), intent="interaction")], delay=20)[0]
        with self.store._transaction() as conn:
            text_row, text_body = self.store._turn(conn, text["turn_id"])
            gift_row, gift_body = self.store._turn(conn, gift["turn_id"])
            source_id = gift_body["sources"][0]
            text_body["sources"].append(source_id)
            text_body["payments"] = gift_body["payments"]
            self.store._save_turn(conn, text_row, text_body)
            conn.execute("UPDATE requests SET turn_id=? WHERE id=?", (text["turn_id"], source_id))
            self.store._move_interaction_jobs(conn, text_row["owner"], source_id, text["turn_id"])
            gift_body["sources"], gift_body["payments"] = [], {}
            self.store._save_turn(conn, gift_row, gift_body, status="cancelled")
            conn.execute("UPDATE jobs SET status='cancelled' WHERE id=?", (gift_body["job_id"],))
        def rejected():
            raise ValueError("合成扣款拒绝")
        with self.assertRaises(ValueError):
            self.store.charge_interaction("owner", "request-legacy-gift", rejected)
        isolated = self.store.request("owner", "request-legacy-gift")["turn_id"]
        self.assertNotEqual(isolated, text["turn_id"])
        self.store.cancel("owner", isolated)
        self.store.ready("owner", [text["turn_id"]])
        job = self.store.claim(kinds={"chat"})
        result = self.store.commit_reply(job, self.store.begin_turn(job), {"reply": "原文保留"})
        self.assertEqual(result["requestIds"], ["request-legacy-text"])


if __name__ == "__main__":
    unittest.main()
