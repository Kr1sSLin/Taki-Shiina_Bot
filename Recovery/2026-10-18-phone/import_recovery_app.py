"""Import a verified merged backup into the separate Android recovery app.

Data is streamed over ADB directly into the app sandbox. No plaintext chat or
images are copied to Android shared storage, and the production app is untouched.
"""

from __future__ import annotations

import argparse
import hashlib
import shlex
import subprocess
import tarfile
import zipfile
from pathlib import Path, PurePosixPath


PACKAGE = "com.krisslin.androidaiassistant.recovery"
SOURCE_PACKAGE = "com.krisslin.androidaiassistant"
DATABASE_NAME = "android_ai_assistant.db"


def command(adb: Path, *arguments: str) -> str:
    result = subprocess.run(
        [str(adb), *arguments], capture_output=True, check=True
    )
    return result.stdout.decode("utf-8", errors="replace").strip()


def stream_file(adb: Path, target: str, content: bytes) -> None:
    result = subprocess.run(
        [str(adb), "exec-in", "run-as", PACKAGE, "sh", "-c",
         f"cat > {shlex.quote(target)}"],
        input=content, capture_output=True,
    )
    if result.returncode != 0:
        raise RuntimeError(f"ADB import failed for {target}: {result.stderr.decode(errors='replace')}")
    expected_hash = hashlib.sha256(content).hexdigest()
    for _ in range(3):
        remote_hash = command(adb, "shell", "run-as", PACKAGE, "sha256sum", target).split()[0]
        if remote_hash.lower() == expected_hash:
            return
    raise RuntimeError(f"ADB import checksum mismatch for {target}")


def import_backup(adb: Path, database: Path, backup: Path) -> None:
    if not adb.is_file() or not database.is_file() or not backup.is_file():
        raise ValueError("Missing ADB, merged database, or phone backup")
    command(adb, "shell", "run-as", PACKAGE, "id")
    command(adb, "shell", "am", "force-stop", PACKAGE)
    command(adb, "shell", "run-as", PACKAGE, "mkdir", "-p", "databases")
    existing = subprocess.run(
        [str(adb), "shell", "run-as", PACKAGE, "test", "-e", f"databases/{DATABASE_NAME}"],
        capture_output=True,
    )
    database_bytes = database.read_bytes()
    if existing.returncode == 0:
        remote_hash = command(
            adb, "shell", "run-as", PACKAGE, "sha256sum", f"databases/{DATABASE_NAME}"
        ).split()[0]
        if remote_hash.lower() != hashlib.sha256(database_bytes).hexdigest():
            raise RuntimeError("Recovery app has a different database; refusing to overwrite it")
    else:
        stream_file(adb, f"databases/{DATABASE_NAME}", database_bytes)
    print("merged_database_imported_and_verified")
    file_count = 0
    byte_count = 0
    created_directories: set[str] = set()
    with zipfile.ZipFile(backup) as archive:
        name = next(name for name in archive.namelist() if name.endswith(".bak"))
        with archive.open(name) as payload:
            if payload.readline() != b"MIUI BACKUP\n":
                raise ValueError("Not a Xiaomi backup")
            while payload.readline() != b"ANDROID BACKUP\n":
                pass
            payload.readline()  # version
            if payload.readline().strip() != b"0" or payload.readline().strip() != b"none":
                raise ValueError("Unsupported Android backup encoding")
            with tarfile.open(fileobj=payload, mode="r|") as contents:
                for member in contents:
                    if not member.isfile():
                        continue
                    parts = PurePosixPath(member.name).parts
                    if parts[:3] != ("apps", SOURCE_PACKAGE, "f"):
                        continue
                    relative = PurePosixPath(*parts[3:])
                    if not relative.parts or any(part in ("", ".", "..") for part in relative.parts):
                        raise ValueError("Unsafe backup file path")
                    target = str(PurePosixPath("files") / relative)
                    parent = str(PurePosixPath(target).parent)
                    if parent not in created_directories:
                        command(adb, "shell", "run-as", PACKAGE, "mkdir", "-p", parent)
                        created_directories.add(parent)
                    source = contents.extractfile(member)
                    if source is None:
                        raise ValueError("Unreadable backup file")
                    content = source.read()
                    stream_file(adb, target, content)
                    file_count += 1
                    byte_count += len(content)
                    if file_count % 10 == 0:
                        print(f"verified_files={file_count}", flush=True)
    print(f"import_complete files={file_count} bytes={byte_count}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("adb", type=Path)
    parser.add_argument("database", type=Path)
    parser.add_argument("backup", type=Path)
    args = parser.parse_args()
    import_backup(args.adb, args.database, args.backup)
