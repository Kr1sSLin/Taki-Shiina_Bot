"""Repeatable stage-B demo. Only synthetic identity, random key and temp DB."""
import json
import os
from pathlib import Path
import secrets
import sys
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Taki_Shiina_Bot"))
from services.contracts import ChatCommand
from services.domain_cipher import DomainCipher
from services.reliable_chat_store import ReliableChatStore


def demonstrate():
    with tempfile.TemporaryDirectory(prefix="tks-stage-b-demo-") as directory:
        store = ReliableChatStore(str(Path(directory) / "demo.sqlite3"), DomainCipher(secrets.token_bytes(32)), clock=lambda: 1000)
        weights = []
        evidence = {"text": "用户今天感冒", "evidence": "我今天感冒了", "subject": "user", "assertion": "explicit", "temporal_type": "current_state", "temporal_validity": {"from": 900, "until": 2000, "expression": "今天", "timezone": "Asia/Shanghai", "precision": "day"}}
        for number in range(5):
            store.accept([ChatCommand("synthetic-owner", f"request-{number}", f"client-{number}", "demo", "我今天感冒了")])
            job = store.claim(kinds={"extract"})
            fact_id = store.commit_extraction(job, [evidence])[0]
            chat = store.claim(kinds={"chat"})
            context = store.begin_turn(chat)
            store.prepare_memories(context, "感冒")
            result = store.commit_reply(chat, context, {"reply": "合成测试回复"})
            # Finish delivery so the next input starts an independent turn.
            store.mark_dispatched("synthetic-owner", result)
            weights.append(store.read_fact("synthetic-owner", fact_id)["data"]["score"])
        current = store.read_fact("synthetic-owner", fact_id)
        revision = store.set_permission("synthetic-owner", fact_id, enabled=False, expected_revision=current["revision"])
        blocked = not store.select_memories("synthetic-owner", "感冒")
        store.set_permission("synthetic-owner", fact_id, enabled=True, expected_revision=revision)
        sample = json.loads((ROOT / "design" / "memory-lore-sample.json").read_text(encoding="utf-8"))
        store.stage_lore("synthetic-owner", sample["version"], sample["entries"])
        store.review_lore("synthetic-owner", sample["version"], **sample["review"])
        publication = store.publish_lore("synthetic-owner", sample["version"])
        stable_scores = []
        for number in range(2):
            store.accept([ChatCommand("synthetic-owner", f"stable-request-{number}", f"stable-client-{number}", "demo", "我喜欢咖啡")])
            job = store.claim(kinds={"extract"})
            stable_id = store.commit_extraction(job, [{"text": "用户喜欢咖啡", "evidence": "我喜欢咖啡", "subject": "user", "assertion": "explicit", "temporal_type": "preference"}])[0]
            chat = store.claim(kinds={"chat"})
            context = store.begin_turn(chat)
            store.prepare_memories(context, "咖啡")
            result = store.commit_reply(chat, context, {"reply": "合成测试回复"})
            store.mark_dispatched("synthetic-owner", result)
            stable_scores.append(store.read_fact("synthetic-owner", stable_id)["data"]["score"])
        return {"temporary_scores": weights, "stable_scores": stable_scores, "blocked_is_excluded": blocked, "restored_score": store.read_fact("synthetic-owner", fact_id)["data"]["score"], "lore_publication": publication, "production_data_accessed": False}


if __name__ == "__main__":
    with patch.dict(os.environ, {"MEMORY_COLLECTION_ENABLED": "1", "MEMORY_REINFORCEMENT_ENABLED": "1"}):
        print(json.dumps(demonstrate(), ensure_ascii=True, indent=2))
