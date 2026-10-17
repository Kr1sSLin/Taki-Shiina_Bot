"""Durable text-chat application transactions over the foundation repository."""
import base64
import binascii
from dataclasses import asdict
import hashlib
import os

from secure_storage import DataCorruptionError
from services.contracts import ChatCommand, Domain, new_id
from services.foundation_store import PermissionDenied, VersionConflict
from services.memory_store import MemoryStore
from services.daily_store import DailyEventMixin
from services.privacy import scrub_locations
from services.vision_limits import MAX_INLINE_IMAGE_BYTES
from services.owner_fence import serialized_owner
from services.public_policy import PUBLIC_POLICY_VERSION, PROMPT_VERSIONS
from services.reply_check import reply_digest, DraftBudgetExceeded


class ReliableChatStore(DailyEventMixin, MemoryStore):
    def __init__(self, *args, require_checks=False, **kwargs):
        # Production assembly enables this also for pre-upgrade outbox rows.
        self.require_checks = require_checks
        super().__init__(*args, **kwargs)
        with self._transaction() as conn:
            conn.execute("CREATE TABLE IF NOT EXISTS chat_turns (id TEXT PRIMARY KEY,owner TEXT NOT NULL,status TEXT NOT NULL,generation INTEGER NOT NULL,created REAL NOT NULL,deadline REAL NOT NULL,body BLOB NOT NULL)")
            conn.execute("CREATE INDEX IF NOT EXISTS chat_turns_owner ON chat_turns(owner,status,created)")
            conn.execute("CREATE TABLE IF NOT EXISTS chat_messages (seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,owner TEXT NOT NULL,turn_id TEXT NOT NULL,body BLOB NOT NULL)")
            conn.execute("CREATE INDEX IF NOT EXISTS chat_messages_owner ON chat_messages(owner,seq)")
            conn.execute("CREATE TABLE IF NOT EXISTS chat_owners (owner TEXT PRIMARY KEY,body BLOB NOT NULL)")

    def _turn(self, conn, turn_id):
        row = conn.execute("SELECT * FROM chat_turns WHERE id=?", (turn_id,)).fetchone()
        if row is None:
            raise VersionConflict("chat turn unavailable")
        value = self.cipher.decrypt(Domain.DERIVED, row["owner"], turn_id, row["body"])
        return row, value

    def _save_turn(self, conn, row, value, *, status=None, generation=None):
        conn.execute("UPDATE chat_turns SET status=?,generation=?,body=? WHERE id=?", (status or row["status"], generation or row["generation"], self.cipher.encrypt(Domain.DERIVED, row["owner"], row["id"], value), row["id"]))

    def _time_state(self):
        # Both user-facing Beijing dates and character Tokyo dates can change.
        return [int((self.clock() + hours*3600)//86400) for hours in (8, 9)]

    @serialized_owner("commands")
    def accept(self, commands: list[ChatCommand], *, delay=0, merge_pending=False, memory_first=False):
        if not commands or len({c.owner for c in commands}) != 1 or not 0 <= delay <= 20:
            raise ValueError("a bounded batch for one authenticated owner is required")
        owner = self.cipher.index("owner", commands[0].owner)
        now = self.clock()
        with self._transaction() as conn:
            self._require_ready(conn)
            conn.execute("INSERT OR IGNORE INTO chat_owners VALUES (?,?)", (owner, self.cipher.encrypt(Domain.DERIVED, owner, "owner", {"owner": commands[0].owner})))
            receipts, pending = [], []
            for command in commands:
                request_key = self.cipher.index("request", command.owner, command.request_id)
                client_key = self.cipher.index("client", command.owner, command.client_message_id)
                existing = conn.execute("SELECT * FROM requests WHERE owner=? AND (request_key=? OR client_key=?)", (owner, request_key, client_key)).fetchone()
                if existing:
                    previous = self.cipher.decrypt(Domain.SOURCE_MESSAGE, owner, existing["id"], existing["body"])
                    if any(previous[key] != scrub_locations(getattr(command, key)) for key in ("request_id", "client_message_id", "text", "context_revision")):
                        raise VersionConflict("request identity reused with changed input")
                    previous_images = ((previous.get("metadata") or {}).get("images") or [])
                    incoming_images = ((command.metadata or {}).get("images") or [])
                    if previous_images != incoming_images:
                        raise VersionConflict("request identity reused with changed images")
                    receipts.append({"turn_id": existing["turn_id"], "message_id": existing["id"], "duplicate": True})
                else:
                    pending.append((command, request_key, client_key))
            if not pending:
                return receipts
            incoming_interactions = sum(command.intent == "interaction" for command, _, _ in pending)
            if incoming_interactions > 1:
                raise ValueError("only one interaction is allowed per logical turn")
            max_images = max(1, int(os.getenv("VISION_MAX_IMAGE_COUNT", "3")))
            incoming_images = sum(len((command.metadata or {}).get("images") or []) for command, _, _ in pending)
            if incoming_images > max_images:
                raise ValueError("image count exceeds the logical turn limit")
            incoming_image_bytes = sum(
                len(base64.b64decode(image["dataBase64"]))
                for command, _, _ in pending
                for image in ((command.metadata or {}).get("images") or [])
            )
            if incoming_image_bytes > MAX_INLINE_IMAGE_BYTES:
                raise ValueError("image bytes exceed the logical turn limit")
            turn_row = None
            candidates = conn.execute("SELECT * FROM chat_turns WHERE owner=? AND status IN ('pending','running','completed') AND deadline>? ORDER BY created DESC,rowid DESC", (owner, now))
            for candidate in candidates:
                row, candidate_body = self._turn(conn, candidate["id"])
                if candidate_body.get("dispatched_at") or candidate_body.get("revoked"):
                    continue
                # Existing paid operations retain their independent identity.
                if incoming_interactions and candidate_body.get("payments"):
                    continue
                job = conn.execute("SELECT status FROM jobs WHERE id=?", (candidate_body["job_id"],)).fetchone()
                if job and job["status"] in ("failed", "cancelled"):
                    continue
                in_flight = row["status"] in ("running", "completed")
                if job and (in_flight or (merge_pending and candidate_body.get("merge_pending") and job["status"] == "pending")):
                    image_count = candidate_body.get("image_count")
                    image_bytes = candidate_body.get("image_bytes")
                    if image_count is None or image_bytes is None:
                        image_count, image_bytes = 0, 0
                        for source_id in candidate_body["sources"]:
                            source_row = conn.execute("SELECT body FROM requests WHERE id=? AND owner=?", (source_id, owner)).fetchone()
                            source = self.cipher.decrypt(Domain.SOURCE_MESSAGE, owner, source_id, source_row["body"])
                            images = (source.get("metadata") or {}).get("images") or []
                            image_count += len(images)
                            image_bytes += sum(len(base64.b64decode(image["dataBase64"])) for image in images)
                    if image_count + incoming_images > max_images or image_bytes + incoming_image_bytes > MAX_INLINE_IMAGE_BYTES:
                        break
                    candidate_body["image_count"] = image_count
                    candidate_body["image_bytes"] = image_bytes
                    if row["status"] == "completed":
                        self._revoke_reply(conn, row, candidate_body, "new_input")
                        candidate_body["result"] = None
                        candidate_body["memory_refs"] = []
                        candidate_body.pop("revoked", None)
                        conn.execute("UPDATE jobs SET status='pending',available=?,lease_token=NULL,lease_until=NULL WHERE id=?", (now, candidate_body["job_id"]))
                    turn_row, turn_body = row, candidate_body
                    break
            if turn_row is None:
                turn_id = new_id()
                turn_body = {"owner": commands[0].owner, "sources": [], "merge_pending": merge_pending, "result": None, "image_count": 0, "image_bytes": 0, "phases": [{"phase": "accepted", "at": now, "generation": 1}]}
                job_id = self._enqueue(conn, owner, "chat", turn_id, {"kind": "chat", "turn_id": turn_id, "dependencies": []})
                turn_body["job_id"] = job_id
                conn.execute("INSERT INTO chat_turns VALUES (?,?,'pending',1,?,?,?)", (turn_id, owner, now, now + 120, self.cipher.encrypt(Domain.DERIVED, owner, turn_id, turn_body)))
                turn_row, _ = self._turn(conn, turn_id)
            turn_id = turn_row["id"]
            turn_body.pop("memory_control_result", None)
            turn_body["memory_first"] = bool(memory_first or turn_body.get("memory_first"))
            seen = set()
            for command, request_key, client_key in pending:
                if request_key in seen:
                    raise VersionConflict("duplicate request within the same batch")
                seen.add(request_key)
                message_id = new_id()
                conn.execute("INSERT INTO requests VALUES (?,?,?,?,?,?)", (owner, request_key, client_key, message_id, turn_id, self.cipher.encrypt(Domain.SOURCE_MESSAGE, owner, message_id, {**asdict(command), "received_at": now})))
                turn_body["sources"].append(message_id)
                turn_body["image_count"] += len((command.metadata or {}).get("images") or [])
                turn_body["image_bytes"] += sum(len(base64.b64decode(image["dataBase64"])) for image in ((command.metadata or {}).get("images") or []))
                extraction_epoch = self._extraction_epoch(conn, owner)
                self._enqueue(conn, owner, "extract", message_id, {"kind": "extract", "message_id": message_id, "turn_id": turn_id, "permission_epoch": extraction_epoch, "dependencies": []})
                if command.intent == "interaction":
                    turn_body.setdefault("payments", {})[message_id] = "pending"
                    recovery = self._enqueue(conn, owner, "interaction_reconcile", message_id, {"kind": "interaction_reconcile", "message_id": message_id, "turn_id": turn_id, "dependencies": []})
                    conn.execute("UPDATE jobs SET available=? WHERE id=?", (now + 15, recovery))
                receipts.append({"turn_id": turn_id, "message_id": message_id, "duplicate": False})
            appended = len(turn_body["sources"]) > len(pending)
            self._save_turn(conn, turn_row, turn_body, status="pending" if turn_row["status"] == "completed" else None, generation=turn_row["generation"] + int(appended))
            # Silence-window resets are bounded independently of the 120s turn.
            conn.execute("UPDATE jobs SET available=? WHERE id=? AND status='pending'", (min(now + delay, turn_row["created"] + 25, turn_row["deadline"] - 1), turn_body["job_id"]))
            return receipts

    @serialized_owner()
    def set_permission(self, owner, *args, **kwargs):
        return super().set_permission(owner, *args, **kwargs)

    @serialized_owner()
    def put_fact(self, owner, *args, **kwargs):
        return super().put_fact(owner, *args, **kwargs)

    @serialized_owner()
    def publish_lore(self, owner, *args, **kwargs):
        return super().publish_lore(owner, *args, **kwargs)

    def _change(self, conn, owner, entity_id, revision, action, targets):
        event = super()._change(conn, owner, entity_id, revision, action, targets)
        # Permission/content invalidation and compensation share the mutation's
        # transaction. Reinforcement/compensation events do not recurse here.
        if action in ("disabled", "restored", "updated"):
            for row in conn.execute("SELECT id FROM chat_turns WHERE owner=? AND status='completed'", (owner,)).fetchall():
                turn, body = self._turn(conn, row["id"])
                if body.get("dispatched_at") or body.get("revoked"):
                    continue
                try:
                    self._check_reply_delivery(conn, turn, body)
                except (VersionConflict, PermissionDenied):
                    self._revoke_reply(conn, turn, body, "dependencies_changed")
                    self._save_turn(conn, turn, body, status="cancelled")
        return event

    @serialized_owner("job")
    def handle_memory_control(self, job):
        return super().handle_memory_control(job)

    def restart_changed_turn(self, job, snapshot):
        with self._transaction() as conn:
            self._lease(conn, job)
            row, body = self._turn(conn, snapshot["turn_id"])
            if row["generation"] == snapshot["generation"] or row["deadline"] <= self.clock() or row["status"] == "cancelled":
                return False
            self._save_turn(conn, row, body, status="pending")
            conn.execute("UPDATE jobs SET status='pending',available=?,lease_token=NULL,lease_until=NULL WHERE id=?", (self.clock(), job["job_id"]))
            return True

    def reserve_draft(self, snapshot):
        self.assert_snapshot(snapshot)
        with self._transaction() as conn:
            row, body = self._turn(conn, snapshot["turn_id"])
            if row["generation"] != snapshot["generation"]:
                raise VersionConflict("draft input changed")
            count = body.get("draft_count", 0)
            if count >= 2:
                raise DraftBudgetExceeded("turn draft budget exhausted")
            body["draft_count"] = count + 1
            body["controlled"] = True
            body["public_policy_version"] = PUBLIC_POLICY_VERSION
            self._save_turn(conn, row, body)

    def record_phase(self, snapshot, phase):
        if phase not in ("context_ready", "generating", "checking", "ready_to_commit"):
            raise ValueError("unknown internal phase")
        with self._transaction() as conn:
            row, body = self._turn(conn, snapshot["turn_id"])
            if row["generation"] != snapshot["generation"]:
                raise VersionConflict("phase belongs to an obsolete generation")
            body.setdefault("phases", []).append({"phase": phase, "at": self.clock(), "generation": row["generation"]})
            body["phases"] = body["phases"][-20:]
            self._save_turn(conn, row, body)

    def _claim_allowed(self, conn, row, payload):
        if payload.get("kind") == "interaction_reconcile":
            turn, body = self._turn(conn, payload["turn_id"])
            chat = conn.execute("SELECT status FROM jobs WHERE id=?", (body["job_id"],)).fetchone()
            confirmed = body.get("payments", {}).get(payload["message_id"]) == "confirmed"
            return not (confirmed and turn["status"] not in ("completed", "cancelled") and turn["deadline"] > self.clock() and chat and chat["status"] not in ("failed", "cancelled"))
        if payload.get("kind") != "chat":
            return True
        turn, body = self._turn(conn, payload["turn_id"])
        if turn["deadline"] <= self.clock():
            self._save_turn(conn, turn, body, status="failed")
            conn.execute("UPDATE jobs SET status='failed',lease_token=NULL,lease_until=NULL WHERE id=?", (row["id"],))
            return False
        earlier = conn.execute("SELECT id FROM chat_turns WHERE owner=? AND status IN ('pending','running') AND rowid<(SELECT rowid FROM chat_turns WHERE id=?) ORDER BY rowid", (row["owner"], turn["id"]))
        for previous in earlier:
            older, old_body = self._turn(conn, previous["id"])
            older_job = conn.execute("SELECT status FROM jobs WHERE id=?", (old_body["job_id"],)).fetchone()
            if older_job and older_job["status"] not in ("failed", "cancelled") and older["deadline"] > self.clock() and not old_body.get("dispatched_at") and not old_body.get("revoked"):
                return False
        if body.get("memory_first"):
            for source_id in body["sources"]:
                dedupe = self.cipher.index("job", row["owner"], "extract", source_id)
                extraction = conn.execute("SELECT status FROM jobs WHERE dedupe=?", (dedupe,)).fetchone()
                if extraction and extraction["status"] not in ("succeeded", "cancelled", "failed"):
                    return False
        if any(state != "confirmed" for state in body.get("payments", {}).values()):
            return False
        # A model call holds a lease, never a SQL transaction. Other tasks and
        # owners proceed, while two processes cannot generate for one owner.
        active = conn.execute("SELECT * FROM jobs WHERE owner=? AND status='running' AND lease_until>? AND id<>?", (row["owner"], self.clock(), row["id"]))
        for job in active:
            value = self.cipher.decrypt(Domain.JOB, job["owner"], job["id"], job["body"])
            if value.get("kind") == "chat":
                return False
        return True

    def _lease(self, conn, job):
        row = conn.execute("SELECT * FROM jobs WHERE id=?", (job["job_id"],)).fetchone()
        if not row or row["status"] != "running" or row["lease_token"] != job["lease_token"] or row["lease_until"] <= self.clock():
            raise VersionConflict("worker lease is no longer current")
        payload = self.cipher.decrypt(Domain.JOB, row["owner"], row["id"], row["body"])
        if payload != job.get("payload"):
            raise PermissionDenied("job input differs from its durable authenticated payload")
        return row

    def source_for_job(self, job):
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row = self._lease(conn, job)
            if job["payload"].get("kind") != "extract":
                raise PermissionDenied("job is not an extraction")
            source = conn.execute("SELECT * FROM requests WHERE id=? AND owner=?", (job["payload"]["message_id"], row["owner"])).fetchone()
            if source is None:
                raise PermissionDenied("source unavailable")
            return self.cipher.decrypt(Domain.SOURCE_MESSAGE, row["owner"], source["id"], source["body"])

    def _permission_snapshot(self, conn, owner):
        self._require_ready(conn)
        epoch = 0
        for event in conn.execute("SELECT * FROM events WHERE owner=? ORDER BY seq DESC", (owner,)):
            value = self.cipher.decrypt(Domain.EVENT, owner, event["id"], event["body"])
            if value["action"] in ("disabled", "restored", "updated"):
                epoch = event["seq"]
                break
        rows = conn.execute("SELECT id FROM facts WHERE owner=?", (owner,))
        allowed = True
        for row in rows:
            _, fact = self._fact(conn, owner, row["id"])
            if not fact.get("enabled", False):
                allowed = False
                break
        return epoch, allowed

    def _extraction_epoch(self, conn, owner):
        for event in conn.execute("SELECT * FROM events WHERE owner=? ORDER BY seq DESC", (owner,)):
            value = self.cipher.decrypt(Domain.EVENT, owner, event["id"], event["body"])
            if value["action"] in ("disabled", "restored"):
                return event["seq"]
        return 0

    def begin_turn(self, job):
        with self._transaction() as conn:
            self._require_ready(conn)
            lease = self._lease(conn, job)
            if job["payload"].get("kind") != "chat":
                raise PermissionDenied("job is not a chat generation")
            row, body = self._turn(conn, job["payload"]["turn_id"])
            if row["owner"] != lease["owner"] or row["status"] == "completed" or row["deadline"] <= self.clock():
                raise VersionConflict("turn is no longer current")
            if body.get("memory_first"):
                for source_id in body["sources"]:
                    dedupe = self.cipher.index("job", row["owner"], "extract", source_id)
                    extraction = conn.execute("SELECT status FROM jobs WHERE dedupe=?", (dedupe,)).fetchone()
                    if not extraction or extraction["status"] in ("failed", "cancelled"):
                        body.update(memory_identity_unresolved=True, memory_extraction_incomplete=True)
            sources = []
            for identity in body["sources"]:
                source = conn.execute("SELECT * FROM requests WHERE id=? AND owner=?", (identity, row["owner"])).fetchone()
                sources.append(self.cipher.decrypt(Domain.SOURCE_MESSAGE, row["owner"], identity, source["body"]))
            deadline = row["deadline"]
            if any(source.get("intent") == "interaction" for source in sources):
                if "generation_deadline" not in body:
                    body["generation_deadline"] = min(deadline, self.clock() + 15)
                deadline = min(deadline, body["generation_deadline"])
                conn.execute("UPDATE chat_turns SET deadline=? WHERE id=?", (deadline, row["id"]))
            epoch, _ = self._permission_snapshot(conn, row["owner"])
            body["permission_epoch"] = epoch
            body["lore_version"] = self._lore_head(conn, row["owner"])
            body["time_state"] = self._time_state()
            body["memory_refs"] = []
            body.setdefault("phases", []).append({"phase": "batched", "at": self.clock(), "generation": row["generation"]})
            self._save_turn(conn, row, body, status="running")
            return {"turn_id": row["id"], "generation": row["generation"], "deadline": deadline, "owner": body["owner"], "sources": sources, "permission_epoch": epoch, "lore_version": body["lore_version"], "time_state": body["time_state"], "legacy_allowed": False, "memory_refs": [], "memory_clarification": body.get("memory_clarification", False), "memory_extraction_incomplete": body.get("memory_extraction_incomplete", False)}

    def assert_snapshot(self, snapshot):
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row, body = self._turn(conn, snapshot["turn_id"])
            epoch, _ = self._permission_snapshot(conn, row["owner"])
            if row["owner"] != self.cipher.index("owner", snapshot["owner"]) or row["generation"] != snapshot["generation"] or epoch != snapshot["permission_epoch"] or row["deadline"] <= self.clock() or row["status"] != "running":
                raise VersionConflict("generation context is no longer current")
            if body.get("memory_refs", []) != snapshot.get("memory_refs", []):
                raise VersionConflict("selected generation memories changed")
            if self._lore_head(conn, row["owner"]) != snapshot.get("lore_version"):
                raise VersionConflict("official source version changed")
            if self._time_state() != snapshot.get("time_state"):
                raise VersionConflict("generation date changed")
            self._validate_memories(conn, row["owner"], body.get("memory_refs", []))

    def prepare_memories(self, snapshot, query):
        refs = self.select_memories(snapshot["owner"], query)
        with self._transaction() as conn:
            self._require_ready(conn)
            row, body = self._turn(conn, snapshot["turn_id"])
            epoch, _ = self._permission_snapshot(conn, row["owner"])
            if row["owner"] != self.cipher.index("owner", snapshot["owner"]) or row["status"] != "running" or row["generation"] != snapshot["generation"] or epoch != snapshot["permission_epoch"] or row["deadline"] <= self.clock():
                raise VersionConflict("generation changed before memory selection")
            if body.get("memory_identity_unresolved"):
                refs = []
            self._validate_memories(conn, row["owner"], refs)
            body["memory_refs"] = refs
            self._save_turn(conn, row, body)
        snapshot["memory_refs"] = refs
        return refs

    def renew(self, job_id, lease_token, *, lease_seconds=30):
        if not 0 < lease_seconds <= 300:
            raise ValueError("invalid lease duration")
        with self._transaction() as conn:
            self._require_ready(conn)
            row = conn.execute("SELECT * FROM jobs WHERE id=?", (job_id,)).fetchone()
            if not row or row["status"] != "running" or row["lease_token"] != lease_token or row["lease_until"] <= self.clock():
                raise VersionConflict("worker lease is no longer current")
            payload = self.cipher.decrypt(Domain.JOB, row["owner"], row["id"], row["body"])
            if not self._eligible(conn, row["owner"], payload.get("dependencies", [])):
                raise PermissionDenied("job dependencies changed")
            if payload.get("kind") == "chat":
                turn, body = self._turn(conn, payload["turn_id"])
                epoch, _ = self._permission_snapshot(conn, row["owner"])
                if body.get("permission_epoch", epoch) != epoch or turn["deadline"] <= self.clock():
                    raise PermissionDenied("generation context changed")
            conn.execute("UPDATE jobs SET lease_until=? WHERE id=?", (self.clock()+lease_seconds, job_id))

    def history(self, owner, *, limit=300, for_model=False):
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            _, allowed = self._permission_snapshot(conn, key)
            # Raw history has no reliable fact-span provenance. O-03 permits
            # only current turn direct input; historical prose stays display-only.
            if for_model:
                return []
            rows = conn.execute("SELECT * FROM chat_messages WHERE owner=? ORDER BY seq DESC LIMIT ?", (key, max(1, min(limit, 3000)))).fetchall()
            return [self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, row["id"], row["body"]) for row in reversed(rows)]

    def history_delivery(self, owner, *, limit=3000):
        """Separate prior delivered history from replies awaiting first delivery."""
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            rows = conn.execute("SELECT * FROM chat_messages WHERE owner=? ORDER BY seq DESC LIMIT ?", (key, max(1, min(limit, 3000)))).fetchall()
            items, pending = [], []
            for row in reversed(rows):
                item = self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, row["id"], row["body"])
                if item["role"] == "bot":
                    turn, body = self._turn(conn, row["turn_id"])
                    if not body.get("dispatched_at"):
                        try:
                            self._check_reply_delivery(conn, turn, body)
                        except (VersionConflict, PermissionDenied):
                            continue
                        pending.append(body["result"])
                items.append(item)
            return items, pending

    def read_image(self, owner: str, request_id: str, index: int):
        """Return (MIME, exact raw bytes), or None for an inaccessible image.

        Requests are looked up through their owner-keyed private index. The
        restore permission gate applies before any image bytes are decrypted.
        """
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            if not isinstance(request_id, str) or not request_id or len(request_id) > 200 or not isinstance(index, int) or index < 0:
                return None
            request_key = self.cipher.index("request", owner, request_id)
            row = conn.execute("SELECT id,body FROM requests WHERE owner=? AND request_key=?", (key, request_key)).fetchone()
            if row is None:
                return None
            source = self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, row["id"], row["body"])
            if source.get("owner") != owner or source.get("request_id") != request_id:
                raise DataCorruptionError("image source identity mismatch")
            images = (source.get("metadata") or {}).get("images") or []
            if index >= len(images):
                return None
            image = images[index]
            if not isinstance(image, dict):
                raise DataCorruptionError("image source metadata invalid")
            mime, encoded, digest = image.get("mimeType"), image.get("dataBase64"), image.get("sha256")
            if mime not in {"image/jpeg", "image/png", "image/webp", "image/gif"} or not isinstance(encoded, str) or not isinstance(digest, str):
                raise DataCorruptionError("image source metadata invalid")
            try:
                raw = base64.b64decode(encoded, validate=True)
            except (ValueError, binascii.Error) as exc:
                raise DataCorruptionError("image source encoding invalid") from exc
            if not raw or base64.b64encode(raw).decode("ascii") != encoded or hashlib.sha256(raw).hexdigest() != digest:
                raise DataCorruptionError("image source digest mismatch")
            return mime, raw

    def turn_result(self, owner, turn_id):
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row, body = self._turn(conn, turn_id)
            if row["owner"] != key:
                raise PermissionDenied("turn owner mismatch")
            if row["status"] == "completed":
                self._check_reply_delivery(conn, row, body)
            job = conn.execute("SELECT * FROM jobs WHERE id=?", (body["job_id"],)).fetchone()
            payload = self.cipher.decrypt(Domain.JOB, key, job["id"], job["body"])
            return {"status": row["status"], "job_status": job["status"], "deadline": row["deadline"], "result": body["result"], "failure_category": payload.get("failure_category", "generation")}

    def _check_reply_delivery(self, conn, row, body):
        if body.get("revoked") or row["status"] != "completed":
            raise PermissionDenied("reply was superseded before dispatch")
        if self.require_checks:
            self._require_complete_check(body.get("result") or {})
        if not body.get("dispatched_at") and body.get("time_state") != self._time_state():
            raise PermissionDenied("reply date changed before first dispatch")
        epoch, _ = self._permission_snapshot(conn, row["owner"])
        if epoch != body.get("permission_epoch"):
            raise PermissionDenied("undelivered reply permission changed")
        if self._lore_head(conn, row["owner"]) != body.get("lore_version"):
            raise PermissionDenied("official source version changed before delivery")
        self._validate_memories(conn, row["owner"], body.get("delivery_memory_refs", []), strict_revision=False)

    @staticmethod
    def _require_complete_check(result):
        receipt = result.get("check") or {}
        if receipt.get("policy_version") != PUBLIC_POLICY_VERSION or receipt.get("prompt_versions") != PROMPT_VERSIONS or receipt.get("digest") != reply_digest(result) or receipt.get("approved") is not True:
            raise PermissionDenied("reply lacks a current complete check")

    def _revoke_reply(self, conn, row, body, reason):
        if body.get("revoked") or body.get("dispatched_at"):
            return
        for entry in conn.execute("SELECT * FROM memory_ledger WHERE owner=? AND turn_id=?", (row["owner"], row["id"])).fetchall():
            identity = f"{row['id']}:{entry['fact_id']}"
            effect = self.cipher.decrypt(Domain.EVIDENCE, row["owner"], identity, entry["body"])
            if effect.get("reason") != "usage" or effect.get("compensated"):
                continue
            fact, data = self._fact(conn, row["owner"], entry["fact_id"])
            if data.get("revision_id") == effect.get("revision_id"):
                data["weight"] = max(0, data["weight"] - effect.get("delta", 1))
                self._write_fact(conn, row["owner"], fact["id"], Domain(fact["domain"]), fact["revision"] + 1, data)
                self._change(conn, row["owner"], fact["id"], fact["revision"] + 1, "usage_revoked", ("sync",))
            effect.update(compensated=True, compensated_at=self.clock(), reason_detail=reason)
            conn.execute("UPDATE memory_ledger SET body=? WHERE owner=? AND turn_id=? AND fact_id=?", (self.cipher.encrypt(Domain.EVIDENCE, row["owner"], identity, effect), row["owner"], row["id"], fact["id"]))
        result = body.get("result") or {}
        if result.get("messageId"):
            conn.execute("DELETE FROM chat_messages WHERE id=? AND owner=?", (result["messageId"], row["owner"]))
        if result.get("deliveryId"):
            conn.execute("UPDATE jobs SET status='cancelled',lease_token=NULL,lease_until=NULL WHERE id=(SELECT job_id FROM outbox WHERE id=?)", (result["deliveryId"],))
        body["revoked"] = reason
        body["revoked_at"] = self.clock()
        self._revoke_daily_references(conn, row, reason)
        self._change(conn, row["owner"], row["id"], row["generation"], "reply_revoked", ())

    def mark_dispatched(self, owner, result):
        """Called inside the owner's network fence immediately before transport.

        A crash after this boundary is delivery-uncertain; retries keep the ID.
        """
        with self._transaction() as conn:
            self._require_ready(conn)
            row = conn.execute("SELECT turn_id FROM chat_messages WHERE id=? AND owner=?", (result["messageId"], self.cipher.index("owner", owner))).fetchone()
            if row is None:
                raise PermissionDenied("reply is no longer deliverable")
            turn, body = self._turn(conn, row["turn_id"])
            self._check_reply_delivery(conn, turn, body)
            for previous in conn.execute("SELECT id FROM chat_turns WHERE owner=? AND status='completed' AND rowid<(SELECT rowid FROM chat_turns WHERE id=?)", (turn["owner"], turn["id"])):
                older_row, older = self._turn(conn, previous["id"])
                if not older.get("dispatched_at") and not older.get("revoked"):
                    try:
                        self._check_reply_delivery(conn, older_row, older)
                    except (PermissionDenied, VersionConflict):
                        self._revoke_reply(conn, older_row, older, "invalid_before_later_dispatch")
                        self._save_turn(conn, older_row, older, status="cancelled")
                        continue
                    raise StorageBusy("an earlier reply must be dispatched first")
            if (body.get("result") or {}).get("messageId") != result["messageId"]:
                raise PermissionDenied("reply generation changed before dispatch")
            body.setdefault("dispatched_at", self.clock())
            body["delivery_attempts"] = body.get("delivery_attempts", 0) + 1
            self._save_turn(conn, turn, body)

    def mark_delivered(self, owner, result):
        with self._transaction() as conn:
            self._require_ready(conn)
            source = conn.execute("SELECT turn_id FROM chat_messages WHERE id=? AND owner=?", (result["messageId"], self.cipher.index("owner", owner))).fetchone()
            if source is None:
                raise PermissionDenied("delivered reply is unavailable")
            row, body = self._turn(conn, source["turn_id"])
            body.setdefault("delivery_completed_at", self.clock())
            self._save_turn(conn, row, body)

    def record_failure(self, job, category):
        if category not in ("generation", "storage"):
            raise ValueError("unknown failure category")
        with self._transaction() as conn:
            self._require_ready(conn)
            row = self._lease(conn, job)
            payload = dict(job["payload"], failure_category=category)
            conn.execute("UPDATE jobs SET body=? WHERE id=?", (self.cipher.encrypt(Domain.JOB, row["owner"], row["id"], payload), row["id"]))
            turn, body = self._turn(conn, payload["turn_id"])
            body["last_failure_at"] = self.clock()
            self._save_turn(conn, turn, body)

    def fail_closed(self, job):
        with self._transaction() as conn:
            row = self._lease(conn, job)
            turn, body = self._turn(conn, job["payload"]["turn_id"])
            body.setdefault("phases", []).append({"phase": "failed", "at": self.clock(), "generation": turn["generation"]})
            self._save_turn(conn, turn, body, status="failed")
            conn.execute("UPDATE jobs SET status='failed',lease_token=NULL,lease_until=NULL WHERE id=?", (row["id"],))

    def chat_metrics(self, owner):
        """Content-free, per-owner end-to-end observations; no SLA inference."""
        import math
        latencies, waits, totals, failures, timeouts, committed_count = [], [], [], 0, 0, 0
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            for item in conn.execute("SELECT id FROM chat_turns WHERE owner=?", (self.cipher.index("owner", owner),)):
                row, body = self._turn(conn, item["id"])
                completed = next((phase["at"] for phase in reversed(body.get("phases", [])) if phase["phase"] == "committed"), None)
                if completed is not None and not body.get("revoked"):
                    committed_count += 1
                delivered = body.get("delivery_completed_at")
                if delivered is not None:
                    latencies.append(max(0, delivered - row["created"]))
                job = conn.execute("SELECT status FROM jobs WHERE id=?", (body["job_id"],)).fetchone()
                failed = row["status"] in ("failed", "cancelled") or job["status"] in ("failed", "cancelled") or (completed is None and self.clock() >= row["deadline"])
                failures += int(failed)
                timeouts += int((delivered if delivered is not None else self.clock()) >= row["deadline"])
                terminal = next((phase["at"] for phase in reversed(body.get("phases", [])) if phase["phase"] == "failed"), None)
                waits.append(max(0, (delivered or terminal or body.get("revoked_at") or (body.get("last_failure_at") if failed else None) or self.clock()) - row["created"]))
                totals.append(row["id"])
        ordered = sorted(latencies)
        quantile = lambda p: ordered[max(0, math.ceil(len(ordered)*p)-1)] if ordered else None
        return {"turns": len(totals), "committed": committed_count, "completed": len(latencies), "failed": failures, "timeouts": timeouts,
                "p50_seconds": quantile(0.5), "p95_seconds": quantile(0.95), "worst_seconds": max(latencies, default=None),
                "worst_wait_seconds": max(waits, default=None),
                "timeout_rate": timeouts/len(totals) if totals else None}

    @serialized_owner("job")
    def commit_reply(self, job, snapshot, result):
        with self._transaction() as conn:
            self._require_ready(conn)
            lease = self._lease(conn, job)
            if job["payload"].get("kind") != "chat" or job["payload"].get("turn_id") != snapshot.get("turn_id"):
                raise PermissionDenied("reply does not belong to its leased turn")
            row, body = self._turn(conn, snapshot["turn_id"])
            epoch, _ = self._permission_snapshot(conn, row["owner"])
            if row["owner"] != lease["owner"] or row["generation"] != snapshot["generation"] or row["deadline"] <= self.clock() or row["status"] == "completed" or epoch != snapshot["permission_epoch"]:
                raise VersionConflict("turn generation or permission snapshot changed")
            if self._lore_head(conn, row["owner"]) != snapshot.get("lore_version"):
                raise VersionConflict("official source version changed before commit")
            if self._time_state() != snapshot.get("time_state"):
                raise VersionConflict("reply date changed before commit")
            sources = []
            for identity in body["sources"]:
                source_row = conn.execute("SELECT * FROM requests WHERE id=? AND owner=?", (identity, row["owner"])).fetchone()
                if source_row is None:
                    raise PermissionDenied("turn source unavailable")
                sources.append(self.cipher.decrypt(Domain.SOURCE_MESSAGE, row["owner"], identity, source_row["body"]))
            if not isinstance(result.get("reply"), str) or not result["reply"].strip():
                raise ValueError("empty reply cannot be committed")
            if self.require_checks or body.get("controlled"):
                self._require_complete_check(result)
            if body.get("memory_refs", []) != snapshot.get("memory_refs", []):
                raise VersionConflict("reply memories differ from durable context")
            used = result.get("check", {}).get("used_memory_ids")
            usage_refs = body.get("memory_refs", []) if used is None else [ref for ref in body.get("memory_refs", []) if ref["fact_id"] in used]
            self._validate_memories(conn, row["owner"], body.get("memory_refs", []))
            self._commit_memory_usage(conn, row["owner"], row["id"], usage_refs)
            body["delivery_memory_refs"] = []
            for ref in body.get("memory_refs", []):
                current, _ = self._fact(conn, row["owner"], ref["fact_id"])
                body["delivery_memory_refs"].append({**ref, "revision": current["revision"]})
            image_count = sum(len((source.get("metadata") or {}).get("images") or []) for source in sources)
            has_text = any(source["text"] != "[图片]" for source in sources)
            content_type = "mixed" if image_count and has_text else "image" if image_count else "text"
            result = scrub_locations({**result, "messageId": new_id(), "timestamp": int(self.clock()*1000), "requestIds": [source["request_id"] for source in sources], "contentType": content_type, "imageCount": image_count})
            for index, source in enumerate(sources):
                item = {"messageId": source["client_message_id"], "userId": source["owner"], "role": "user", "content": source["text"], "timestamp": result["timestamp"] - len(sources) + index}
                source_image_count = len((source.get("metadata") or {}).get("images") or [])
                if source_image_count:
                    item.update({"contentType": "image" if source["text"] == "[图片]" else "mixed", "imageCount": source_image_count, "requestId": source["request_id"]})
                if source.get("intent") == "interaction":
                    metadata = source.get("metadata") or {}
                    gift = metadata.get("item") or {}
                    item.update({"contentType": "interaction", "itemId": gift.get("id"), "itemName": gift.get("name"), "itemIcon": gift.get("icon"), "costPoints": gift.get("cost_points"), "attachment": metadata.get("attachment") or None})
                identity = self.cipher.index("source-row", row["owner"], source["client_message_id"])
                conn.execute("INSERT OR IGNORE INTO chat_messages(id,owner,turn_id,body) VALUES (?,?,?,?)", (identity, row["owner"], row["id"], self.cipher.encrypt(Domain.SOURCE_MESSAGE, row["owner"], identity, item)))
            item = {"messageId": result["messageId"], "userId": body["owner"], "role": "bot", "content": result["reply"], "timestamp": result["timestamp"], "contentType": result["contentType"], "imageCount": image_count}
            if result.get("interaction"):
                gift = result["interaction"]
                item.update({"itemId": gift.get("itemId"), "itemName": gift.get("itemName"), "itemIcon": gift.get("itemIcon")})
                if not image_count:
                    item["contentType"] = "interaction"
            conn.execute("INSERT INTO chat_messages(id,owner,turn_id,body) VALUES (?,?,?,?)", (result["messageId"], row["owner"], row["id"], self.cipher.encrypt(Domain.SOURCE_MESSAGE, row["owner"], result["messageId"], item)))
            result["eventId"] = self._change(conn, row["owner"], row["id"], row["generation"], "reply_completed", ("chat",))
            result["deliveryId"] = conn.execute("SELECT id FROM outbox WHERE event_id=?", (result["eventId"],)).fetchone()[0]
            body["result"] = result
            body.setdefault("phases", []).append({"phase": "committed", "at": self.clock(), "generation": row["generation"]})
            self._save_turn(conn, row, body, status="completed")
            conn.execute("UPDATE jobs SET status='succeeded',lease_token=NULL,lease_until=NULL WHERE id=?", (job["job_id"],))
            return result

    def extraction_context_allowed(self, job):
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row = self._lease(conn, job)
            if job["payload"].get("kind") != "extract":
                raise PermissionDenied("job is not an extraction")
            epoch = self._extraction_epoch(conn, row["owner"])
            return job["payload"].get("permission_epoch") == epoch

    def assert_extraction_snapshot(self, job, candidates):
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row = self._lease(conn, job)
            if job["payload"].get("permission_epoch") != self._extraction_epoch(conn, row["owner"]):
                raise PermissionDenied("extraction permission changed before outbound call")
            for ref in candidates:
                current, data = self._fact(conn, row["owner"], ref["fact_id"])
                if current["revision"] != ref["revision"] or not data.get("enabled"):
                    raise VersionConflict("extraction candidate changed before outbound call")

    @serialized_owner("job")
    def commit_extraction(self, job, facts):
        with self._transaction() as conn:
            self._require_ready(conn)
            row = self._lease(conn, job)
            if job["payload"].get("kind") != "extract":
                raise PermissionDenied("job is not an extraction")
            epoch = self._extraction_epoch(conn, row["owner"])
            if job["payload"].get("permission_epoch") != epoch:
                # In-flight extraction cannot commit across permission changes.
                conn.execute("UPDATE jobs SET status='cancelled',lease_token=NULL,lease_until=NULL WHERE id=?", (job["job_id"],))
                return []
            identities = []
            source_id = job["payload"]["message_id"]
            source_row = conn.execute("SELECT * FROM requests WHERE id=? AND owner=?", (source_id, row["owner"])).fetchone()
            if source_row is None or source_row["turn_id"] != job["payload"]["turn_id"]:
                raise PermissionDenied("extraction source mismatch")
            source = self.cipher.decrypt(Domain.SOURCE_MESSAGE, row["owner"], source_id, source_row["body"])
            text = (source.get("metadata") or {}).get("attachment", "") if source.get("intent") == "interaction" else source["text"]
            if os.getenv("MEMORY_COLLECTION_ENABLED", "0") == "1":
                for item in facts[:20]:
                    identity = self._apply_evidence(conn, row["owner"], source_id, source_row["turn_id"], text, item)
                    if identity and identity not in identities:
                        identities.append(identity)
            conn.execute("UPDATE jobs SET status='succeeded',lease_token=NULL,lease_until=NULL WHERE id=?", (job["job_id"],))
            return identities

    def delivery(self, job):
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            lease = self._lease(conn, job)
            if job["payload"].get("kind") != "deliver":
                raise PermissionDenied("job is not a delivery")
            event = conn.execute("SELECT * FROM events WHERE id=?", (job["payload"]["event_id"],)).fetchone()
            if not event or event["owner"] != lease["owner"]:
                raise PermissionDenied("event owner differs from leased delivery")
            value = self.cipher.decrypt(Domain.EVENT, event["owner"], event["id"], event["body"])
            if value["action"] == "reply_completed":
                row, body = self._turn(conn, value["entity_id"])
                self._check_reply_delivery(conn, row, body)
                return {"owner": body["owner"], "target": "chat", "result": body["result"], "event_id": event["id"]}
            identity = conn.execute("SELECT body FROM chat_owners WHERE owner=?", (event["owner"],)).fetchone()
            owner = self.cipher.decrypt(Domain.DERIVED, event["owner"], "owner", identity["body"])["owner"] if identity else None
            return {"owner": owner, "target": "sync", "event": value, "event_id": event["id"]}

    def owner_context_allowed(self, owner):
        with self._transaction(write=False) as conn:
            _, allowed = self._permission_snapshot(conn, self.cipher.index("owner", owner))
            return allowed

    def ready(self, owner, turn_ids):
        key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            for turn_id in turn_ids:
                row, body = self._turn(conn, turn_id)
                if row["owner"] != key:
                    raise PermissionDenied("turn owner mismatch")
                conn.execute("UPDATE jobs SET available=? WHERE id=? AND status='pending'", (self.clock(), body["job_id"]))

    def request(self, owner, request_id):
        key = self.cipher.index("owner", owner)
        request_key = self.cipher.index("request", owner, request_id)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row = conn.execute("SELECT * FROM requests WHERE owner=? AND request_key=?", (key, request_key)).fetchone()
            if row is None:
                return None
            return {"turn_id": row["turn_id"], "source": self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, row["id"], row["body"])}

    def cancel(self, owner, turn_id):
        key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            row, body = self._turn(conn, turn_id)
            if row["owner"] != key:
                raise PermissionDenied("turn owner mismatch")
            if row["status"] == "completed":
                return body["result"]
            body["payments"] = {identity: "cancelled" for identity in body.get("payments", {})}
            self._save_turn(conn, row, body, status="cancelled", generation=row["generation"] + 1)
            conn.execute("UPDATE jobs SET status='cancelled',lease_token=NULL,lease_until=NULL WHERE id=?", (body["job_id"],))
            return None

    def _interaction(self, conn, owner, request_id):
        key = self.cipher.index("owner", owner)
        source = conn.execute("SELECT * FROM requests WHERE owner=? AND request_key=?", (key, self.cipher.index("request", owner, request_id))).fetchone()
        if source is None:
            raise VersionConflict("interaction intent unavailable")
        command = self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, source["id"], source["body"])
        if command.get("intent") != "interaction":
            raise PermissionDenied("request is not an interaction")
        row, body = self._turn(conn, source["turn_id"])
        return source, command, row, body

    def _confirm_payment(self, conn, source, row, body):
        state = body.get("payments", {}).get(source["id"])
        if row["status"] in ("cancelled", "completed") or state not in ("pending", "confirmed"):
            raise VersionConflict("interaction payment is no longer pending")
        body["payments"][source["id"]] = "confirmed"
        self._save_turn(conn, row, body)

    def confirm_interaction(self, owner, request_id):
        with self._transaction() as conn:
            self._require_ready(conn)
            source, _, row, body = self._interaction(conn, owner, request_id)
            self._confirm_payment(conn, source, row, body)

    def charge_interaction(self, owner, request_id, charge):
        # This is a short local storage operation, never a model/network call.
        # The SQL writer lock fences late JSON deductions against reconciliation.
        # A JSON charge followed by SQL failure leaves the prior pending intent,
        # which is deterministically compensated after restart.
        with self._transaction() as conn:
            self._require_ready(conn)
            source, _, row, body = self._interaction(conn, owner, request_id)
            if body.get("payments", {}).get(source["id"]) not in ("pending", "confirmed") or row["status"] in ("cancelled", "completed"):
                raise VersionConflict("interaction payment has already been reconciled")
            result = charge()
            self._confirm_payment(conn, source, row, body)
            return result

    def begin_interaction_reconciliation(self, job):
        with self._transaction() as conn:
            self._require_ready(conn)
            lease = self._lease(conn, job)
            if job["payload"].get("kind") != "interaction_reconcile":
                raise PermissionDenied("job is not an interaction reconciliation")
            source = conn.execute("SELECT * FROM requests WHERE id=? AND owner=?", (job["payload"]["message_id"], lease["owner"])).fetchone()
            if not source:
                raise PermissionDenied("interaction source unavailable")
            command = self.cipher.decrypt(Domain.SOURCE_MESSAGE, lease["owner"], source["id"], source["body"])
            row, body = self._turn(conn, source["turn_id"])
            state = body.get("payments", {}).get(source["id"])
            if row["status"] == "completed":
                return None
            chat = conn.execute("SELECT status FROM jobs WHERE id=?", (body["job_id"],)).fetchone()
            if state == "confirmed" and row["status"] != "cancelled" and row["deadline"] > self.clock() and chat and chat["status"] not in ("failed", "cancelled"):
                raise VersionConflict("confirmed interaction is still generating")
            # Fence generation before any refund. This also recovers a process
            # lost after cancellation but before its external ledger refund.
            body["payments"][source["id"]] = "reconciling"
            self._save_turn(conn, row, body, status="cancelled", generation=row["generation"] + 1)
            conn.execute("UPDATE jobs SET status='cancelled',lease_token=NULL,lease_until=NULL WHERE id=?", (body["job_id"],))
            return {**command, "turn_id": row["id"]}

    def fact_page(self, owner, *, cursor=0, since=0, limit=200):
        if cursor < 0 or since < 0:
            raise ValueError("sync cursors must be non-negative")
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            result = []
            next_cursor = cursor
            for event in conn.execute("SELECT * FROM events WHERE owner=? AND seq>? ORDER BY seq", (key, cursor)):
                next_cursor = event["seq"]
                value = self.cipher.decrypt(Domain.EVENT, key, event["id"], event["body"])
                timestamp = int(value["created_at"]*1000)
                if value["action"].startswith("reply_") or timestamp < since:
                    continue
                fact_row, fact = self._fact(conn, key, value["entity_id"])
                result.append({"factId": value["entity_id"], "userId": owner, "revision": fact_row["revision"], "revisionId": fact.get("revision_id"), "eventRevision": value["revision"], "eventId": event["id"], "sequence": event["seq"], "action": value["action"], "enabled": fact["enabled"], "fact": fact["text"] if fact["enabled"] else "", "timestamp": int(value["created_at"]*1000), "score": fact.get("weight", 0)/10, "stage": fact.get("stage", "candidate"), "truthStatus": fact.get("truth_status", "pending"), "revisionStatus": fact.get("revision_status", "active"), "modelPermission": fact.get("model_permission", "blocked"), "permissionVersion": fact.get("permission_version", 1), "temporalType": fact.get("temporal_type"), "temporalValidity": fact.get("temporal_validity") if fact["enabled"] else None})
                if len(result) >= max(1,min(limit,500)):
                    break
            more = conn.execute("SELECT 1 FROM events WHERE owner=? AND seq>? LIMIT 1", (key,next_cursor)).fetchone() is not None
            return {"items": result, "nextCursor": next_cursor, "hasMore": more}

    def fact_items(self, owner, *, since=0, limit=200):
        return self.fact_page(owner, since=since, limit=limit)["items"]
