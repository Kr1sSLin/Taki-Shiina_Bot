"""Stage A SQL repository. Runtime assembly and data import are separate concerns.

Every method owns a short connection/transaction. Call models and external delivery
between claim and complete, never inside a transaction. Payloads are authenticated
and encrypted; SQL indexes contain opaque identities and bounded worker metadata.
"""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import asdict
import os
import sqlite3
import tempfile
import time

from secure_storage import DataCorruptionError
from services.contracts import ChatCommand, Domain, FactRef, new_id
from services.domain_cipher import DomainCipher
from services.privacy import scrub_locations


class VersionConflict(RuntimeError):
    pass


class PermissionDenied(RuntimeError):
    pass


class StorageBusy(RuntimeError):
    pass


_SCHEMA = (
    "CREATE TABLE meta (id TEXT PRIMARY KEY, body BLOB NOT NULL)",
    "CREATE TABLE facts (id TEXT PRIMARY KEY, owner TEXT NOT NULL, domain TEXT NOT NULL, revision INTEGER NOT NULL, body BLOB NOT NULL)",
    "CREATE INDEX facts_owner ON facts(owner,domain,id)",
    "CREATE TABLE requests (owner TEXT NOT NULL, request_key TEXT NOT NULL, client_key TEXT NOT NULL, id TEXT NOT NULL UNIQUE, turn_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(owner,request_key), UNIQUE(owner,client_key))",
    "CREATE TABLE jobs (id TEXT PRIMARY KEY, owner TEXT NOT NULL, dedupe TEXT NOT NULL UNIQUE, status TEXT NOT NULL, attempts INTEGER NOT NULL, max_attempts INTEGER NOT NULL, available REAL NOT NULL, lease_until REAL, lease_token TEXT, body BLOB NOT NULL)",
    "CREATE INDEX jobs_due ON jobs(status,available,lease_until)",
    "CREATE TABLE events (seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, owner TEXT NOT NULL, entity_id TEXT NOT NULL, body BLOB NOT NULL)",
    "CREATE TABLE outbox (id TEXT PRIMARY KEY, event_id TEXT NOT NULL, job_id TEXT NOT NULL UNIQUE, body BLOB NOT NULL)",
)


