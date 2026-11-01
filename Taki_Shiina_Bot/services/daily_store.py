"""Encrypted daily events, atomic reviewed commits and separate reference audit.

Mixed into the reliable store so event changes share the existing permission,
lease, revision and dispatch fences. No model call occurs in a SQL transaction.
"""
import os
from datetime import datetime, timezone

from services.contracts import Domain, new_id
from services.daily_policy import DailyRejected, RULE_VERSIONS, local_time, time_context, validate_candidate, validate_approval
from services.foundation_store import PermissionDenied, VersionConflict
from services.memory_policy import relevance, timestamp
from services.owner_fence import serialized_owner
from services.privacy import scrub_locations
from services.input_budget import MEMORY_BYTES, json_bytes


def daily_enabled():
    return os.getenv("CHARACTER_DAILY_ENABLED", "0") == "1"


def daily_limit():
    value = int(os.getenv("CHARACTER_DAILY_MAX_PER_DAY", "2"))
    if not 0 <= value <= 8:
        raise ValueError("daily trial limit must be between 0 and 8")
    return value


class DailyEventMixin:
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        with self._transaction() as conn:
            conn.execute("CREATE TABLE IF NOT EXISTS daily_commits (owner TEXT NOT NULL, day_key TEXT NOT NULL, slot_key TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(owner,day_key,slot_key))")
            conn.execute("CREATE TABLE IF NOT EXISTS daily_references (owner TEXT NOT NULL, turn_id TEXT NOT NULL, event_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(owner,turn_id,event_id))")

    def _write_fact(self, conn, owner, fact_id, domain, revision, data):
        if domain == Domain.CHARACTER_DAILY:
            # Only the reviewed commit below may change daily content. The
            # generic memory API can still disable/restore an existing event.
            row = conn.execute("SELECT id FROM facts WHERE owner=? AND id=?", (owner, fact_id)).fetchone()
            if not row:
                raise DailyRejected("daily content requires candidate review and commit")
            _, old = self._fact(conn, owner, fact_id)
            permission_fields = {"enabled", "model_permission", "permission_version"}
            if {k: v for k, v in old.items() if k not in permission_fields} != {k: v for k, v in data.items() if k not in permission_fields}:
                raise DailyRejected("generic fact writes cannot rewrite daily content")
        return super()._write_fact(conn, owner, fact_id, domain, revision, data)

    def _model_eligible(self, conn, owner, row, data):
        if row["domain"] != Domain.CHARACTER_DAILY:
            return super()._model_eligible(conn, owner, row, data)
        return (data.get("origin") == "authored_daily" and data.get("evidence_type") == "authored_daily"
                and data.get("subject") == "character" and data.get("enabled") is True
                and data.get("model_permission") == "allowed" and data.get("truth_status") == "accepted"
                and data.get("revision_status") == "active" and data.get("review", {}).get("approved") is True
                and data.get("event_state") in {"planned", "in_progress", "completed", "cancelled"})

    def _daily_heads(self, conn, key):
        return [[row["id"], row["revision"]] for row in conn.execute("SELECT id,revision FROM facts WHERE owner=? AND domain=? ORDER BY id", (key, Domain.CHARACTER_DAILY.value))]

    def _daily_sources(self, conn, key, source_ids):
        result = []
        for identity in source_ids:
            row = conn.execute("SELECT * FROM requests WHERE owner=? AND id=?", (key, identity)).fetchone()
            if row is None:
                raise PermissionDenied("real daily source is unavailable for this owner")
            source = self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, identity, row["body"])
            result.append({"source_id": identity, "text": source["text"], "intent": source.get("intent", "chat"), "origin": "authenticated_user"})
        return result

    def daily_snapshot(self, owner, *, source_ids=()):
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            epoch, legacy_allowed = self._permission_snapshot(conn, key)
            if source_ids and not legacy_allowed:
                raise PermissionDenied("unmapped historical chat cannot bypass blocked memory")
            events, lore = [], []
            for row in conn.execute("SELECT * FROM facts WHERE owner=? AND domain IN (?,?) ORDER BY id", (key, Domain.CHARACTER_DAILY.value, Domain.OFFICIAL_LORE.value)):
                _, data = self._fact(conn, key, row["id"])
                if not self._model_eligible(conn, key, row, data):
                    continue
                if row["domain"] == Domain.OFFICIAL_LORE:
                    lore.append({"fact_id": row["id"], "revision": row["revision"], "text": data["text"], "lore_version": data["lore_version"]})
                else:
                    events.append({"event_id": row["id"], "revision": row["revision"], **{field: data[field] for field in ("event_type", "participants", "object", "status", "summary", "scheduled_at", "occurred_at", "outcome", "character_date")}})
            now = self.clock()
            return {"owner": owner, "now": now, "time": time_context(now), "events": events,
                    "heads": self._daily_heads(conn, key), "permission_epoch": epoch,
                    "lore_version": self._lore_head(conn, key), "official_lore": lore,
                    "real_sources": self._daily_sources(conn, key, source_ids), "rule_versions": dict(RULE_VERSIONS)}

    def _assert_daily_snapshot(self, conn, snapshot):
        self._require_ready(conn)
        key = self.cipher.index("owner", snapshot["owner"])
        epoch, _ = self._permission_snapshot(conn, key)
        if (snapshot["rule_versions"] != RULE_VERSIONS or snapshot["heads"] != self._daily_heads(conn, key)
                or snapshot["permission_epoch"] != epoch or snapshot["lore_version"] != self._lore_head(conn, key)
                or snapshot["time"]["character_date"] != time_context(self.clock())["character_date"]):
            raise VersionConflict("daily source, rule, date or event revision changed")
        if snapshot["real_sources"] != self._daily_sources(conn, key, [s["source_id"] for s in snapshot["real_sources"]]):
            raise VersionConflict("daily real source changed")
        return key

    def assert_daily_snapshot(self, snapshot, job=None):
        with self._transaction(write=False) as conn:
            key = self._assert_daily_snapshot(conn, snapshot)
            if job and self._lease(conn, job)["owner"] != key:
                raise PermissionDenied("daily job owner mismatch")
            if not daily_enabled():
                raise DailyRejected("new authored daily events are disabled")

    @serialized_owner()
    def commit_daily(self, owner, candidate, snapshot, approval, *, slot, job=None):
        if owner != snapshot["owner"] or not isinstance(slot, str) or not slot or len(slot) > 100:
            raise DailyRejected("daily commit identity mismatch")
        validate_candidate(candidate, snapshot)
        validate_approval(candidate, snapshot, approval)
        if candidate != scrub_locations(candidate):
            raise DailyRejected("daily candidate must be sanitized before review")
        with self._transaction() as conn:
            key = self._assert_daily_snapshot(conn, snapshot)
            if not daily_enabled():
                raise DailyRejected("new authored daily events are disabled")
            if job:
                lease = self._lease(conn, job)
                payload = job["payload"].get("input", {})
                if lease["owner"] != key or job["payload"].get("kind") != "daily" or payload.get("slot") != slot or payload.get("character_date") != snapshot["time"]["character_date"]:
                    raise PermissionDenied("daily commit does not match its durable job")
            day_key = self.cipher.index("daily-day", key, snapshot["time"]["character_date"])
            slot_key = self.cipher.index("daily-slot", key, slot)
            if conn.execute("SELECT 1 FROM daily_commits WHERE owner=? AND day_key=? AND slot_key=?", (key, day_key, slot_key)).fetchone():
                raise VersionConflict("daily opportunity already committed")
            count = conn.execute("SELECT count(*) FROM daily_commits WHERE owner=? AND day_key=?", (key, day_key)).fetchone()[0]
            if count >= daily_limit():
                raise DailyRejected("daily trial maximum reached")
            identity, expected = candidate["event_id"], candidate["expected_revision"]
            old = conn.execute("SELECT * FROM facts WHERE id=?", (identity,)).fetchone()
            if old and (old["owner"] != key or old["domain"] != Domain.CHARACTER_DAILY.value):
                raise PermissionDenied("daily identity belongs to another owner or domain")
            if (old["revision"] if old else 0) != expected:
                raise VersionConflict("daily event revision changed")
            if old:
                _, previous = self._fact(conn, key, identity)
                if not self._model_eligible(conn, key, old, previous):
                    raise PermissionDenied("daily continuation cannot restore a blocked event")
            revision = expected + 1
            occurrence = candidate["occurred_at"]
            event_time = occurrence or candidate["scheduled_at"]
            times = {}
            for field in ("scheduled_at", "occurred_at"):
                at = timestamp(candidate[field])
                times[field + "_utc"] = datetime.fromtimestamp(at, timezone.utc).isoformat() if at is not None else None
                times[field + "_local"] = local_time(at).isoformat() if at is not None else None
            data = {**candidate, "event_id": identity, "event_revision": revision,
                    **times,
                    "revision_id": new_id(),
                    "permission_version": previous["permission_version"] if old else 1,
                    "event_state": candidate["status"], "text": candidate["summary"],
                    "character_date": local_time(timestamp(event_time)).date().isoformat(),
                    "recorded_character_date": snapshot["time"]["character_date"],
                    "timezone": "Asia/Tokyo", "recorded_at_utc": time_context(self.clock())["utc"],
                    "origin": "authored_daily", "evidence_type": "authored_daily", "source": "authored_daily",
                    "subject": "character", "enabled": True, "model_permission": "allowed",
                    "truth_status": "accepted", "revision_status": "active", "stage": "formal", "weight": 0,
                    "temporal_type": "plan" if candidate["status"] == "planned" else "past_event",
                    "temporal_validity": {"from": occurrence, "until": None, "timezone": "Asia/Tokyo", "precision": "event"},
                    "rule_versions": dict(RULE_VERSIONS), "review": approval}
            # Bypass only this mixin's generic-write guard, retaining encryption,
            # revision history and all shared memory permission validation.
            super()._write_fact(conn, key, identity, Domain.CHARACTER_DAILY, revision, data)
            self._change(conn, key, identity, revision, "daily_committed", ("sync",))
            body = {"event_id": identity, "revision": revision, "at": self.clock()}
            conn.execute("INSERT INTO daily_commits VALUES (?,?,?,?)", (key, day_key, slot_key, self.cipher.encrypt(Domain.CHARACTER_DAILY, key, day_key + ":" + slot_key, body)))
            if job:
                conn.execute("UPDATE jobs SET status='succeeded',lease_token=NULL,lease_until=NULL WHERE id=?", (job["job_id"],))
            return {"event_id": identity, "revision": revision}

    def select_memories(self, owner, query, *, limit=12, domain=None, byte_limit=MEMORY_BYTES):
        refs = super().select_memories(owner, query, limit=limit, domain=domain, byte_limit=byte_limit)
        if domain is not None and domain != Domain.CHARACTER_DAILY:
            return refs
        # Include the authoritative state and time even when the base lexical
        # selector already matched this event. Never send only its summary.
        refs = [r for r in refs if r["domain"] != Domain.CHARACTER_DAILY]
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            today = time_context(self.clock())["character_date"]
            last = None
            for ref in conn.execute("SELECT * FROM daily_references WHERE owner=? ORDER BY rowid DESC", (key,)):
                value = self.cipher.decrypt(Domain.CHARACTER_DAILY, key, ref["turn_id"] + ":" + ref["event_id"], ref["body"])
                if not value.get("revoked"):
                    last = ref
                    break
            followup = any(word in query for word in ("那段", "后来", "然后呢", "下午那", "昨天那", "挂包上"))
            current = any(word in query for word in ("在干嘛", "在忙什么", "做什么", "今天怎么样"))
            for row in conn.execute("SELECT * FROM facts WHERE owner=? AND domain=?", (key, Domain.CHARACTER_DAILY.value)):
                _, data = self._fact(conn, key, row["id"])
                if not self._model_eligible(conn, key, row, data):
                    continue
                rank = relevance(query, data["text"] + data["object"])
                if current and (data["character_date"] == today or data["status"] in {"planned", "in_progress"}):
                    rank = max(rank, 2)
                if followup and last and row["id"] == last["event_id"]:
                    rank = max(rank, 4)
                if not rank:
                    continue
                refs.append({"fact_id": row["id"], "revision": row["revision"], "revision_id": data["revision_id"],
                             "permission_version": data["permission_version"], "domain": Domain.CHARACTER_DAILY.value,
                             "subject": "character", "text": data["text"], "temporal_type": data["temporal_type"],
                             "temporal_validity": data["temporal_validity"], "rank": rank, "weight": 0,
                             "source": "authored_daily", "lore_version": None, "event_id": row["id"],
                             "event_state": data["status"], "event": {k: data[k] for k in (
                                 "event_revision", "event_type", "participants", "object", "status", "outcome", "scheduled_at", "occurred_at", "scheduled_at_utc", "scheduled_at_local", "occurred_at_utc", "occurred_at_local", "character_date", "timezone", "previous_refs", "chat_refs", "rule_versions")}})
        selected, size = [], 2
        for ref in sorted(refs, key=lambda r: (-r["rank"], -r["weight"], r["fact_id"]))[:limit]:
            cost = json_bytes(ref) + 2
            if byte_limit is None or size + cost <= byte_limit:
                selected.append(ref)
                size += cost
        return selected

    def _commit_memory_usage(self, conn, owner, turn_id, refs):
        super()._commit_memory_usage(conn, owner, turn_id, refs)
        for ref in refs:
            if ref["domain"] == Domain.CHARACTER_DAILY:
                body = {"revision": ref["revision"], "at": self.clock(), "phase": "reply_committed"}
                conn.execute("INSERT OR IGNORE INTO daily_references VALUES (?,?,?,?)", (owner, turn_id, ref["fact_id"], self.cipher.encrypt(Domain.CHARACTER_DAILY, owner, turn_id + ":" + ref["fact_id"], body)))

    def daily_references(self, owner, event_id):
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            return [{"turn_id": row["turn_id"], **self.cipher.decrypt(Domain.CHARACTER_DAILY, key, row["turn_id"] + ":" + event_id, row["body"])} for row in conn.execute("SELECT * FROM daily_references WHERE owner=? AND event_id=? ORDER BY rowid", (key, event_id))]

    def schedule_daily(self, owner):
        limit = daily_limit()
        now = local_time(self.clock())
        if not daily_enabled() or not limit or not 7 <= now.hour < 23:
            return None
        slot = str(min(limit - 1, (now.hour - 7) * limit // 16))
        day = now.date().isoformat()
        return self.enqueue(owner, "daily", day + ":" + slot,
                            {"owner": owner, "character_date": day, "slot": slot}, max_attempts=1)

    def _revoke_daily_references(self, conn, row, reason):
        for ref in conn.execute("SELECT * FROM daily_references WHERE owner=? AND turn_id=?", (row["owner"], row["id"])).fetchall():
            identity = row["id"] + ":" + ref["event_id"]
            value = self.cipher.decrypt(Domain.CHARACTER_DAILY, row["owner"], identity, ref["body"])
            value.update(revoked=True, reason=reason)
            conn.execute("UPDATE daily_references SET body=? WHERE owner=? AND turn_id=? AND event_id=?", (self.cipher.encrypt(Domain.CHARACTER_DAILY, row["owner"], identity, value), row["owner"], row["id"], ref["event_id"]))

    def daily_known_owners(self):
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            return [self.cipher.decrypt(Domain.DERIVED, row["owner"], "owner", row["body"])["owner"] for row in conn.execute("SELECT * FROM chat_owners")]
