"""Check Git's staged contents without printing secrets; no third-party packages.

This is a focused guard for known private paths and credential signatures,
not a replacement for a full repository/history security audit.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys


PRIVATE_RUNTIME = {
    ".aitest", ".t1", ".t2", ".e2e", ".dbg", ".home", ".probe-home",
}
PRIVATE_NAMES = {
    "credentials.json", "credentials.enc", "keystore.properties",
    "chat_history.json", "user_memories.json", "current_city.txt", "memory_db.json",
    "chat_timeline.json", "memory_timeline.json", "refresh_tokens.json",
    "points_account.json", "progress_data.json",
    "SingletonCookie", "SingletonLock", "SingletonSocket",
}
PATTERNS = {
    "private-key": re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----"),
    "provider-key": re.compile(rb"\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{30,})\b"),
    "jwt": re.compile(rb"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
    "telegram-token": re.compile(rb"\b\d{8,12}:[A-Za-z0-9_-]{30,}\b"),
    "webhook-secret": re.compile(rb"https?://[^\s\"']+/(?:hook|webhooks)/[A-Za-z0-9_-]{20,}"),
    "personal-windows-path": re.compile(rb"(?i)[a-z]:[\\/]+Users[\\/]+(?!(?:user|username|public|default)(?:[\\/]|\b))[^\\/\s\"'<>]+"),
}


def private_path(path: str) -> bool:
    p = pathlib.PurePosixPath(path)
    name = p.name
    if name.startswith(".env") and (name == ".env" or name.startswith(".env.")):
        return not (name == ".env.example" or name.endswith(".example"))
    return (
        name in PRIVATE_NAMES
        or name.endswith(".bak") or ".bak." in name
        or p.suffix.lower() in {".jks", ".keystore", ".p12", ".pfx", ".db", ".sqlite", ".sqlite3"}
        or (".claude" in p.parts and name == "settings.local.json")
        or ("TKS_Bot_Linux" in p.parts and any(x in PRIVATE_RUNTIME for x in p.parts))
    )


def content_findings(data: bytes) -> list[tuple[str, int]]:
    return [(kind, data.count(b"\n", 0, match.start()) + 1)
            for kind, pattern in PATTERNS.items() for match in pattern.finditer(data)]


def git(*args: str) -> bytes:
    return subprocess.check_output(["git", *args])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--staged", action="store_true", help="only check staged changes")
    args = parser.parse_args()
    # Blob IDs come from the index, never from unstaged working-tree files.
    entries = git("ls-files", "--stage", "-z").split(b"\0")
    changed = None
    if args.staged:
        changed = set(git("diff", "--cached", "--name-only", "--diff-filter=ACMRT", "-z").split(b"\0"))
    failed = False
    count = 0
    batch = subprocess.Popen(["git", "cat-file", "--batch"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    for entry in entries:
        if not entry:
            continue
        metadata, path_bytes = entry.split(b"\t", 1)
        mode, oid, stage = metadata.split()
        if changed is not None and path_bytes not in changed:
            continue
        path = path_bytes.decode("utf-8", "replace")
        count += 1
        if private_path(path):
            print(f"BLOCKED private-path: {path}")
            failed = True
        if stage != b"0":
            print(f"BLOCKED unmerged-index: {path}")
            failed = True
            continue
        if mode == b"160000":  # Submodule contents need their own audit.
            continue
        batch.stdin.write(oid + b"\n")
        batch.stdin.flush()
        header = batch.stdout.readline().split()
        if len(header) != 3 or header[1] != b"blob":
            raise RuntimeError("Cannot read indexed blob")
        data = batch.stdout.read(int(header[2]))
        batch.stdout.read(1)
        for kind, line in content_findings(data):
            print(f"BLOCKED {kind}: {path}:{line}")
            failed = True
    batch.stdin.close()
    batch.stdout.close()
    if batch.wait() != 0:
        raise RuntimeError("Git blob reader failed")
    print(f"Privacy check: {count} indexed files; {'FAILED' if failed else 'passed'}.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