class FoundationStore:
    def __init__(self, path: str, cipher: DomainCipher, *, busy_timeout: float = 0.5, clock=time.time):
        if not 0 < busy_timeout <= 5:
            raise ValueError("SQL lock wait must be within (0, 5] seconds")
        self.path = os.path.abspath(path)
        if self.path.endswith(".partial"):
            raise DataCorruptionError("incomplete backup cannot be opened as a database")
        if os.path.lexists(self.path) and os.path.isfile(self.path) and os.path.getsize(self.path) == 0:
            raise DataCorruptionError("existing empty database cannot reset permission state")
        self.cipher = cipher
        self.busy_timeout = busy_timeout
        self.clock = clock
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with self._transaction() as conn:
            tables = conn.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall()
            if not tables:
                for statement in _SCHEMA:
                    conn.execute(statement)
                self._set_meta(conn, {"schema": 1, "permissions_ready": True})
            self._meta(conn)  # Authenticate before accepting any writes on reopen.

    def _connect(self):
        conn = sqlite3.connect(self.path, timeout=self.busy_timeout, isolation_level=None)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys=ON")
        conn.execute("PRAGMA synchronous=FULL")
        return conn

    @contextmanager
    def _transaction(self, *, write=True):
        conn = self._connect()
        try:
            conn.execute("BEGIN IMMEDIATE" if write else "BEGIN")
            yield conn
            conn.commit()
        except sqlite3.OperationalError as exc:
            conn.rollback()
            if "locked" in str(exc).lower() or "busy" in str(exc).lower():
                raise StorageBusy("SQL lock wait exceeded") from exc
            raise
        except BaseException:
            conn.rollback()
            raise
        finally:
            conn.close()

    def _meta(self, conn):
        try:
            row = conn.execute("SELECT body FROM meta WHERE id='format'").fetchone()
        except sqlite3.DatabaseError as exc:
            raise DataCorruptionError("invalid foundation database") from exc
        if row is None:
            raise DataCorruptionError("database encryption marker missing")
        value = self.cipher.decrypt(Domain.DERIVED, "system", "format", row["body"])
        if value.get("schema") != 1:
            raise DataCorruptionError("unsupported SQL schema")
        return value

    def _set_meta(self, conn, data):
        body = self.cipher.encrypt(Domain.DERIVED, "system", "format", data)
        conn.execute("INSERT OR REPLACE INTO meta VALUES ('format',?)", (body,))

    def _require_ready(self, conn):
        if not self._meta(conn).get("permissions_ready", False):
            raise PermissionDenied("restored permissions must be verified before business writes")

    def receive(self, command: ChatCommand) -> dict:
        owner = self.cipher.index("owner", command.owner)
        request_key = self.cipher.index("request", command.owner, command.request_id)
        client_key = self.cipher.index("client", command.owner, command.client_message_id)
        with self._transaction() as conn:
            self._require_ready(conn)
            row = conn.execute("SELECT * FROM requests WHERE owner=? AND (request_key=? OR client_key=?)", (owner, request_key, client_key)).fetchone()
            if row:
                previous = self.cipher.decrypt(Domain.SOURCE_MESSAGE, owner, row["id"], row["body"])
                if previous["text"] != scrub_locations(command.text) or previous["client_message_id"] != command.client_message_id or previous["request_id"] != command.request_id:
                    raise VersionConflict("request identity reused for different input")
                dedupe = self.cipher.index("job", owner, "extract", row["id"])
                job = conn.execute("SELECT id FROM jobs WHERE dedupe=?", (dedupe,)).fetchone()
                return {"message_id": row["id"], "turn_id": row["turn_id"], "job_id": job["id"] if job else None, "duplicate": True}
            message_id, turn_id = new_id(), new_id()
            body = self.cipher.encrypt(Domain.SOURCE_MESSAGE, owner, message_id, asdict(command))
            conn.execute("INSERT INTO requests VALUES (?,?,?,?,?,?)", (owner, request_key, client_key, message_id, turn_id, body))
            job_id = self._enqueue(conn, owner, "extract", message_id, {"kind": "extract", "message_id": message_id, "turn_id": turn_id, "dependencies": []})
            return {"message_id": message_id, "turn_id": turn_id, "job_id": job_id, "duplicate": False}

    def _enqueue(self, conn, owner, kind, key, payload, *, max_attempts=3):
        dedupe = self.cipher.index("job", owner, kind, key)
        row = conn.execute("SELECT id FROM jobs WHERE dedupe=?", (dedupe,)).fetchone()
        if row:
            return row["id"]
        job_id = new_id()
        body = self.cipher.encrypt(Domain.JOB, owner, job_id, payload)
        conn.execute("INSERT INTO jobs VALUES (?,?,?,'pending',0,?,?,NULL,NULL,?)", (job_id, owner, dedupe, max_attempts, self.clock(), body))
        return job_id

    def enqueue(self, owner: str, kind: str, key: str, payload: dict, *, dependencies: tuple[FactRef, ...] = (), max_attempts=3):
        if not 1 <= max_attempts <= 10:
            raise ValueError("retry budget must be between 1 and 10")
        owner_key = self.cipher.index("owner", owner)
        value = {"kind": kind, "input": payload, "dependencies": [asdict(ref) for ref in dependencies]}
        with self._transaction() as conn:
            self._require_ready(conn)
            return self._enqueue(conn, owner_key, kind, key, value, max_attempts=max_attempts)

    def _fact(self, conn, owner, fact_id):
        row = conn.execute("SELECT * FROM facts WHERE id=? AND owner=?", (fact_id, owner)).fetchone()
        if row is None:
            raise PermissionDenied("fact unavailable")
        data = self.cipher.decrypt(Domain(row["domain"]), owner, f"{fact_id}:{row['revision']}", row["body"])
        return row, data

    def _eligible(self, conn, owner, dependencies):
        if not self._meta(conn)["permissions_ready"]:
            return False
        for dependency in dependencies:
            try:
                row, data = self._fact(conn, owner, dependency["fact_id"])
            except PermissionDenied:
                return False
            if row["revision"] != dependency["revision"] or not data.get("enabled", False):
                return False
        return True

    def put_fact(self, owner: str, fact_id: str, domain: Domain, data: dict, *, expected_revision: int, targets=("sync",)):
        if domain not in (Domain.USER_FACT, Domain.CHARACTER_DAILY, Domain.OFFICIAL_LORE):
            raise ValueError("invalid fact domain")
        if not isinstance(data.get("enabled"), bool):
            raise ValueError("fact permission is required")
        owner_key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            old = conn.execute("SELECT * FROM facts WHERE id=?", (fact_id,)).fetchone()
            if old and (old["owner"] != owner_key or old["domain"] != domain.value):
                raise PermissionDenied("fact identity belongs to another owner or domain")
            if (old["revision"] if old else 0) != expected_revision:
                raise VersionConflict("fact revision changed")
            revision = expected_revision + 1
            # A content correction never silently restores a disabled fact.
            if old:
                _, old_data = self._fact(conn, owner_key, fact_id)
                if old_data["enabled"] != data["enabled"]:
                    raise VersionConflict("use set_permission for explicit disable/restore")
            self._write_fact(conn, owner_key, fact_id, domain, revision, data)
            self._change(conn, owner_key, fact_id, revision, "created" if not old else "updated", targets)
            return revision

    def _write_fact(self, conn, owner, fact_id, domain, revision, data):
        body = self.cipher.encrypt(domain, owner, f"{fact_id}:{revision}", data)
        conn.execute("INSERT OR REPLACE INTO facts VALUES (?,?,?,?,?)", (fact_id, owner, domain.value, revision, body))

    def _change(self, conn, owner, entity_id, revision, action, targets):
        event_id = new_id()
        event = {"event_id": event_id, "entity_id": entity_id, "revision": revision, "action": action, "created_at": self.clock()}
        conn.execute("INSERT INTO events(id,owner,entity_id,body) VALUES (?,?,?,?)", (event_id, owner, entity_id, self.cipher.encrypt(Domain.EVENT, owner, event_id, event)))
        for target in targets:
            delivery_id = new_id()
            payload = {"kind": "deliver", "event_id": event_id, "delivery_id": delivery_id, "target": target, "dependencies": []}
            job_id = self._enqueue(conn, owner, "deliver", f"{event_id}:{target}", payload)
            conn.execute("INSERT INTO outbox VALUES (?,?,?,?)", (delivery_id, event_id, job_id, self.cipher.encrypt(Domain.DELIVERY, owner, delivery_id, payload)))
        return event_id

    def set_permission(self, owner: str, fact_id: str, *, enabled: bool, expected_revision: int, targets=("sync",)):
        if not isinstance(enabled, bool):
            raise ValueError("permission must be boolean")
        owner_key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            row, data = self._fact(conn, owner_key, fact_id)
            if row["revision"] != expected_revision:
                raise VersionConflict("fact revision changed")
            if data["enabled"] == enabled:
                return expected_revision
            revision = expected_revision + 1
            data["enabled"] = enabled
            self._write_fact(conn, owner_key, fact_id, Domain(row["domain"]), revision, data)
            self._change(conn, owner_key, fact_id, revision, "restored" if enabled else "disabled", targets)
            return revision

    def read_fact(self, owner: str, fact_id: str, *, for_model=False):
        owner_key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            row, data = self._fact(conn, owner_key, fact_id)
            if for_model and not self._eligible(conn, owner_key, [{"fact_id": fact_id, "revision": row["revision"]}]):
                raise PermissionDenied("fact is disabled or restore permissions are unverified")
            return {"fact_id": fact_id, "revision": row["revision"], "domain": row["domain"], "data": data}

    def search(self, owner: str, domain: Domain, query: str, *, limit=100):
        if not 1 <= limit <= 1000:
            raise ValueError("search bound must be between 1 and 1000")
        owner_key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            if not self._meta(conn)["permissions_ready"]:
                raise PermissionDenied("restore permissions unverified")
            rows = conn.execute("SELECT * FROM facts WHERE owner=? AND domain=? ORDER BY id", (owner_key, domain.value))
            found = []
            while batch := rows.fetchmany(100):
                for row in batch:
                    data = self.cipher.decrypt(domain, owner_key, f"{row['id']}:{row['revision']}", row["body"])
                    if data.get("enabled") and query.casefold() in str(data.get("text", "")).casefold():
                        found.append({"fact_id": row["id"], "revision": row["revision"], "data": data})
                        if len(found) == limit:
                            return found
            return found

    def claim(self, *, lease_seconds=30, kinds=None):
        if not 0 < lease_seconds <= 300:
            raise ValueError("invalid lease duration")
        now = self.clock()
        with self._transaction() as conn:
            if not self._meta(conn).get("permissions_ready", False):
                return None
            conn.execute("UPDATE jobs SET status='failed',lease_token=NULL,lease_until=NULL WHERE status='running' AND lease_until<=? AND attempts>=max_attempts", (now,))
            rows = conn.execute("SELECT * FROM jobs WHERE (status IN ('pending','retry_wait') AND available<=?) OR (status='running' AND lease_until<=? AND attempts<max_attempts) ORDER BY available,id", (now, now))
            for row in rows:
                payload = self.cipher.decrypt(Domain.JOB, row["owner"], row["id"], row["body"])
                if kinds is not None and payload.get("kind") not in kinds:
                    continue
                if not self._claim_allowed(conn, row, payload):
                    continue
                if not self._eligible(conn, row["owner"], payload.get("dependencies", [])):
                    conn.execute("UPDATE jobs SET status='cancelled',lease_token=NULL,lease_until=NULL WHERE id=?", (row["id"],))
                    continue
                token = new_id()
                conn.execute("UPDATE jobs SET status='running',attempts=attempts+1,lease_until=?,lease_token=? WHERE id=?", (now + lease_seconds, token, row["id"]))
                return {"job_id": row["id"], "lease_token": token, "attempt": row["attempts"] + 1, "payload": payload}
        return None

    def _claim_allowed(self, conn, row, payload):
        return True

    def finish(self, job_id: str, lease_token: str, *, succeeded: bool, retry_delay=1):
        if not 0 <= retry_delay <= 3600:
            raise ValueError("invalid retry delay")
        with self._transaction() as conn:
            self._require_ready(conn)
            row = conn.execute("SELECT * FROM jobs WHERE id=?", (job_id,)).fetchone()
            if not row or row["status"] != "running" or row["lease_token"] != lease_token or row["lease_until"] <= self.clock():
                raise VersionConflict("worker lease is no longer current")
            payload = self.cipher.decrypt(Domain.JOB, row["owner"], job_id, row["body"])
            if not self._eligible(conn, row["owner"], payload.get("dependencies", [])):
                status = "cancelled"
            else:
                status = "succeeded" if succeeded else ("failed" if row["attempts"] >= row["max_attempts"] else "retry_wait")
            conn.execute("UPDATE jobs SET status=?,available=?,lease_token=NULL,lease_until=NULL WHERE id=?", (status, self.clock() + retry_delay, job_id))
            return status

    def renew(self, job_id: str, lease_token: str, *, lease_seconds=30):
        if not 0 < lease_seconds <= 300:
            raise ValueError("invalid lease duration")
        with self._transaction() as conn:
            self._require_ready(conn)
            changed = conn.execute("UPDATE jobs SET lease_until=? WHERE id=? AND status='running' AND lease_token=? AND lease_until>?", (self.clock() + lease_seconds, job_id, lease_token, self.clock())).rowcount
            if not changed:
                raise VersionConflict("worker lease is no longer current")

    def commit_extracted_fact(self, job_id: str, lease_token: str, fact_id: str, domain: Domain, data: dict):
        """One durable extraction effect and its completion/event/outbox, or none."""
        if domain != Domain.USER_FACT or not isinstance(data.get("enabled"), bool):
            raise ValueError("extraction requires an explicit user-fact permission")
        with self._transaction() as conn:
            self._require_ready(conn)
            job = conn.execute("SELECT * FROM jobs WHERE id=?", (job_id,)).fetchone()
            if not job or job["status"] != "running" or job["lease_token"] != lease_token or job["lease_until"] <= self.clock():
                raise VersionConflict("worker lease is no longer current")
            payload = self.cipher.decrypt(Domain.JOB, job["owner"], job_id, job["body"])
            if payload["kind"] != "extract" or not self._eligible(conn, job["owner"], payload.get("dependencies", [])):
                raise PermissionDenied("extraction input is no longer eligible")
            if conn.execute("SELECT 1 FROM facts WHERE id=?", (fact_id,)).fetchone():
                raise VersionConflict("fact identity already exists")
            self._write_fact(conn, job["owner"], fact_id, domain, 1, data)
            self._change(conn, job["owner"], fact_id, 1, "created", ("sync",))
            payload["result"] = {"fact_id": fact_id, "revision": 1}
            body = self.cipher.encrypt(Domain.JOB, job["owner"], job_id, payload)
            conn.execute("UPDATE jobs SET status='succeeded',lease_token=NULL,lease_until=NULL,body=? WHERE id=?", (body, job_id))
            return payload["result"]

    def change_head(self):
        with self._transaction(write=False) as conn:
            return conn.execute("SELECT COALESCE(MAX(seq),0) FROM events").fetchone()[0]

    def backup(self, destination: str):
        if os.path.exists(destination):
            raise FileExistsError("backup target already exists")
        destination = os.path.abspath(destination)
        fd, partial = tempfile.mkstemp(prefix="tks-backup-", suffix=".partial", dir=os.path.dirname(destination))
        os.close(fd)
        source = self._connect()
        target = sqlite3.connect(partial)
        try:
            try:
                source.backup(target)
                # Backups start closed for model use, even when opened directly.
                self._set_meta(target, {"schema": 1, "permissions_ready": False})
                target.commit()
            finally:
                target.close()
                source.close()
            # Publish without replacing an existing backup; hard links are atomic
            # on the supported local NTFS/POSIX filesystems.
            os.link(partial, destination)
        finally:
            os.unlink(partial)

    def mark_restored(self):
        """Call on a restored copy before any use. Reopen preserves the guard."""
        with self._transaction() as conn:
            meta = self._meta(conn)
            meta["permissions_ready"] = False
            self._set_meta(conn, meta)

    def verify_restored_permissions(self, authoritative_sequence: int):
        with self._transaction() as conn:
            sequence = conn.execute("SELECT COALESCE(MAX(seq),0) FROM events").fetchone()[0]
            if authoritative_sequence != sequence:
                raise PermissionDenied("latest permission changes are missing; restore stays closed")
            meta = self._meta(conn)
            meta["permissions_ready"] = True
            self._set_meta(conn, meta)
