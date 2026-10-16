"""Permission checks for legacy contexts after the SQL journal exists.

The business feature switch never disables this boundary. Legacy profile,
scene and history files have no fact references; if an owner's SQL permission
snapshot denies context, all their unmapped legacy context is quarantined.
"""
from __future__ import annotations

import os
from contextlib import closing
from pathlib import Path
import sqlite3

from secure_storage import DataCorruptionError, DataReadError, load_encryption_config
from services.domain_cipher import DomainCipher
from services.reliable_chat_store import ReliableChatStore


def resolve_data_dir(base_dir=None):
    fallback = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.abspath(base_dir or os.getenv("BOT_DATA_DIR") or fallback)


def foundation_database_exists(base_dir=None):
    return os.path.lexists(os.path.join(resolve_data_dir(base_dir), "foundation.sqlite3"))


class PermissionGuard:
    def __init__(self, base_dir=None):
        self.base_dir = resolve_data_dir(base_dir)
        self.path = os.path.join(self.base_dir, "foundation.sqlite3")
        self._store = None

    def owner_context_allowed(self, owner):
        if not os.path.lexists(self.path):
            if self._store is not None:
                raise DataReadError("The existing permission journal is unavailable")
            return True
        if self._store is None:
            # An existing empty/foreign database is corruption, never consent
            # to initialise a fresh permissions_ready=True journal.
            try:
                with closing(sqlite3.connect(Path(self.path).as_uri() + "?mode=ro", uri=True, timeout=0.5)) as conn:
                    if conn.execute("SELECT 1 FROM meta WHERE id='format'").fetchone() is None:
                        raise DataCorruptionError("Permission journal marker is missing")
            except sqlite3.DatabaseError:
                raise DataCorruptionError("Existing permission journal is invalid or unreadable") from None
            config = load_encryption_config()
            self._store = ReliableChatStore(self.path, DomainCipher(config.key, os.getenv("FOUNDATION_KEY_ID", "primary-v1")))
        # This always rereads the current encrypted permission/recovery state.
        # Corruption, wrong keys, busy storage and unverified restores propagate.
        return self._store.owner_context_allowed(str(owner))


def require_supported_telegram_runtime(base_dir=None):
    if foundation_database_exists(base_dir):
        raise RuntimeError("已存在可靠数据与权限记录，旧 Telegram 入口已关闭；请使用 unified_api 启动三端服务。")
