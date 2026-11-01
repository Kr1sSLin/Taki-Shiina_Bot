"""List Taki database entries in a Xiaomi backup without extracting user data."""

from __future__ import annotations

import argparse
import tarfile
import zipfile
from collections import Counter
from pathlib import Path


def inspect(path: Path) -> None:
    with zipfile.ZipFile(path) as archive:
        backup_name = next(name for name in archive.namelist() if name.endswith(".bak"))
        with archive.open(backup_name) as payload:
            if payload.readline() != b"MIUI BACKUP\n":
                raise ValueError("Not a Xiaomi backup")
            while payload.readline() != b"ANDROID BACKUP\n":
                pass
            version = payload.readline().strip()
            compression = payload.readline().strip()
            encryption = payload.readline().strip()
            if compression != b"0" or encryption != b"none":
                raise ValueError("Compressed or encrypted Android backup is unsupported")
            print(f"android_backup_version={version.decode('ascii')}")
            groups: Counter[str] = Counter()
            databases: list[tuple[str, int]] = []
            files: list[int] = []
            with tarfile.open(fileobj=payload, mode="r|") as contents:
                for member in contents:
                    parts = member.name.split("/")
                    if len(parts) >= 3:
                        groups[parts[2]] += 1
                    if "/db/" in member.name:
                        databases.append((member.name, member.size))
                    if "/f/" in member.name and member.isfile():
                        files.append(member.size)
            print(f"groups={dict(groups)}")
            print(f"database_entries={databases}")
            print(f"file_count={len(files)} file_bytes={sum(files)} largest_file={max(files, default=0)}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("backup", type=Path)
    inspect(parser.parse_args().backup)
