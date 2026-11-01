"""Bind model semantics to trusted candidate versions and relative-day windows."""
from datetime import datetime, timedelta, timezone

from services.memory_policy import timestamp


def relative_day_windows(text, now):
    """A day window limits current use; it does not assert recovery/completion."""
    if now is None:
        return {}
    start = datetime.fromtimestamp(timestamp(now), timezone(timedelta(hours=8))).replace(
        hour=0, minute=0, second=0, microsecond=0)
    return {
        expression: {"from": (start + timedelta(days=offset)).isoformat(),
                     "until": (start + timedelta(days=offset + 1)).isoformat(),
                     "expression": expression, "timezone": "Asia/Shanghai", "precision": "day"}
        for expression, offset in (("今天", 0), ("明天", 1)) if expression in text
    }


def bind_extractions(payload, candidates, text, windows):
    # Keep the existing list-returning entry point and legacy array responses.
    facts = payload.get("facts") if isinstance(payload, dict) else payload
    if not isinstance(facts, list):
        raise ValueError("fact extraction must contain a facts array")
    allowed = {candidate["fact_id"]: candidate for candidate in candidates}
    bound = []
    for item in facts:
        if isinstance(item, str):
            bound.append(item)  # Legacy strings remain unverified/pending in storage.
            continue
        if not isinstance(item, dict):
            raise ValueError("invalid extracted fact")
        item = dict(item)
        target = item.get("fact_id")
        if target:
            if target not in allowed:
                raise ValueError("extracted fact references an unavailable candidate")
            # The model selects an identity, never its CAS version. The store
            # still rejects this snapshot version if it changed during the call.
            item["expected_revision"] = allowed[target]["revision"]
        else:
            item.pop("expected_revision", None)
        validity = item.get("temporal_validity")
        evidence = item.get("evidence")
        if (isinstance(validity, dict) and isinstance(evidence, str) and evidence and evidence in text
                and item.get("subject") == "user" and item.get("assertion") == "explicit"
                and item.get("temporal_type") in ("plan", "current_state")
                and (not target or item.get("operation") == "change")
                and validity.get("precision") != "ongoing" and validity.get("until") is None):
            expression = validity.get("expression")
            window = windows.get(expression) if isinstance(expression, str) else None
            mentioned = [day for day in windows if day in evidence]
            if window and mentioned == [expression] and (
                    validity.get("from") is None or timestamp(validity["from"]) == timestamp(window["from"])):
                item["temporal_validity"] = {**validity, **window}
        bound.append(item)
    return bound
