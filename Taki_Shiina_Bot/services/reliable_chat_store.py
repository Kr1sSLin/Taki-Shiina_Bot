"""Durable text-chat application transactions over the foundation repository."""
import base64
import binascii
from dataclasses import asdict
import hashlib
import os

from secure_storage import DataCorruptionError
from services.contracts import ChatCommand, Domain, new_id
from services.foundation_store import FoundationStore, PermissionDenied, VersionConflict
from services.privacy import scrub_locations
from services.vision_limits import MAX_INLINE_IMAGE_BYTES


class ReliableChatStore(FoundationStore):
    def __init__(self, *args, **kwargs):
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

    def accept(self, commands: list[ChatCommand], *, delay=0, merge_pending=False):
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
            if merge_pending:
                candidates = conn.execute("SELECT * FROM chat_turns WHERE owner=? AND status='pending' AND deadline>? ORDER BY created DESC,rowid DESC", (owner, now))
                for candidate in candidates:
                    row, candidate_body = self._turn(conn, candidate["id"])
                    if incoming_interactions and candidate_body.get("payments"):
                        continue
                    job = conn.execute("SELECT status FROM jobs WHERE id=?", (candidate_body["job_id"],)).fetchone()
                    if candidate_body.get("merge_pending") and job and job["status"] == "pending":
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
                        turn_row, turn_body = row, candidate_body
                        break
            if turn_row is None:
                turn_id = new_id()
                turn_body = {"owner": commands[0].owner, "sources": [], "merge_pending": merge_pending, "result": None, "image_count": 0, "image_bytes": 0}
                job_id = self._enqueue(conn, owner, "chat", turn_id, {"kind": "chat", "turn_id": turn_id, "dependencies": []})
                turn_body["job_id"] = job_id
                conn.execute("INSERT INTO chat_turns VALUES (?,?,'pending',1,?,?,?)", (turn_id, owner, now, now + 120, self.cipher.encrypt(Domain.DERIVED, owner, turn_id, turn_body)))
                turn_row, _ = self._turn(conn, turn_id)
            turn_id = turn_row["id"]
            seen = set()
            for command, request_key, client_key in pending:
                if request_key in seen:
                    raise VersionConflict("duplicate request within the same batch")
                seen.add(request_key)
                message_id = new_id()
                conn.execute("INSERT INTO requests VALUES (?,?,?,?,?,?)", (owner, request_key, client_key, message_id, turn_id, self.cipher.encrypt(Domain.SOURCE_MESSAGE, owner, message_id, asdict(command))))
                turn_body["sources"].append(message_id)
                turn_body["image_count"] += len((command.metadata or {}).get("images") or [])
                turn_body["image_bytes"] += sum(len(base64.b64decode(image["dataBase64"])) for image in ((command.metadata or {}).get("images") or []))
                self._enqueue(conn, owner, "extract", message_id, {"kind": "extract", "message_id": message_id, "turn_id": turn_id, "dependencies": []})
                if command.intent == "interaction":
                    turn_body.setdefault("payments", {})[message_id] = "pending"
                    recovery = self._enqueue(conn, owner, "interaction_reconcile", message_id, {"kind": "interaction_reconcile", "message_id": message_id, "turn_id": turn_id, "dependencies": []})
                    conn.execute("UPDATE jobs SET available=? WHERE id=?", (now + 15, recovery))
                receipts.append({"turn_id": turn_id, "message_id": message_id, "duplicate": False})
            self._save_turn(conn, turn_row, turn_body, generation=turn_row["generation"] + (1 if turn_row["body"] and merge_pending and len(turn_body["sources"]) > len(pending) else 0))
            conn.execute("UPDATE jobs SET available=? WHERE id=? AND status='pending'", (min(now + delay, turn_row["deadline"] - 1), turn_body["job_id"]))
            return receipts

    def _claim_allowed(self, conn, row, payload):
        if payload.get("kind") == "interaction_reconcile":
            turn, body = self._turn(conn, payload["turn_id"])
            chat = conn.execute("SELECT status FROM jobs WHERE id=?", (body["job_id"],)).fetchone()
            confirmed = body.get("payments", {}).get(payload["message_id"]) == "confirmed"
            return not (confirmed and turn["status"] not in ("completed", "cancelled") and turn["deadline"] > self.clock() and chat and chat["status"] not in ("failed", "cancelled"))
        if payload.get("kind") != "chat":
            return True
        turn, body = self._turn(conn, payload["turn_id"])
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

    def begin_turn(self, job):
        with self._transaction() as conn:
            self._require_ready(conn)
            lease = self._lease(conn, job)
            if job["payload"].get("kind") != "chat":
                raise PermissionDenied("job is not a chat generation")
            row, body = self._turn(conn, job["payload"]["turn_id"])
            if row["owner"] != lease["owner"] or row["status"] == "completed" or row["deadline"] <= self.clock():
                raise VersionConflict("turn is no longer current")
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
            epoch, legacy_allowed = self._permission_snapshot(conn, row["owner"])
            body["permission_epoch"] = epoch
            self._save_turn(conn, row, body, status="running")
            return {"turn_id": row["id"], "generation": row["generation"], "deadline": deadline, "owner": body["owner"], "sources": sources, "permission_epoch": epoch, "legacy_allowed": legacy_allowed}

    def assert_snapshot(self, snapshot):
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row, _ = self._turn(conn, snapshot["turn_id"])
            epoch, _ = self._permission_snapshot(conn, row["owner"])
            if row["owner"] != self.cipher.index("owner", snapshot["owner"]) or row["generation"] != snapshot["generation"] or epoch != snapshot["permission_epoch"] or row["deadline"] <= self.clock() or row["status"] != "running":
                raise VersionConflict("generation context is no longer current")

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
            if for_model and not allowed:
                return []
            rows = conn.execute("SELECT * FROM chat_messages WHERE owner=? ORDER BY seq DESC LIMIT ?", (key, max(1, min(limit, 3000)))).fetchall()
            return [self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, row["id"], row["body"]) for row in reversed(rows)]

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
            job = conn.execute("SELECT * FROM jobs WHERE id=?", (body["job_id"],)).fetchone()
            payload = self.cipher.decrypt(Domain.JOB, key, job["id"], job["body"])
            return {"status": row["status"], "job_status": job["status"], "deadline": row["deadline"], "result": body["result"], "failure_category": payload.get("failure_category", "generation")}

    def record_failure(self, job, category):
        if category not in ("generation", "storage"):
            raise ValueError("unknown failure category")
        with self._transaction() as conn:
            self._require_ready(conn)
            row = self._lease(conn, job)
            payload = dict(job["payload"], failure_category=category)
            conn.execute("UPDATE jobs SET body=? WHERE id=?", (self.cipher.encrypt(Domain.JOB, row["owner"], row["id"], payload), row["id"]))

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
            sources = []
            for identity in body["sources"]:
                source_row = conn.execute("SELECT * FROM requests WHERE id=? AND owner=?", (identity, row["owner"])).fetchone()
                if source_row is None:
                    raise PermissionDenied("turn source unavailable")
                sources.append(self.cipher.decrypt(Domain.SOURCE_MESSAGE, row["owner"], identity, source_row["body"]))
            if not isinstance(result.get("reply"), str) or not result["reply"].strip():
                raise ValueError("empty reply cannot be committed")
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
                conn.execute("INSERT INTO chat_messages(id,owner,turn_id,body) VALUES (?,?,?,?)", (identity, row["owner"], row["id"], self.cipher.encrypt(Domain.SOURCE_MESSAGE, row["owner"], identity, item)))
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
            self._save_turn(conn, row, body, status="completed")
            conn.execute("UPDATE jobs SET status='succeeded',lease_token=NULL,lease_until=NULL WHERE id=?", (job["job_id"],))
            return result

    def extraction_context_allowed(self, job):
        with self._transaction(write=False) as conn:
            row = self._lease(conn, job)
            if job["payload"].get("kind") != "extract":
                raise PermissionDenied("job is not an extraction")
            _, allowed = self._permission_snapshot(conn, row["owner"])
            return allowed

    def commit_extraction(self, job, facts):
        with self._transaction() as conn:
            self._require_ready(conn)
            row = self._lease(conn, job)
            if job["payload"].get("kind") != "extract":
                raise PermissionDenied("job is not an extraction")
            _, allowed = self._permission_snapshot(conn, row["owner"])
            if not allowed:
                # Unmapped old context is quarantined until explicit restoration.
                conn.execute("UPDATE jobs SET status='cancelled',lease_token=NULL,lease_until=NULL WHERE id=?", (job["job_id"],))
                return []
            identities = []
            for index, text in enumerate(facts[:20]):
                if not isinstance(text, str) or not text.strip():
                    continue
                identity = self.cipher.index("extracted-fact", job["payload"]["message_id"], str(index))
                data = {"text": scrub_locations(text.strip()), "enabled": True, "score": 0.3, "source": job["payload"]["message_id"], "truthfulness": "user_asserted", "subject": "user", "valid_from": None, "valid_until": None}
                self._write_fact(conn, row["owner"], identity, Domain.USER_FACT, 1, data)
                self._change(conn, row["owner"], identity, 1, "created", ("sync",))
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
                if value["action"] == "reply_completed" or timestamp < since:
                    continue
                fact_row, fact = self._fact(conn, key, value["entity_id"])
                result.append({"factId": value["entity_id"], "userId": owner, "revision": fact_row["revision"], "eventRevision": value["revision"], "eventId": event["id"], "sequence": event["seq"], "action": value["action"], "enabled": fact["enabled"], "fact": fact["text"] if fact["enabled"] else "", "timestamp": int(value["created_at"]*1000)})
                if len(result) >= max(1,min(limit,500)):
                    break
            more = conn.execute("SELECT 1 FROM events WHERE owner=? AND seq>? LIMIT 1", (key,next_cursor)).fetchone() is not None
            return {"items": result, "nextCursor": next_cursor, "hasMore": more}

    def fact_items(self, owner, *, since=0, limit=200):
        return self.fact_page(owner, since=since, limit=limit)["items"]
