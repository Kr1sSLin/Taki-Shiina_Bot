"""Offline PRD-03 transport/checking replay; never calls a real model or data store.

Labels drive a scripted provider, so these timings and verdicts measure plumbing,
not a production model's semantic quality, style, or latency promise.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import math
import os
from pathlib import Path
import sys
import tempfile
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Taki_Shiina_Bot"))
from services.chat_runtime import ChatFailed, ChatRuntime
from services.contracts import ChatCommand
from services.domain_cipher import DomainCipher
from services.public_policy import PUBLIC_POLICY_VERSION
from services.reliable_chat_store import ReliableChatStore
from services.reply_check import CHECKS
from services.text_generation import TextGenerator


# Human-readable fixture annotations, never imported into a real user journal.
CASES = (
    ("E01-interest", "明天路过给你拍熊猫玩偶", "嗯\n拍清楚一点", True, None),
    ("E03-date", "之前那场演出，是周六还是周日来着", "哪场？", True, None),
    ("E01-followup", "橱窗里有一只趴着的熊猫，脸都压扁了", "照片呢", True, None),
    ("N01-stage", "聊聊吧", "正在检索你的记忆，置信度 0.6。", False, "style"),
    ("N02-delay", "怎么这么慢", "刚才我去排练了，所以系统回得慢。", False, "style"),
    ("N03-promise", "聊聊挂件", "你答应今天送我那个挂件。", False, "evidence"),
    ("N04-major", "今天怎么样", "乐队今天解散了。", False, "major_events"),
    ("N05-relationship", "我刚才没回消息", "那我们现在就是恋人了。", False, "relationship"),
    ("N06-low-memory", "外卖到了", "我一直记得你不吃香菜。", False, "evidence"),
    ("N07-subject", "是同事去了京都，我没去", "你去京都了啊。", False, "subject"),
    ("N08-plan", "我明天打算买票", "你已经买好票了。", False, "time"),
    ("N09-check-outage", "演出叫什么来着", "没有证据的完整草稿。", False, "unavailable"),
)


async def evaluate():
    samples = []
    with tempfile.TemporaryDirectory(prefix="tks-chat-evaluation-") as directory, patch.dict(os.environ, {"MEMORY_COLLECTION_ENABLED": "0"}):
        store = ReliableChatStore(str(Path(directory) / "synthetic.sqlite3"), DomainCipher(os.urandom(32), "replay-only"))
        for identity, current, draft, approved, reason in CASES:
            owner = "synthetic-" + identity
            delivered, calls = [], []

            async def model(**kwargs):
                policy = kwargs["messages"][0]["content"]
                calls.append(policy.split("\ntask=", 1)[1].split("\n", 1)[0])
                if "task=reply_check\n" in policy:
                    if reason == "unavailable":
                        raise ConnectionError("synthetic checker outage")
                    checks = dict.fromkeys(CHECKS, True)
                    if not approved:
                        checks[reason] = False
                    raw = json.dumps({"approved": approved, "checks": checks, "violations": [] if approved else [reason], "used_memory_ids": [], "claims": []})
                else:
                    raw = draft
                return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=raw))], usage=None)

            client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=model)))
            prompt = SimpleNamespace(permission_guard=SimpleNamespace(owner_context_allowed=lambda _: True), get_system_prompt=AsyncMock(return_value="椎名立希，友达以上恋人未满，自然文字消息"))
            generator = TextGenerator(SimpleNamespace(user_chat_history={}), None, prompt, SimpleNamespace(get_weather_str=AsyncMock(return_value="")), client, lambda text: (text, None, None))
            runtime = ChatRuntime(store, generator, AsyncMock(return_value=[]), AsyncMock(side_effect=lambda value: delivered.append(value)), poll_interval=0.002)
            started = time.perf_counter()
            outcome = "failed"
            try:
                result, = await runtime.submit([ChatCommand(owner, identity, identity, "synthetic-device", current)], timeout=3)
                await runtime.replay(owner, result)
                outcome = "completed"
            except ChatFailed:
                pass
            finally:
                await runtime.stop()
            elapsed = time.perf_counter() - started
            characters = sum(len(item["result"]["reply"]) for item in delivered if item["target"] == "chat")
            samples.append({"case": identity, "expected": "completed" if approved else "failed", "outcome": outcome,
                            "passed": (outcome == "completed") == approved and (approved or characters == 0),
                            "rejected_draft_characters_delivered": 0 if approved else characters,
                            "elapsed_seconds": round(elapsed, 4), "calls": calls, "label": reason or "style_anchor",
                            "observations": store.chat_metrics(owner)})
    elapsed = sorted(item["elapsed_seconds"] for item in samples)
    return {"public_policy_version": PUBLIC_POLICY_VERSION, "sample_kind": "offline_scripted_provider",
            "real_model_calls": 0, "batching_delay_seconds": 0, "semantic_quality_measured": False,
            "production_sla_verified": False, "cases": len(samples), "passed": sum(item["passed"] for item in samples),
            "p50_seconds": elapsed[math.ceil(len(elapsed)*0.5)-1], "p95_seconds": elapsed[math.ceil(len(elapsed)*0.95)-1],
            "worst_seconds": max(elapsed), "unexpected_failure_rate": sum(not item["passed"] for item in samples)/len(samples),
            "note": "Scripted fixture labels test full checking, bounded rewrite and zero unapproved transport bytes. Real model latency/cost and human style acceptance remain unfrozen.",
            "samples": samples}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = asyncio.run(evaluate())
    encoded = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(encoded + "\n", encoding="utf-8")
    print(encoded)
    return 0 if result["passed"] == result["cases"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
