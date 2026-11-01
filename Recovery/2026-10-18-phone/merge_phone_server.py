"""Merge one phone database copy with its matching encrypted server timeline.

The input database is a scratch copy of the phone backup. Existing rows always
win, including the few rows whose content differs from the server snapshot.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

from analyze_timeline import decode_base64, read_key


CHINA_TIME = timezone(timedelta(hours=8))
CUTOFF_MS = int(datetime(2026, 10, 14, tzinfo=CHINA_TIME).timestamp() * 1000)
OLD_PACKAGE = "com.krisslin.androidaiassistant"
NEW_PACKAGE = OLD_PACKAGE + ".recovery"


def normalized(items: list[dict]):
    for item in items:
        message_id = str(item.get("messageId", ""))
        content = str(item.get("content", ""))
        if not message_id or not content.strip():
            continue
        timestamp = int(item["timestamp"])
        role = "user" if item.get("role") == "user" else "bot"
        if role == "user":
            yield message_id, role, content, timestamp
        else:
            parts = [part.strip() for part in content.split("\n") if part.strip()]
            for index, part in enumerate(parts):
                yield f"{message_id}_{index}", role, part, timestamp + index


def merge(timeline_path: Path, env_path: Path, database_path: Path) -> None:
    envelope = json.loads(timeline_path.read_text(encoding="utf-8"))
    raw = AESGCM(read_key(env_path)).decrypt(
        decode_base64(envelope["nonce"]), decode_base64(envelope["ciphertext"]), None
    )
    groups: dict[str, list[dict]] = defaultdict(list)
    for item in json.loads(raw):
        groups[str(item.get("userId", ""))].append(item)
    with sqlite3.connect(database_path) as database:
        phone_rows = dict(database.execute("SELECT message_id, content FROM chat_messages"))
        if len(phone_rows) < 1000:
            raise ValueError("Phone backup is unexpectedly small")
        ranked = sorted(
            ((sum(message_id in phone_rows for message_id, _, _, _ in normalized(items)), user_id)
             for user_id, items in groups.items()),
            reverse=True,
        )
        if not ranked or ranked[0][0] < 500 or (len(ranked) > 1 and ranked[1][0] > 0):
            raise ValueError("Cannot identify one matching server account unambiguously")
        server_rows = list(normalized(groups[ranked[0][1]]))
        content_conflicts = sum(
            message_id in phone_rows and phone_rows[message_id] != content
            for message_id, _, content, _ in server_rows
        )
        before = len(phone_rows)
        with database:
            database.executemany(
                """INSERT OR IGNORE INTO chat_messages
                (message_id, session_id, role, message_type, content_type,
                 model_provider, content, image_url, weather_attached, status,
                 timestamp, error_code)
                VALUES (?, 'default_session', ?, 'text', 'text', 'deepseek',
                        ?, NULL, 0, ?, ?, NULL)""",
                ((message_id, role, content,
                  "sent" if role == "user" else "received", timestamp)
                 for message_id, role, content, timestamp in server_rows),
            )
            database.execute(
                "UPDATE chat_attachments SET local_uri = REPLACE(local_uri, ?, ?) "
                "WHERE local_uri LIKE ?",
                (f"/{OLD_PACKAGE}/", f"/{NEW_PACKAGE}/", f"%/{OLD_PACKAGE}/%"),
            )
        total = database.execute("SELECT COUNT(*) FROM chat_messages").fetchone()[0]
        older = database.execute(
            "SELECT COUNT(*) FROM chat_messages WHERE timestamp < ?", (CUTOFF_MS,)
        ).fetchone()[0]
        first, last = database.execute(
            "SELECT MIN(timestamp), MAX(timestamp) FROM chat_messages"
        ).fetchone()
        print(f"phone_messages={before} server_account_messages={len(server_rows)}")
        print(f"inserted={total - before} retained_content_conflicts={content_conflicts}")
        print(f"merged_total={total} before_2026_10_14={older}")
        print(f"earliest={datetime.fromtimestamp(first / 1000, CHINA_TIME).isoformat()}")
        print(f"latest={datetime.fromtimestamp(last / 1000, CHINA_TIME).isoformat()}")
        print(f"integrity_check={database.execute('PRAGMA integrity_check').fetchone()[0]}")
        print(f"attachment_rows={database.execute('SELECT COUNT(*) FROM chat_attachments').fetchone()[0]}")
        database.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        database.execute("PRAGMA journal_mode=DELETE")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("timeline", type=Path)
    parser.add_argument("env", type=Path)
    parser.add_argument("database", type=Path)
    arguments = parser.parse_args()
    merge(arguments.timeline, arguments.env, arguments.database)
