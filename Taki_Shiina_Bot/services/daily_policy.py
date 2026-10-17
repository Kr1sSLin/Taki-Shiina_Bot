"""PRD-04 candidate schema and deterministic continuity checks.

Open-ended meaning is checked separately. A keyword hit is never treated as a
complete semantic judgment (official discussion, hypotheses and negation differ).
"""
from datetime import datetime, timedelta, timezone
import hashlib
import json
import re

from services.memory_policy import timestamp
from services.public_policy import PUBLIC_POLICY_VERSION, PERSONA_VERSION, RELATIONSHIP_VERSION
from services.reply_check import hard_violations

TOKYO = timezone(timedelta(hours=9), "Asia/Tokyo")
DAILY_RULE_VERSION = "04-v0.3-events-1"
DAILY_CHECKS = ("ordinary_scope", "relationship", "official_conflict", "continuity", "time", "user_evidence")
RULE_VERSIONS = {"public": PUBLIC_POLICY_VERSION, "persona": PERSONA_VERSION,
                 "relationship": RELATIONSHIP_VERSION, "daily": DAILY_RULE_VERSION}
EVENT_TYPES = {"practice", "composition", "shopping", "food", "work", "small_interaction", "ordinary_life"}
TRANSITIONS = {"planned": {"planned", "in_progress", "completed", "cancelled"},
               "in_progress": {"in_progress", "completed", "cancelled"},
               "completed": {"completed"}, "cancelled": {"cancelled"}}


class DailyRejected(ValueError):
    pass


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False, allow_nan=False).encode()).hexdigest()


def local_time(now):
    return datetime.fromtimestamp(now, TOKYO)


def time_context(now):
    local = local_time(now)
    hour = local.hour
    period = "morning" if 6 <= hour < 12 else "afternoon" if 12 <= hour < 18 else "evening" if 18 <= hour < 23 else "night"
    return {"utc": datetime.fromtimestamp(now, timezone.utc).isoformat(),
            "local": local.isoformat(), "character_date": local.date().isoformat(),
            "timezone": "Asia/Tokyo", "period": period}


