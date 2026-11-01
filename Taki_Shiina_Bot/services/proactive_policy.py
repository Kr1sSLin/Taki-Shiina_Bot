"""Pure Beijing-time eligibility rules for proactive conversations."""
from datetime import datetime, timedelta, timezone
import hashlib


BEIJING = timezone(timedelta(hours=8), "Asia/Shanghai")
MIN_RECONTACT_SECONDS = 48 * 3600


def period_keys(now):
    local = datetime.fromtimestamp(now, BEIJING)
    day = local.date()
    return day.isoformat(), (day - timedelta(days=day.weekday())).isoformat()


def in_window(now):
    local = datetime.fromtimestamp(now, BEIJING)
    return 9 <= local.hour < 23


def eligible(now, state, day_count, week_count):
    if not state["enabled"] or not in_window(now) or day_count >= 1 or week_count >= 4:
        return False
    unanswered = state["unanswered"]
    return unanswered == 0 or (unanswered == 1 and now - state["last_sent_at"] >= MIN_RECONTACT_SECONDS)


def opportunity_due(owner, now):
    local = datetime.fromtimestamp(now, BEIJING)
    day = local.date().isoformat()
    minute = int.from_bytes(hashlib.sha256((owner + ":" + day).encode()).digest()[:4], "big") % (14 * 60)
    current = local.hour * 60 + local.minute
    return 9 * 60 + minute <= current < 9 * 60 + minute + 1


def state_after_send(state, now):
    return {**state, "unanswered": state["unanswered"] + 1, "last_sent_at": now,
            "version": state["version"] + 1}


def state_after_user_message(state):
    return {**state, "unanswered": 0, "last_sent_at": None, "version": state["version"] + 1}
