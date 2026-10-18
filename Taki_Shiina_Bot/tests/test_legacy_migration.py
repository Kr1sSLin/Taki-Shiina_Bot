"""Isolated PRD-06 R01/R02 migration and recovery rehearsal with synthetic data."""
import logging
import tempfile
import unittest
from pathlib import Path

from secure_storage import EncryptionConfig, SecureJsonStore
from services.domain_cipher import DomainCipher
from services.foundation_store import PermissionDenied
from services.legacy_migration import migrate_file
from services.reliable_chat_store import ReliableChatStore


class LegacyMigrationTests(unittest.TestCase):
    def test_repeatable_pending_import_and_permission_fenced_restore(self):
        with tempfile.TemporaryDirectory(prefix="tks-legacy-rehearsal-") as folder:
            source = Path(folder) / "legacy.json"
            target = Path(folder) / "new.sqlite3"
            key = b"m" * 32
            source_store = SecureJsonStore(str(source), logging.getLogger(__name__),
                EncryptionConfig(key, auto_migrate=False, backup_on_migrate=False))
            source_store.save_strict({"owner": ["[2026-08-01] 喜欢咖啡", "[2026-08-01] 喜欢咖啡"]})

            first = migrate_file(str(source), str(target), key)
            again = migrate_file(str(source), str(target), key)
            self.assertEqual((first["created"], first["checked"]), (2, 2))
            self.assertEqual((again["created"], again["checked"]), (0, 2))
            self.assertEqual(first["audit_sha256"], again["audit_sha256"])
            store = ReliableChatStore(str(target), DomainCipher(key))
            page = store.fact_page("owner")
            ids = {item["factId"] for item in page["items"]}
            self.assertEqual(len(ids), 2)
            self.assertTrue(all(item["enabled"] is False and item["score"] == 0
                                and item["stage"] == "candidate" for item in page["items"]))
            self.assertTrue(all(store.fact_detail("owner", fact_id)["content"] == "[2026-08-01] 喜欢咖啡"
                                for fact_id in ids))

            backup = Path(folder) / "backup.sqlite3"
            authority_head = store.change_head()
            store.backup(str(backup))
            restored = ReliableChatStore(str(backup), DomainCipher(key))
            with self.assertRaises(PermissionDenied):
                restored.fact_detail("owner", next(iter(ids)))
            with self.assertRaises(PermissionDenied):
                restored.verify_restored_permissions(authority_head + 1)
            restored.verify_restored_permissions(authority_head)
            self.assertEqual(restored.change_head(), authority_head)
            self.assertTrue(all(restored.fact_detail("owner", fact_id)["enabled"] is False for fact_id in ids))


if __name__ == "__main__":
    unittest.main()
