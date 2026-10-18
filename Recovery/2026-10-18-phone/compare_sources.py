"""Compare phone backup IDs with encrypted server timeline; no content output."""

from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from analyze_timeline import decode_base64, read_key


CHINA_TIME = timezone(timedelta(hours=8))


def compare(timeline_path: Path, env_path: Path, database_path: Path) -> None:
    payload = json.loads(timeline_path.read_text(encoding="utf-8"))
    data = AESGCM(read_key(env_path)).decrypt(
        decode_base64(payload["nonce"]), decode_base64(payload["ciphertext"]), None
    )
    timeline = json.loads(data)
    with sqlite3.connect(f"file:{database_path.as_posix()}?mode=ro", uri=True) as database:
        phone_rows = {
            row[0]: row[1]
            for row in database.execute("SELECT message_id, content FROM chat_messages")
        }
        phone_ids = set(phone_rows)
        phone_min, phone_max = database.execute(
            "SELECT MIN(timestamp), MAX(timestamp) FROM chat_messages"
        ).fetchone()
    groups: dict[str, list[dict]] = defaultdict(list)
    for item in timeline:
        groups[str(item.get("userId", ""))].append(item)
    for user_id, items in sorted(groups.items(), key=lambda group: -len(group[1])):
        dates = [datetime.fromtimestamp(int(x["timestamp"]) / 1000, CHINA_TIME) for x in items]
        matching = sum(str(x.get("messageId", "")) in phone_ids for x in items)
        normalized = []
        for item in items:
            content = str(item.get("content", ""))
            if not content.strip():
                continue
            message_id = str(item.get("messageId", ""))
            if item.get("role") == "user":
                normalized.append((message_id, content))
            else:
                normalized.extend(
                    (f"{message_id}_{index}", part)
                    for index, part in enumerate(part.strip() for part in content.split("\n") if part.strip())
                )
        normalized_overlap = sum(message_id in phone_ids for message_id, _ in normalized)
        mismatches = sum(
            message_id in phone_rows and phone_rows[message_id] != content
            for message_id, content in normalized
        )
        before_phone = sum(int(x["timestamp"]) < phone_min for x in items)
        after_phone = sum(int(x["timestamp"]) > phone_max for x in items)
        label = hashlib.sha256(user_id.encode()).hexdigest()[:8]
        print(
            f"user_hash={label} count={len(items)} phone_id_overlap={matching} "
            f"normalized={len(normalized)} normalized_overlap={normalized_overlap} "
            f"overlap_content_mismatches={mismatches} "
            f"before_phone={before_phone} after_phone={after_phone} "
            f"earliest={min(dates).date()} latest={max(dates).date()}"
        )


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("timeline", type=Path)
    parser.add_argument("env", type=Path)
    parser.add_argument("database", type=Path)
    args = parser.parse_args()
    compare(args.timeline, args.env, args.database)
