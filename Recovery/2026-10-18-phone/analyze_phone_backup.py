"""Summarize the chat DB in a Xiaomi backup; plaintext lives only in a temp dir."""

from __future__ import annotations

import argparse
import sqlite3
import tarfile
import zipfile
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlparse


CHINA_TIME = timezone(timedelta(hours=8))
CUTOFF_MS = int(datetime(2026, 10, 14, tzinfo=CHINA_TIME).timestamp() * 1000)
DATABASE_NAME = "android_ai_assistant.db"


def analyze(backup_path: Path, scratch: Path) -> None:
    if not scratch.is_dir() or not scratch.resolve().is_relative_to(backup_path.resolve().parent):
        raise ValueError("Scratch directory must already exist next to the backup")
    with zipfile.ZipFile(backup_path) as archive:
        backup_name = next(name for name in archive.namelist() if name.endswith(".bak"))
        with archive.open(backup_name) as payload:
            if payload.readline() != b"MIUI BACKUP\n":
                raise ValueError("Not a Xiaomi backup")
            while payload.readline() != b"ANDROID BACKUP\n":
                pass
            payload.readline()  # version
            if payload.readline().strip() != b"0" or payload.readline().strip() != b"none":
                raise ValueError("Compressed or encrypted backup is unsupported")
            with tarfile.open(fileobj=payload, mode="r|") as contents:
                for member in contents:
                    name = Path(member.name).name
                    if "/db/" not in member.name or name not in (DATABASE_NAME, DATABASE_NAME + "-wal", DATABASE_NAME + "-shm"):
                        continue
                    source = contents.extractfile(member)
                    if source is None:
                        raise ValueError(f"Unreadable database entry: {name}")
                    (scratch / name).write_bytes(source.read())
    database = scratch / DATABASE_NAME
    if not database.exists():
        raise ValueError("Chat database missing from backup")
    with sqlite3.connect(f"file:{database.as_posix()}?mode=ro", uri=True) as connection:
        connection.execute("PRAGMA query_only=ON")
        print(f"schema_version={connection.execute('PRAGMA user_version').fetchone()[0]}")
        print(f"integrity_check={connection.execute('PRAGMA integrity_check').fetchone()[0]}")
        rows = connection.execute(
            "SELECT message_id, session_id, role, timestamp FROM chat_messages"
        ).fetchall()
        attachments = connection.execute("SELECT local_uri FROM chat_attachments").fetchall()
    timestamps = [int(row[3]) for row in rows]
    print(f"total={len(rows)} unique_message_ids={len({row[0] for row in rows})}")
    print(f"before_2026_10_14={sum(value < CUTOFF_MS for value in timestamps)}")
    print(f"on_or_after_2026_10_14={sum(value >= CUTOFF_MS for value in timestamps)}")
    print(f"earliest={datetime.fromtimestamp(min(timestamps) / 1000, CHINA_TIME).isoformat() if timestamps else 'none'}")
    print(f"latest={datetime.fromtimestamp(max(timestamps) / 1000, CHINA_TIME).isoformat() if timestamps else 'none'}")
    print(f"roles={dict(Counter(row[2] for row in rows))} sessions={dict(Counter(row[1] for row in rows))}")
    print(f"attachments={len(attachments)} uri_directories={dict(Counter(str(Path(urlparse(row[0]).path).parent) for row in attachments))}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("backup", type=Path)
    parser.add_argument("scratch", type=Path)
    arguments = parser.parse_args()
    analyze(arguments.backup, arguments.scratch)
