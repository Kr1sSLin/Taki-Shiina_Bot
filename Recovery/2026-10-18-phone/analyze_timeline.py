"""Read-only summary of an encrypted production chat timeline.

Only aggregate counts are printed. Neither the key nor message contents are
written to disk or included in the output.
"""

from __future__ import annotations

import argparse
import base64
import json
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


CHINA_TIME = timezone(timedelta(hours=8))
CUTOFF = datetime(2026, 10, 14, tzinfo=CHINA_TIME)


def decode_base64(value: str) -> bytes:
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def read_key(env_path: Path) -> bytes:
    for line in env_path.read_text(encoding="utf-8-sig").splitlines():
        name, separator, value = line.partition("=")
        if separator and name.strip().removeprefix("export ").strip() == "DATA_ENC_KEY":
            value = value.strip().strip('"').strip("'")
            try:
                key = decode_base64(value)
            except ValueError:
                key = bytes.fromhex(value)
            if len(key) not in (16, 24, 32):
                raise ValueError("Invalid DATA_ENC_KEY length")
            return key
    raise ValueError("DATA_ENC_KEY is missing")


def summarize(timeline_path: Path, env_path: Path) -> None:
    envelope = json.loads(timeline_path.read_text(encoding="utf-8"))
    if envelope.get("ver") != 1 or envelope.get("alg") != "AESGCM":
        raise ValueError("Unsupported encrypted timeline format")
    raw = AESGCM(read_key(env_path)).decrypt(
        decode_base64(envelope["nonce"]), decode_base64(envelope["ciphertext"]), None
    )
    items = json.loads(raw)
    if not isinstance(items, list):
        raise ValueError("Timeline root is not a list")
    timestamps = [datetime.fromtimestamp(int(x["timestamp"]) / 1000, CHINA_TIME) for x in items]
    by_day = Counter(date.date().isoformat() for date in timestamps)
    by_role = Counter(str(item.get("role", "")) for item in items)
    ids = [str(item.get("messageId", "")) for item in items]
    print(f"total={len(items)} unique_message_ids={len(set(ids))}")
    print(f"before_2026_10_14={sum(date < CUTOFF for date in timestamps)}")
    print(f"on_or_after_2026_10_14={sum(date >= CUTOFF for date in timestamps)}")
    print(f"earliest={min(timestamps).isoformat() if timestamps else 'none'}")
    print(f"latest={max(timestamps).isoformat() if timestamps else 'none'}")
    print(f"roles={dict(by_role)} unique_users={len({str(x.get('userId', '')) for x in items})}")
    print(f"by_day={dict(sorted(by_day.items()))}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("timeline", type=Path)
    parser.add_argument("env", type=Path)
    arguments = parser.parse_args()
    summarize(arguments.timeline, arguments.env)
