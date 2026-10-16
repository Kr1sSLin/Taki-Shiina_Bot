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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--facts", type=int, default=250)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
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