def validate_candidate(candidate, snapshot):
    fields = {"event_id", "expected_revision", "event_type", "participants", "object", "status",
              "summary", "scheduled_at", "occurred_at", "outcome", "previous_refs", "chat_refs"}
    if not isinstance(candidate, dict) or set(candidate) != fields:
        raise DailyRejected("daily candidate schema is incomplete or has extra fields")
    if candidate["event_type"] not in EVENT_TYPES or candidate["status"] not in TRANSITIONS:
        raise DailyRejected("unsupported daily type or state")
    for key, bound in (("summary", 500), ("object", 100), ("event_id", 100)):
        if not isinstance(candidate[key], str) or not candidate[key].strip() or len(candidate[key]) > bound:
            raise DailyRejected("invalid daily text or identity")
    # Narrow declarative hard stops augment, never replace, semantic review.
    # Descriptions of discussion/negation/hypothesis do not match these anchors.
    summary = candidate["summary"].strip()
    if (re.match(r"^(?:我|立希|椎名立希|灯|爱音|乐奈|素世)(?:今天|刚刚|已经)?(?:死亡|去世|重病|患上绝症|发生严重事故)", summary)
            or re.match(r"^(?:MyGO!!!!!|MyGO|乐队)(?:今天|已经|正式)?解散了", summary)
            or (summary.startswith(("我们", "咱们", "做我的", "当我的")) and "relationship" in hard_violations(summary))):
        raise DailyRejected("major original assertion cannot be authored daily")
    revision = candidate["expected_revision"]
    if type(revision) is not int or revision < 0:
        raise DailyRejected("invalid daily revision")
    participants = candidate["participants"]
    if not isinstance(participants, list) or not 1 <= len(participants) <= 5 or any(not isinstance(p, str) or not p.strip() or len(p) > 40 for p in participants) or len(set(participants)) != len(participants) or "椎名立希" not in participants:
        raise DailyRejected("invalid participants")
    if candidate["outcome"] not in {"none", "not_bought", "bought"}:
        raise DailyRejected("invalid outcome")
    if candidate["event_type"] != "shopping" and candidate["outcome"] != "none":
        raise DailyRejected("shopping outcome on another event type")
    if candidate["outcome"] == "bought" and candidate["status"] != "completed":
        raise DailyRejected("purchase requires recorded completion")
    if candidate["status"] == "planned" and candidate["occurred_at"] is not None:
        raise DailyRejected("a plan cannot assert occurrence")
    for key in ("scheduled_at", "occurred_at"):
        value = candidate[key]
        if value is not None and not isinstance(value, str):
            raise DailyRejected("event time must be an aware ISO string")
        try:
            parsed = timestamp(value)
        except (ValueError, TypeError, OverflowError) as error:
            raise DailyRejected("invalid event time") from error
        if key == "occurred_at" and parsed is not None and parsed > snapshot["now"]:
            raise DailyRejected("future action cannot be recorded as occurring")
    if candidate["status"] == "planned" and candidate["scheduled_at"] is None:
        raise DailyRejected("plan requires its planned time")
    if candidate["status"] != "planned" and candidate["occurred_at"] is None:
        raise DailyRejected("non-plan state requires its occurrence time")
    old = next((e for e in snapshot["events"] if e["event_id"] == candidate["event_id"]), None)
    refs = candidate["previous_refs"]
    if not isinstance(refs, list) or any(not isinstance(r, dict) or set(r) != {"event_id", "revision"} or type(r["revision"]) is not int for r in refs):
        raise DailyRejected("invalid predecessor references")
    heads = {(e["event_id"], e["revision"]) for e in snapshot["events"]}
    if any((r["event_id"], r["revision"]) not in heads for r in refs):
        raise DailyRejected("predecessor is not an allowed current revision")
    if old:
        if revision != old["revision"] or {"event_id": old["event_id"], "revision": revision} not in refs:
            raise DailyRejected("continuation must cite its current event revision")
        for key in ("event_type", "participants", "object"):
            if candidate[key] != old[key]:
                raise DailyRejected("continuation changes event identity")
        if candidate["status"] not in TRANSITIONS[old["status"]]:
            raise DailyRejected("illegal event state transition")
        if old["outcome"] == "bought" and candidate["outcome"] != "bought":
            raise DailyRejected("purchase cannot be erased by a continuation")
        if old["occurred_at"] and candidate["occurred_at"] and timestamp(candidate["occurred_at"]) < timestamp(old["occurred_at"]):
            raise DailyRejected("event time goes backwards")
    elif revision:
        raise DailyRejected("unknown event revision")
    else:
        if candidate["occurred_at"] and local_time(timestamp(candidate["occurred_at"])).date().isoformat() != snapshot["time"]["character_date"]:
            raise DailyRejected("new daily events cannot invent a past day's activity")
        # An existing object's follow-up must use the same ID, even on a new day.
        if any(e["event_type"] == candidate["event_type"] and e["object"] == candidate["object"] for e in snapshot["events"]):
            raise DailyRejected("new identity duplicates an existing event")
    chat_refs = candidate["chat_refs"]
    if not isinstance(chat_refs, list):
        raise DailyRejected("invalid real chat references")
    sources = {s["source_id"]: s for s in snapshot["real_sources"]}
    for ref in chat_refs:
        if not isinstance(ref, dict) or set(ref) != {"source_id", "quote"} or not isinstance(ref["quote"], str) or not ref["quote"].strip() or ref["source_id"] not in sources or ref["quote"] not in sources[ref["source_id"]]["text"]:
            raise DailyRejected("shared experience lacks a real source quote")
    if ("user" in participants or "用户" in summary) and not chat_refs:
        raise DailyRejected("user participation requires actual chat evidence")
    return candidate


def validate_approval(candidate, snapshot, approval):
    if not isinstance(approval, dict) or approval.get("approved") is not True or approval.get("classification") != "ordinary" or approval.get("violations") != []:
        raise DailyRejected("daily semantic review did not approve an ordinary event")
    checks = approval.get("checks")
    if not isinstance(checks, dict) or any(checks.get(key) is not True for key in DAILY_CHECKS):
        raise DailyRejected("daily semantic review is incomplete or uncertain")
    if approval.get("digest") != digest({"candidate": candidate, "snapshot": snapshot}) or approval.get("rule_versions") != RULE_VERSIONS:
        raise DailyRejected("daily review is stale or does not bind this candidate")
