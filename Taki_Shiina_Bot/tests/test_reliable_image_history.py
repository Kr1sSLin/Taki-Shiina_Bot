"""Authenticated historical image bytes, permission gate, and source integrity."""
import base64
from io import BytesIO
import hashlib
import pathlib
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch
from urllib.parse import quote

import httpx
from PIL import Image

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from ws_api_import_fixture import ensure_ws_api_env

ensure_ws_api_env()

import http_api
from auth_utils import create_access_token
from services.contracts import ChatCommand, Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import PermissionDenied
from services.reliable_chat_store import ReliableChatStore


class ReliableImageHistoryTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-image-history-")
        self.addCleanup(self.temp.cleanup)
        self.path = str(pathlib.Path(self.temp.name) / "foundation.sqlite3")
        self.cipher = DomainCipher(b"h" * 32)
        self.store = ReliableChatStore(self.path, self.cipher)
        self.original = self._jpeg_with_exif()
        self.image = {
            "mimeType": "image/jpeg",
            "dataBase64": base64.b64encode(self.original).decode("ascii"),
            "sha256": hashlib.sha256(self.original).hexdigest(),
        }
        self.owner_a, self.owner_b = "owner-a", "owner-b"
        self.request_id = "image request (same across devices)"
        self.command = ChatCommand(
            self.owner_a, self.request_id, "client-message-1", "device-a", "[图片]",
            channel="ws", metadata={"images": [self.image]},
        )
        self.receipt = self.store.accept([self.command])[0]
        self.headers_a = {"Authorization": "Bearer " + create_access_token(self.owner_a, "device-b", "image-history-jwt", 3600)}
        self.headers_b = {"Authorization": "Bearer " + create_access_token(self.owner_b, "device-b", "image-history-jwt", 3600)}
        patches = (
            patch.object(http_api, "get_reliable_runtime", new=lambda: SimpleNamespace(store=self.store)),
            patch.object(http_api, "AUTH_JWT_SECRET", "image-history-jwt"),
            patch.object(http_api, "DEVICE_ALLOWLIST", {}),
            patch.object(http_api, "DEVICE_TOKEN_MAP", {}),
            patch.object(http_api, "BOT_HTTP_TOKEN", ""),
            patch.object(http_api, "BOT_WS_TOKEN", ""),
            patch.object(http_api, "_load_timeline_async", new_callable=AsyncMock, return_value=[]),
        )
        for active in patches:
            active.start()
            self.addCleanup(active.stop)
        self.client = httpx.AsyncClient(transport=httpx.ASGITransport(app=http_api.app), base_url="http://test")
        self.addAsyncCleanup(self.client.aclose)

    @staticmethod
    def _jpeg_with_exif():
        stream = BytesIO()
        exif = Image.Exif()
        exif[270] = "synthetic EXIF GPS 12.345678,98.765432"
        Image.new("RGB", (2, 2), "white").save(stream, format="JPEG", exif=exif)
        return stream.getvalue()

    def url(self, request_id=None, index=0):
        return f"/api/v1/chat/images/{quote(request_id or self.request_id, safe='')}/{index}"

    async def test_other_device_retrieves_exact_original_before_and_after_restart(self):
        first = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(first.status_code, 200)
        self.assertEqual(first.content, self.original)
        self.assertEqual(first.headers["content-type"], "image/jpeg")
        self.assertIn("no-store", first.headers["cache-control"])
        self.assertEqual(first.headers["x-content-type-options"], "nosniff")
        self.store = ReliableChatStore(self.path, self.cipher)
        second = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(second.status_code, 200)
        self.assertEqual(second.content, self.original)

    async def test_missing_wrong_owner_and_index_share_non_disclosing_404(self):
        for url, headers in (
            (self.url(), self.headers_b),
            (self.url("unknown-request"), self.headers_a),
            (self.url(index=1), self.headers_a),
            (self.url(index=-1), self.headers_a),
        ):
            with self.subTest(url=url, owner=headers):
                response = await self.client.get(url, headers=headers)
                self.assertEqual(response.status_code, 404)
                self.assertNotIn(self.image["dataBase64"], response.text)
        anonymous = await self.client.get(self.url())
        self.assertEqual(anonymous.status_code, 401)

    async def test_history_links_request_without_inlining_image(self):
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.store.commit_reply(job, snapshot, {"reply": "synthetic answer"})
        response = await self.client.get("/api/v1/chat/history", headers=self.headers_a)
        self.assertEqual(response.status_code, 200)
        items = response.json()["data"]["items"]
        user_item = next(item for item in items if item["role"] == "user")
        bot_item = next(item for item in items if item["role"] == "bot")
        self.assertEqual((user_item["requestId"], user_item["imageCount"]), (self.request_id, 1))
        self.assertEqual((user_item["contentType"], bot_item["contentType"]), ("image", "image"))
        self.assertNotIn(self.image["dataBase64"], response.text)
        self.assertNotIn(self.original.decode("latin-1"), response.text)

    async def test_unverified_restore_blocks_raw_image_until_permission_check(self):
        self.store.mark_restored()
        closed = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(closed.status_code, 503)
        with self.assertRaises(PermissionDenied):
            self.store.read_image(self.owner_a, self.request_id, 0)
        self.store.verify_restored_permissions(self.store.change_head())
        opened = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(opened.status_code, 200)
        self.assertEqual(opened.content, self.original)

    async def test_failed_batch_retains_original_image_and_recoverable_source_after_restart(self):
        self.store.cancel(self.owner_a, self.receipt["turn_id"])
        self.store = ReliableChatStore(self.path, self.cipher)
        response = await self.client.get("/api/v1/chat/history", headers=self.headers_a)
        operation = response.json()["data"]["operations"][0]
        self.assertEqual(operation["recoveryAction"], "resend_new_request")
        self.assertEqual(operation["sources"][0]["requestId"], self.request_id)
        self.assertEqual(operation["sources"][0]["imageCount"], 1)
        original = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(original.content, self.original)
        self.assertNotIn(self.image["dataBase64"], response.text)

    async def test_backup_copy_stays_closed_then_returns_original_after_permission_check(self):
        backup_path = str(pathlib.Path(self.temp.name) / "backup.sqlite3")
        authoritative_sequence = self.store.change_head()
        self.store.backup(backup_path)
        self.store = ReliableChatStore(backup_path, self.cipher)
        closed = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(closed.status_code, 503)
        self.store.verify_restored_permissions(authoritative_sequence)
        opened = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(opened.status_code, 200)
        self.assertEqual(opened.content, self.original)

    async def test_corrupt_source_digest_never_returns_image_bytes(self):
        owner_key = self.cipher.index("owner", self.owner_a)
        request_key = self.cipher.index("request", self.owner_a, self.request_id)
        with self.store._transaction() as conn:
            row = conn.execute("SELECT id,body FROM requests WHERE owner=? AND request_key=?", (owner_key, request_key)).fetchone()
            source = self.cipher.decrypt(Domain.SOURCE_MESSAGE, owner_key, row["id"], row["body"])
            source["metadata"]["images"][0]["sha256"] = "0" * 64
            conn.execute("UPDATE requests SET body=? WHERE id=?", (self.cipher.encrypt(Domain.SOURCE_MESSAGE, owner_key, row["id"], source), row["id"]))
        response = await self.client.get(self.url(), headers=self.headers_a)
        self.assertEqual(response.status_code, 503)
        self.assertNotEqual(response.content, self.original)
        self.assertNotIn(self.image["dataBase64"], response.text)

    async def test_turn_byte_budget_rejects_batch_and_splits_pending_messages(self):
        def command(identity, raw):
            image = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"), "sha256": hashlib.sha256(raw).hexdigest()}
            return ChatCommand("budget-owner", identity, identity, "device", "[图片]", channel="ws", metadata={"images": [image]})

        first, second, third = command("a", b"123456"), command("b", b"abcd"), command("c", b"z")
        with patch("services.reliable_chat_store.MAX_INLINE_IMAGE_BYTES", 10):
            with self.assertRaises(ValueError):
                self.store.accept([first, command("oversize", b"abcdef")], delay=8, merge_pending=True)
            owner_key = self.cipher.index("owner", "budget-owner")
            with self.store._transaction(write=False) as conn:
                self.assertEqual(conn.execute("SELECT COUNT(*) FROM requests WHERE owner=?", (owner_key,)).fetchone()[0], 0)
            a = self.store.accept([first], delay=8, merge_pending=True)[0]
            # Older turns had no byte counter. Recalculate from their encrypted sources.
            with self.store._transaction() as conn:
                row = conn.execute("SELECT * FROM chat_turns WHERE id=?", (a["turn_id"],)).fetchone()
                body = self.cipher.decrypt(Domain.DERIVED, owner_key, row["id"], row["body"])
                body.pop("image_bytes")
                self.store._save_turn(conn, row, body)
            b = self.store.accept([second], delay=8, merge_pending=True)[0]
            c = self.store.accept([third], delay=8, merge_pending=True)[0]
        self.assertEqual(a["turn_id"], b["turn_id"])
        self.assertNotEqual(b["turn_id"], c["turn_id"])


if __name__ == "__main__":
    unittest.main()
