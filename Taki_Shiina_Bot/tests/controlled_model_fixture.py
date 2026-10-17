"""Offline structured checker replies for existing provider doubles."""
import json
from types import SimpleNamespace

from services.reply_check import CHECKS


def checker_response(kwargs, *, use_memories=True):
    messages = kwargs.get("messages", [])
    if not messages or "task=reply_check\n" not in messages[0].get("content", ""):
        return None
    value = json.loads(messages[-1]["content"])
    ids = [ref["fact_id"] for ref in value["context"]["formal_memories"]] if use_memories else []
    verdict = {"approved": True, "checks": dict.fromkeys(CHECKS, True), "violations": [], "used_memory_ids": ids,
               "claims": [{"text": value["result"]["reply"], "source_ids": ids}] if ids else []}
    return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(verdict)))], usage=None)
