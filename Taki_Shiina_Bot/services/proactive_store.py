"""Durable proactive topic state and inbox transactions."""
from datetime import datetime
import os

from services.contracts import Domain, new_id
from services.daily_policy import TOKYO
from services.foundation_store import PermissionDenied, VersionConflict
from services.memory_policy import timestamp
from services.owner_fence import serialized_owner
from services.proactive_policy import eligible, opportunity_due, period_keys, state_after_send, state_after_user_message
from services.public_policy import PUBLIC_POLICY_VERSION, PROMPT_VERSIONS
from services.reply_check import reply_digest


def proactive_enabled():
    return os.getenv("PROACTIVE_MESSAGES_ENABLED", "1") == "1"


class ProactiveMixin:
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        with self._transaction() as conn:
            conn.execute("CREATE TABLE IF NOT EXISTS proactive_state (owner TEXT PRIMARY KEY, body BLOB NOT NULL)")
            conn.execute("CREATE TABLE IF NOT EXISTS proactive_topics (id TEXT PRIMARY KEY,owner TEXT NOT NULL,candidate_id TEXT NOT NULL UNIQUE,day_key TEXT NOT NULL,week_key TEXT NOT NULL,body BLOB NOT NULL)")
            conn.execute("CREATE INDEX IF NOT EXISTS proactive_topics_owner_week ON proactive_topics(owner,week_key)")

    def _proactive_state(self, conn, key):
        row = conn.execute("SELECT body FROM proactive_state WHERE owner=?", (key,)).fetchone()
        if row:
            return self.cipher.decrypt(Domain.DERIVED, key, "proactive-state", row["body"])
        return {"enabled": True, "unanswered": 0, "last_sent_at": None, "version": 0}

    def _save_proactive_state(self, conn, key, state):
        body = self.cipher.encrypt(Domain.DERIVED, key, "proactive-state", state)
        conn.execute("INSERT INTO proactive_state VALUES (?,?) ON CONFLICT(owner) DO UPDATE SET body=excluded.body", (key, body))

    def _proactive_counts(self, conn, key, now):
        day, week = period_keys(now)
        day_key = self.cipher.index("proactive-day", key, day)
        week_key = self.cipher.index("proactive-week", key, week)
        daily = conn.execute("SELECT COUNT(*) FROM proactive_topics WHERE owner=? AND day_key=?", (key, day_key)).fetchone()[0]
        weekly = conn.execute("SELECT COUNT(*) FROM proactive_topics WHERE owner=? AND week_key=?", (key, week_key)).fetchone()[0]
        return daily, weekly, day_key, week_key

    def proactive_status(self, owner):
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            state = self._proactive_state(conn, key)
            daily, weekly, _, _ = self._proactive_counts(conn, key, self.clock())
            return {field: state[field] for field in ("enabled", "unanswered", "last_sent_at", "version")} | {
                    "daily_used": daily, "weekly_used": weekly,
                    "globally_enabled": proactive_enabled(),
                    "eligible_now": proactive_enabled() and eligible(self.clock(), state, daily, weekly)}

    @serialized_owner()
    def set_proactive_enabled(self, owner, enabled):
        if type(enabled) is not bool:
            raise ValueError("enabled must be boolean")
        key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            state = self._proactive_state(conn, key)
            if state["enabled"] != enabled:
                state = {**state, "enabled": enabled, "version": state["version"] + 1}
                self._save_proactive_state(conn, key, state)
                if not enabled:
                    self._silence_proactive(conn, key)
                self._change(conn, key, "proactive-settings", state["version"], "proactive_settings_changed", ("sync",))
            daily, weekly, _, _ = self._proactive_counts(conn, key, self.clock())
            return {field: state[field] for field in ("enabled", "unanswered", "last_sent_at", "version")} | {
                    "daily_used": daily, "weekly_used": weekly}

    def _silence_proactive(self, conn, key):
        for topic in conn.execute("SELECT id FROM proactive_topics WHERE owner=?", (key,)).fetchall():
            row, body = self._turn(conn, topic["id"])
            self._silence_proactive_topic(conn, row, body)

    def _silence_proactive_topic(self, conn, row, body):
        if body.get("delivery_completed_at") or body.get("proactive_silent"):
            return
        body["proactive_silent"] = True
        self._save_turn(conn, row, body)
        delivery_id = (body.get("result") or {}).get("deliveryId")
        if delivery_id:
            conn.execute("UPDATE jobs SET status='cancelled',lease_token=NULL,lease_until=NULL WHERE id=(SELECT job_id FROM outbox WHERE id=?)", (delivery_id,))

    def _on_real_user_input(self, conn, key, commands):
        if not any(command.intent == "chat" for command in commands):
            return
        state = self._proactive_state(conn, key)
        disable = any(command.text.strip() in {"关闭主动消息", "不要再主动发消息", "停止主动消息"}
                      for command in commands if command.intent == "chat")
        real_reply = any(command.intent == "chat" and command.text.strip() not in
                         {"关闭主动消息", "不要再主动发消息", "停止主动消息"} for command in commands)
        if disable and state["enabled"]:
            state = {**state, "enabled": False, "version": state["version"] + 1}
            self._change(conn, key, "proactive-settings", state["version"], "proactive_settings_changed", ("sync",))
        if real_reply and state["unanswered"]:
            state = state_after_user_message(state)
        if disable or real_reply:
            self._save_proactive_state(conn, key, state)
        self._silence_proactive(conn, key)

    def _proactive_source(self, conn, key, now, excluded_source=None):
        used = set()
        for row in conn.execute("SELECT id,body FROM proactive_topics WHERE owner=?", (key,)):
            topic = self.cipher.decrypt(Domain.DERIVED, key, row["id"], row["body"])
            used.add((topic["source_id"], topic["source_revision"]))
        referenced = set()
        for row in conn.execute("SELECT event_id,turn_id,body FROM daily_references WHERE owner=?", (key,)):
            value = self.cipher.decrypt(Domain.CHARACTER_DAILY, key, row["turn_id"] + ":" + row["event_id"], row["body"])
            turn, turn_body = self._turn(conn, row["turn_id"])
            if not value.get("revoked") and turn["status"] == "completed" and turn_body.get("dispatched_at"):
                referenced.add((row["event_id"], value["revision"]))
        choices = []
        today_tokyo = datetime.fromtimestamp(now, TOKYO).date().isoformat()
        for row in conn.execute("SELECT * FROM facts WHERE owner=? AND domain IN (?,?)", (key, Domain.CHARACTER_DAILY.value, Domain.USER_FACT.value)):
            _, data = self._fact(conn, key, row["id"])
            if (row["id"] == excluded_source or not self._model_eligible(conn, key, row, data)
                    or (row["id"], row["revision"]) in used):
                continue
            kind = None
            if row["domain"] == Domain.CHARACTER_DAILY.value:
                if data.get("recorded_character_date") != today_tokyo:
                    continue
                if any(event_id == row["id"] and revision < row["revision"] for event_id, revision in referenced):
                    kind = "conversation_followup"
                elif data.get("status") in {"in_progress", "completed"}:
                    kind = "authored_daily"
            elif (data.get("subject") == "user" and data.get("stage") == "formal"
                  and data.get("temporal_type") == "plan"):
                validity = data.get("temporal_validity") or {}
                until = timestamp(validity.get("until"))
                if until is not None and now <= until:
                    kind = "user_plan"
            if kind:
                choices.append(({"conversation_followup": 0, "authored_daily": 1, "user_plan": 2}[kind], row["id"], kind, row, data))
        if not choices:
            return None
        _, _, kind, row, data = min(choices, key=lambda item: (item[0], item[1]))
        ref = {"fact_id": row["id"], "revision": row["revision"], "revision_id": data["revision_id"],
               "permission_version": data["permission_version"], "domain": row["domain"],
               "subject": data.get("subject"), "text": data["text"], "temporal_type": data["temporal_type"],
               "temporal_validity": data.get("temporal_validity"), "source": data.get("source")}
        if row["domain"] == Domain.CHARACTER_DAILY.value:
            ref["event"] = {field: data.get(field) for field in ("event_revision", "event_type", "object", "status", "outcome", "scheduled_at", "occurred_at", "character_date", "timezone")}
        return {"kind": kind, "ref": ref}

    def _proactive_snapshot(self, conn, owner):
        self._require_ready(conn)
        key = self.cipher.index("owner", owner)
        now = self.clock()
        state = self._proactive_state(conn, key)
        daily, weekly, _, _ = self._proactive_counts(conn, key, now)
        if not proactive_enabled() or not eligible(now, state, daily, weekly):
            return None
        if conn.execute("SELECT 1 FROM chat_turns WHERE owner=? AND status IN ('pending','running')", (key,)).fetchone():
            return None
        latest = conn.execute("SELECT MAX(rowid) FROM requests WHERE owner=?", (key,)).fetchone()[0] or 0
        recent = conn.execute("SELECT body,id FROM requests WHERE owner=? ORDER BY rowid DESC LIMIT 1", (key,)).fetchone()
        if recent:
            source = self.cipher.decrypt(Domain.SOURCE_MESSAGE, key, recent["id"], recent["body"])
            if now - source.get("received_at", 0) < 30 * 60:
                return None
        reason = self._proactive_source(conn, key, now, state.get("last_source_id") if state["unanswered"] else None)
        if reason is None:
            return None
        epoch, _ = self._permission_snapshot(conn, key)
        return {"owner": owner, "at": now, "day_week": period_keys(now), "state_version": state["version"],
                "latest_request": latest, "permission_epoch": epoch, "lore_version": self._lore_head(conn, key),
                "reason": reason}

    def proactive_snapshot(self, owner):
        with self._transaction(write=False) as conn:
            return self._proactive_snapshot(conn, owner)

    def _assert_proactive_snapshot(self, conn, snapshot):
        current = self._proactive_snapshot(conn, snapshot["owner"])
        if current is None or any(current[field] != snapshot[field] for field in
                                  ("day_week", "state_version", "latest_request", "permission_epoch", "lore_version", "reason")):
            raise VersionConflict("proactive candidate is no longer current")
        key = self.cipher.index("owner", snapshot["owner"])
        self._validate_memories(conn, key, [snapshot["reason"]["ref"]])
        return key

    def assert_proactive_snapshot(self, snapshot, job=None):
        with self._transaction(write=False) as conn:
            key = self._assert_proactive_snapshot(conn, snapshot)
            if job and self._lease(conn, job)["owner"] != key:
                raise PermissionDenied("proactive job owner mismatch")

    @serialized_owner("job")
    def commit_proactive(self, job, snapshot, result):
        with self._transaction() as conn:
            key = self._assert_proactive_snapshot(conn, snapshot)
            lease = self._lease(conn, job)
            candidate_id = job["payload"]["input"]["candidate_id"]
            if lease["owner"] != key or job["payload"].get("kind") != "proactive":
                raise PermissionDenied("proactive job mismatch")
            if not isinstance(result.get("reply"), str) or not result["reply"].strip() or result.get("timerInstruction"):
                raise PermissionDenied("proactive message must be nonempty and contain no reminder")
            self._require_complete_check(result)
            review = result.get("proactive_review") or {}
            if review.get("approved") is not True or review.get("digest") != reply_digest(result) or review.get("policy_version") != PUBLIC_POLICY_VERSION or review.get("prompt_versions") != PROMPT_VERSIONS:
                raise PermissionDenied("proactive content lacks its current review")
            now = self.clock()
            state = self._proactive_state(conn, key)
            _, _, day_key, week_key = self._proactive_counts(conn, key, now)
            topic_id, message_id = new_id(), new_id()
            result = {**result, "messageId": message_id, "topicId": topic_id, "messageKind": "proactive",
                      "timestamp": int(now * 1000), "requestIds": [], "contentType": "text", "notificationText": "立希发来一条消息"}
            turn_body = {"owner": snapshot["owner"], "sources": [], "result": result, "proactive": True,
                         "permission_epoch": snapshot["permission_epoch"], "lore_version": snapshot["lore_version"],
                         "time_state": self._time_state(), "delivery_memory_refs": [snapshot["reason"]["ref"]]}
            conn.execute("INSERT INTO chat_turns VALUES (?,?, 'completed',1,?,?,?)", (topic_id, key, now, now,
                         self.cipher.encrypt(Domain.DERIVED, key, topic_id, turn_body)))
            item = {"messageId": message_id, "topicId": topic_id, "messageKind": "proactive", "userId": snapshot["owner"],
                    "role": "bot", "content": result["reply"], "timestamp": result["timestamp"], "contentType": "text"}
            conn.execute("INSERT INTO chat_messages(id,owner,turn_id,body) VALUES (?,?,?,?)", (message_id, key, topic_id,
                         self.cipher.encrypt(Domain.SOURCE_MESSAGE, key, message_id, item)))
            result["eventId"] = self._change(conn, key, topic_id, 1, "proactive_committed", ("chat",))
            result["deliveryId"] = conn.execute("SELECT id FROM outbox WHERE event_id=?", (result["eventId"],)).fetchone()[0]
            turn_body["result"] = result
            conn.execute("UPDATE chat_turns SET body=? WHERE id=?", (self.cipher.encrypt(Domain.DERIVED, key, topic_id, turn_body), topic_id))
            topic = {"source_id": snapshot["reason"]["ref"]["fact_id"], "source_revision": snapshot["reason"]["ref"]["revision"],
                     "kind": snapshot["reason"]["kind"], "sent_at": now, "message_id": message_id}
            conn.execute("INSERT INTO proactive_topics VALUES (?,?,?,?,?,?)", (topic_id, key, candidate_id, day_key, week_key,
                         self.cipher.encrypt(Domain.DERIVED, key, topic_id, topic)))
            self._save_proactive_state(conn, key, {**state_after_send(state, now),
                                                   "last_source_id": snapshot["reason"]["ref"]["fact_id"]})
            conn.execute("UPDATE jobs SET status='succeeded',lease_token=NULL,lease_until=NULL WHERE id=?", (job["job_id"],))
            return result

    def schedule_proactive(self, owner):
        now = self.clock()
        if not proactive_enabled() or not opportunity_due(owner, now):
            return None
        day, _ = period_keys(now)
        return self.enqueue(owner, "proactive", day, {"owner": owner, "day": day, "candidate_id": new_id()}, max_attempts=1)
