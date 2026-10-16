"""PRD-02 eligibility. Scores, evidence, time and permission are independent."""
from datetime import datetime, timezone
import math
import re
import unicodedata


def normalized(value):
    return re.sub(r"\s+", "", unicodedata.normalize("NFKC", value).casefold()).strip("。.!！")


def memory_control(text):
    text = text.strip().strip("。.!！")
    restore = re.fullmatch(r"(.{1,200})(?:可以重新记着了|可以重新用了|可以再提了)", text)
    if restore:
        return "恢复使用", restore.group(1)
    match = re.fullmatch(r"(?:请)?(以后别再提|以后不要再提|不要再提|别再提|停止使用|恢复使用|重新允许使用|删除|删掉|彻底删除)(.{1,200})", text)
    return match.groups() if match else None


def timestamp(value):
    if value is None:
        return None
    if isinstance(value, bool):
        raise ValueError("invalid timestamp")
    if isinstance(value, (int, float)):
        result = float(value)
    else:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            raise ValueError("time must include timezone")
        result = parsed.astimezone(timezone.utc).timestamp()
    if not math.isfinite(result):
        raise ValueError("invalid timestamp")
    return result


def initial_weight(temporal_type, validity):
    """User decision 2026-10-16 supersedes PRD ordinary-first-score 0.3.

    Explicit stable assertions start formal at 0.6; transient states and plans
    still accumulate 0.3 -> 0.6. Inferences never call this as certification.
    """
    temporary = temporal_type == "plan" or (temporal_type == "current_state" and validity.get("precision") != "ongoing")
    return 3 if temporary else 6


def eligible(data, now, *, domain="user_fact", lore_version=None):
    if domain == "user_fact" and data.get("subject") != "user":
        return False
    if not (data.get("enabled") is True and data.get("model_permission") == "allowed"
            and data.get("stage") == "formal" and data.get("weight", 0) >= 6
            and data.get("truth_status") == "accepted" and data.get("revision_status") == "active"):
        return False
    if data.get("retention_until") is not None and now >= timestamp(data["retention_until"]):
        return False
    validity = data.get("temporal_validity") or {}
    start, end = timestamp(validity.get("from")), timestamp(validity.get("until"))
    if data.get("temporal_type") in ("current_state", "plan"):
        # An unbounded transient assertion must never become a permanent state.
        ongoing = data.get("temporal_type") == "current_state" and validity.get("precision") == "ongoing"
        if (end is None and not ongoing) or (start is not None and now < start) or (end is not None and now >= end):
            return False
    elif start is not None and now < start:
        return False
    if data.get("temporal_type") == "preference" and end is not None and now >= end:
        return False
    if domain == "official_lore" and (not data.get("source_verified") or data.get("lore_version") != lore_version):
        return False
    return True


def relevance(query, text):
    """Small deterministic lexical rank; no model is given ineligible bodies."""
    query, text = normalized(query).replace("用户", ""), normalized(text).replace("用户", "")
    if not query or not text:
        return 0
    if query in text or text in query:
        return max(1, min(len(query), len(text)))
    terms = set(re.findall(r"[a-z0-9_]+", query))
    pairs = {query[i:i+2] for i in range(len(query)-1) if all("\u3400" <= c <= "\u9fff" for c in query[i:i+2])}
    return sum(len(term) for term in terms | pairs if term in text)
