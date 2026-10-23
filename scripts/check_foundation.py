"""Synthetic stage A benchmark and recovery replay. Never opens runtime data."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import statistics
import sys
import tempfile
import time
import tracemalloc

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Taki_Shiina_Bot"))
from services.contracts import Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import FoundationStore, PermissionDenied


def memory_measurement(count):
    from services.contracts import ChatCommand
    from services.reliable_chat_store import ReliableChatStore
    from services.input_budget import json_bytes
    with tempfile.TemporaryDirectory(prefix="tks-memory-budget-") as temp:
        cipher = DomainCipher(os.urandom(32), "synthetic-v1")
        store = ReliableChatStore(str(Path(temp) / "memory.db"), cipher)
        owner = "synthetic-owner"
        key = cipher.index("owner", owner)
        with store._transaction() as conn:
            for index in range(count):
                data = {"text": f"用户喜欢合成青椒品种{index}", "enabled": index % 7 != 0, "model_permission": "blocked" if index % 7 == 0 else "allowed",
                        "permission_version": 1, "revision_id": str(index), "subject": "user", "weight": 6, "stage": "formal",
                        "truth_status": "accepted", "revision_status": "active", "temporal_type": "preference",
                        "temporal_validity": {"from": None, "until": None}, "source": f"synthetic-{index}"}
                FoundationStore._write_fact(store, conn, key, f"fact-{index:06d}", Domain.USER_FACT, 1, data)
        store.accept([ChatCommand(owner, "synthetic-request", "synthetic-request", "synthetic-device", "我之前说的青椒记错了")])
        job = store.claim(kinds={"extract"}, lease_seconds=300)
        measurements = {}
        for name, action in (("select_memories", lambda: store.select_memories(owner, "青椒")),
                             ("extraction_candidates", lambda: store.extraction_candidates(job))):
            tracemalloc.start()
            started = time.perf_counter()
            values = action()
            elapsed = (time.perf_counter() - started) * 1000
            _, peak = tracemalloc.get_traced_memory()
            tracemalloc.stop()
            measurements[name] = {"elapsed_ms": round(elapsed, 3), "peak_python_bytes": peak, "returned_count": len(values),
                                  "returned_json_bytes": json_bytes(values), "complete": getattr(values, "complete", None)}
        return {"facts": count, "measurements": measurements}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--facts", type=int, default=250)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--memory", action="store_true", help="测量 P4 合成 1k/5k/20k 召回与提取候选")
    args = parser.parse_args()
    if args.memory:
        result = {"python": sys.version.split()[0], "platform": sys.platform, "synthetic": True,
                  "rows": [memory_measurement(count) for count in (1000, 5000, 20000)],
                  "note": "单次合成读路径，含密文解密；peak_python_bytes 为 tracemalloc Python 分配峰值，不含 SQLite/C 原生内存；非语义质量或生产容量结论。"}
        text = json.dumps(result, ensure_ascii=False, indent=2)
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(text + "\n", encoding="utf-8")
        print(text)
        return
    if not 1 <= args.facts <= 10000:
        parser.error("facts must be between 1 and 10000")
    with tempfile.TemporaryDirectory(prefix="tks-foundation-benchmark-") as temp:
        root = Path(temp)
        cipher = DomainCipher(os.urandom(32), "synthetic-v1")
        store = FoundationStore(str(root / "primary.db"), cipher)
        latencies = []
        for index in range(args.facts):
            started = time.perf_counter()
            store.put_fact("synthetic-owner", f"fact-{index:06d}", Domain.USER_FACT, {"text": f"合成事实-{index} 偏好青椒", "enabled": True, "score": 0.3, "source": f"synthetic-message-{index}"}, expected_revision=0, targets=())
            latencies.append((time.perf_counter() - started) * 1000)
        tracemalloc.start()
        started = time.perf_counter()
        result_limit = min(args.facts, 1000)
        matches = store.search("synthetic-owner", Domain.USER_FACT, "青椒", limit=result_limit)
        probes = sorted({0, args.facts//2, args.facts-1})
        recalled = sum(len(store.search("synthetic-owner", Domain.USER_FACT, f"合成事实-{index} ", limit=1)) == 1 for index in probes)
        search_ms = (time.perf_counter() - started) * 1000
        _, peak = tracemalloc.get_traced_memory()
        tracemalloc.stop()
        head = store.change_head()
        started = time.perf_counter()
        store.backup(str(root / "backup.db"))
        restored = FoundationStore(str(root / "backup.db"), cipher)
        guard_verified = False
        try:
            restored.search("synthetic-owner", Domain.USER_FACT, "青椒")
        except PermissionDenied:
            guard_verified = True
        restored.verify_restored_permissions(head)
        rebuilt = restored.search("synthetic-owner", Domain.USER_FACT, "青椒", limit=result_limit)
        tail = restored.search("synthetic-owner", Domain.USER_FACT, f"合成事实-{args.facts-1} ", limit=1)
        result = {"python": sys.version.split()[0], "platform": sys.platform, "facts": args.facts, "write_p50_ms": round(statistics.median(latencies), 3), "write_p95_ms": round(sorted(latencies)[int((len(latencies)-1)*0.95)], 3), "search_and_probe_ms": round(search_ms, 3), "search_peak_bytes": peak, "result_limit": result_limit, "returned_matches": len(matches), "known_probe_recall": recalled/len(probes), "backup_restore_rebuild_ms": round((time.perf_counter()-started)*1000, 3), "restored_matches": len(rebuilt), "restored_tail_found": bool(tail), "restore_guard_verified": guard_verified, "note": "Synthetic bounded substring retrieval and first/middle/last probes; not semantic recall or a production capacity claim."}
        if not guard_verified or len(rebuilt) != result_limit or not tail or recalled != len(probes):
            raise RuntimeError("recovery replay failed")
    text = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text + "\n", encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
