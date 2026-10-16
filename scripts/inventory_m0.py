"""Read-only data metadata inventory. Never decrypt data or print secret values."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import stat

DATA_FILES = (
    "chat_history.json", "user_memories.json", "chat_timeline.json", "memory_timeline.json",
    "points_account.json", "progress_data.json", "current_city.txt", "config/gamification_config.json",
)


def describe(path: Path) -> dict:
    row = {"path": str(path), "exists": path.exists(), "growth": "unknown: requires a second observation"}
    if not path.exists():
        return row
    info = path.stat()
    row.update(bytes=info.st_size, uid=info.st_uid, gid=info.st_gid,
               mode=stat.filemode(info.st_mode), modified=datetime.fromtimestamp(info.st_mtime, timezone.utc).isoformat())
    if path.suffix == ".json" and path.is_file() and info.st_size > 32 * 1024 * 1024:
        row["records"] = "unknown: JSON exceeds 32 MiB metadata-only limit"
    elif path.suffix == ".json" and path.is_file():
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            encrypted = isinstance(data, dict) and data.get("alg") == "AESGCM"
            row["encrypted"] = encrypted
            row["version"] = data.get("ver" if encrypted else "version", "unversioned") if isinstance(data, dict) else "unversioned"
            row["records"] = "unknown: encrypted" if encrypted else len(data) if isinstance(data, (dict, list)) else None
            if isinstance(data, dict) and not encrypted:
                # Only structural counts. Never enumerate user IDs, messages or credentials.
                row["collections"] = {key: len(data[key]) for key in (
                    "tokens", "points_account", "points_ledger", "user_progress", "daily_activity_log", "makeup_cards"
                ) if isinstance(data.get(key), (dict, list))}
        except (OSError, UnicodeError, ValueError):
            row["records"] = "unknown: unreadable JSON"
    return row


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend-dir", type=Path, default=Path(__file__).resolve().parents[1] / "Taki_Shiina_Bot")
    parser.add_argument("--data-dir", type=Path, help="Actual BOT_DATA_DIR, if configured; .env is never read")
    args = parser.parse_args()
    backend = args.backend_dir.resolve()
    data = (args.data_dir or backend).resolve()
    paths = {data / name for name in DATA_FILES}
    paths.update(backend / name for name in ("refresh_tokens.json", ".env", "bot_memory.db", "memory_db.json"))
    # Legacy bot/maintenance script paths may differ from BOT_DATA_DIR.
    paths.update(backend / name for name in DATA_FILES)
    for directory in {data, backend, data / "config"}:
        for pattern in ("*.json.lock", ".setup-backend.lock", "*.tmp", "*.bak", "*.bak.*", ".env.bak.*", "*.db-wal", "*.db-shm"):
            paths.update(directory.glob(pattern))
    report = {"observedAt": datetime.now(timezone.utc).isoformat(), "scope": "explicit directories only; not a production assertion",
              "backendDir": str(backend), "dataDir": str(data), "files": [describe(path) for path in sorted(paths)]}
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
