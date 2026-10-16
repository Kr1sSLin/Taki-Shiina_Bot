"""F-A02 through F-A07: isolated SQL prototype and recovery fault replay."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from dataclasses import replace
import pathlib
import sqlite3
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from secure_storage import DataDecryptError
from services.contracts import ChatCommand, Domain, FactRef
from services.domain_cipher import DomainCipher
from services.foundation_store import FoundationStore, PermissionDenied, StorageBusy, VersionConflict


class FoundationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = str(pathlib.Path(self.temp.name) / "foundation.db")
        self.cipher = DomainCipher(b"s" * 32)
        self.now = 1000.0
        self.store = self.reopen()

    def reopen(self):
        return FoundationStore(self.path, self.cipher, busy_timeout=0.1, clock=lambda: self.now)

    def counts(self):
        with closing(sqlite3.connect(self.path)) as conn:
            return {table: conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0] for table in ("requests", "facts", "events", "jobs", "outbox")}

    def add_fact(self, fact_id="fact-1", text="synthetic-private-keyword-青椒", enabled=True):
        return self.store.put_fact("owner", fact_id, Domain.USER_FACT, {"text": text, "enabled": enabled, "source": "message-1", "score": 0.3}, expected_revision=0)

    def test_cross_protocol_retries_share_source_turn_and_job(self):
        command = ChatCommand("owner", "request-1", "client-1", "http-device", "synthetic input")
        first = self.store.receive(command)
        second = self.reopen().receive(replace(command, device_id="ws-device"))
        self.assertTrue(second["duplicate"])
        self.assertEqual(second["message_id"], first["message_id"])
        self.assertEqual(second["turn_id"], first["turn_id"])
        self.assertEqual(self.counts()["requests"], 1)
        self.assertEqual(self.counts()["jobs"], 1)
        with self.assertRaises(VersionConflict):
            self.store.receive(replace(command, text="different input"))
        with self.assertRaises(VersionConflict):
            self.store.receive(replace(command, request_id="different-request"))
        with self.assertRaises(VersionConflict):
            self.store.receive(replace(command, client_message_id="different-client"))

    def test_fact_event_delivery_failure_rolls_back_whole_transaction(self):
        with patch.object(self.store, "_enqueue", side_effect=RuntimeError("synthetic crash after event")):
            with self.assertRaises(RuntimeError):
                self.add_fact()
        self.assertEqual(self.counts(), {"requests": 0, "facts": 0, "events": 0, "jobs": 0, "outbox": 0})
        self.add_fact()
        self.assertEqual((self.counts()["facts"], self.counts()["events"], self.counts()["outbox"]), (1, 1, 1))

    def test_extraction_effect_and_completion_commit_together(self):
        self.store.receive(ChatCommand("owner", "request", "client", "device", "synthetic input"))
        claimed = self.store.claim()
        data = {"text": "synthetic fact", "enabled": True, "source": claimed["payload"]["message_id"]}
        with patch.object(self.store, "_change", side_effect=RuntimeError("crash before event")):
            with self.assertRaises(RuntimeError):
                self.store.commit_extracted_fact(claimed["job_id"], claimed["lease_token"], "fact-1", Domain.USER_FACT, data)
        self.assertEqual(self.counts()["facts"], 0)
        self.store.commit_extracted_fact(claimed["job_id"], claimed["lease_token"], "fact-1", Domain.USER_FACT, data)
        with self.assertRaises(VersionConflict):
            self.reopen().commit_extracted_fact(claimed["job_id"], claimed["lease_token"], "fact-2", Domain.USER_FACT, data)
        self.assertEqual(self.counts()["facts"], 1)

    def test_key_and_row_binding_cannot_be_swapped(self):
        self.add_fact()
        with self.assertRaises(DataDecryptError):
            FoundationStore(self.path, DomainCipher(b"t" * 32))
        self.assertEqual(self.store.read_fact("owner", "fact-1")["revision"], 1)
        encrypted = self.cipher.encrypt(Domain.USER_FACT, "owner", "fact-1", {"text": "private"})
        for domain, owner, identity in ((Domain.CHARACTER_DAILY, "owner", "fact-1"), (Domain.USER_FACT, "other", "fact-1"), (Domain.USER_FACT, "owner", "fact-2")):
            with self.assertRaises(DataDecryptError):
                self.cipher.decrypt(domain, owner, identity, encrypted)

    def test_disable_revision_jobs_and_owner_checks(self):
        self.add_fact()
        # Clear the independent sync delivery before testing a dependent task.
        delivery = self.store.claim()
        self.store.finish(delivery["job_id"], delivery["lease_token"], succeeded=True)
        job = self.store.enqueue("owner", "summary", "summary-1", {"text": "secret"}, dependencies=(FactRef("fact-1", 1),))
        claimed = self.store.claim()
        self.assertEqual(claimed["job_id"], job)
        self.store.set_permission("owner", "fact-1", enabled=False, expected_revision=1)
        self.assertEqual(self.store.finish(job, claimed["lease_token"], succeeded=True), "cancelled")
        with self.assertRaises(PermissionDenied):
            self.store.read_fact("owner", "fact-1", for_model=True)
        with self.assertRaises(PermissionDenied):
            self.store.read_fact("other", "fact-1")
        with self.assertRaises(VersionConflict):
            self.store.put_fact("owner", "fact-1", Domain.USER_FACT, {"text": "correction", "enabled": True}, expected_revision=2)
        self.store.put_fact("owner", "fact-1", Domain.USER_FACT, {"text": "correction", "enabled": False}, expected_revision=2)
        self.assertFalse(self.store.read_fact("owner", "fact-1")["data"]["enabled"])

    def test_two_workers_and_crash_lease_fencing(self):
        job = self.store.enqueue("owner", "extract", "source-1", {"text": "private"})
        with ThreadPoolExecutor(max_workers=2) as workers:
            results = list(workers.map(lambda _: self.reopen().claim(lease_seconds=5), range(2)))
        claims = [claim for claim in results if claim]
        self.assertEqual(len(claims), 1)
        first = claims[0]
        self.now += 6
        second = self.reopen().claim(lease_seconds=5)
        self.assertEqual(second["job_id"], job)
        self.assertEqual(second["attempt"], 2)
        with self.assertRaises(VersionConflict):
            self.store.finish(job, first["lease_token"], succeeded=True)
        self.assertEqual(self.store.finish(job, second["lease_token"], succeeded=True), "succeeded")
        self.assertIsNone(self.reopen().claim())

    def test_retry_budget_and_final_crashed_attempt(self):
        job = self.store.enqueue("owner", "extract", "source-1", {}, max_attempts=2)
        first = self.store.claim()
        self.assertEqual(self.store.finish(job, first["lease_token"], succeeded=False, retry_delay=2), "retry_wait")
        self.assertIsNone(self.store.claim())
        self.now += 3
        second = self.store.claim(lease_seconds=1)
        self.assertEqual(second["attempt"], 2)
        self.now += 2
        self.assertIsNone(self.reopen().claim())
        with closing(sqlite3.connect(self.path)) as conn:
            self.assertEqual(conn.execute("SELECT status FROM jobs WHERE id=?", (job,)).fetchone()[0], "failed")

    def test_delivery_retries_keep_original_event_and_delivery_ids(self):
        self.add_fact()
        first = self.store.claim(lease_seconds=1)
        self.now += 2
        second = self.reopen().claim()
        self.assertEqual(second["job_id"], first["job_id"])
        self.assertEqual(second["payload"], first["payload"])
        self.store.finish(second["job_id"], second["lease_token"], succeeded=True)
        self.assertEqual(self.counts()["outbox"], 1)

    def test_lock_wait_is_bounded_and_model_phase_holds_no_transaction(self):
        job = self.store.enqueue("owner", "model", "1", {})
        claim = self.store.claim()
        self.add_fact()  # Another write while a worker has an unfinished model job.
        conn = sqlite3.connect(self.path, isolation_level=None)
        self.addCleanup(conn.close)
        conn.execute("BEGIN IMMEDIATE")
        started = time.monotonic()
        with self.assertRaises(StorageBusy):
            self.store.enqueue("owner", "model", "2", {})
        self.assertLess(time.monotonic() - started, 1)
        conn.rollback()
        self.assertEqual(self.store.finish(job, claim["lease_token"], succeeded=True), "succeeded")

    def test_backup_new_environment_stays_closed_when_newer_disable_is_missing(self):
        self.add_fact()
        backup = str(pathlib.Path(self.temp.name) / "backup.db")
        self.store.backup(backup)
        self.store.set_permission("owner", "fact-1", enabled=False, expected_revision=1)
        restored = FoundationStore(backup, self.cipher)
        self.assertEqual(restored.read_fact("owner", "fact-1")["data"]["enabled"], True)
        with self.assertRaises(PermissionDenied):
            restored.read_fact("owner", "fact-1", for_model=True)
        with self.assertRaises(PermissionDenied):
            restored.verify_restored_permissions(2)
        with self.assertRaises(PermissionDenied):
            FoundationStore(backup, self.cipher).search("owner", Domain.USER_FACT, "private")
        self.assertIsNone(restored.claim())

    def test_failed_backup_never_publishes_usable_copy(self):
        self.add_fact()
        destination = pathlib.Path(self.temp.name) / "failed-backup.db"
        with patch.object(self.store, "_set_meta", side_effect=OSError("synthetic disk error")):
            with self.assertRaises(OSError):
                self.store.backup(str(destination))
        self.assertFalse(destination.exists())
        self.assertEqual(list(pathlib.Path(self.temp.name).glob("*.partial")), [])
        with self.assertRaises(ValueError):
            self.store.search("owner", Domain.USER_FACT, "query", limit=0)

    def test_bounded_memory_search_and_no_plaintext_in_database_backup_or_jobs(self):
        marker = "synthetic-private-keyword-青椒"
        self.add_fact(text=marker + " 31.234567,121.765432")
        self.store.enqueue("owner", "derive", "key", {"text": marker, "lat": 31.234567, "lon": 121.765432})
        self.assertEqual(len(self.store.search("owner", Domain.USER_FACT, "青椒", limit=10)), 1)
        with self.assertRaises(ValueError):
            self.store.search("owner", Domain.USER_FACT, "青椒", limit=1001)
        self.assertNotIn("31.234567", str(self.store.read_fact("owner", "fact-1")))
        backup = str(pathlib.Path(self.temp.name) / "backup.db")
        self.store.backup(backup)
        for file in pathlib.Path(self.temp.name).iterdir():
            raw = file.read_bytes()
            for forbidden in (marker.encode(), b"31.234567", b"121.765432"):
                self.assertNotIn(forbidden, raw)
