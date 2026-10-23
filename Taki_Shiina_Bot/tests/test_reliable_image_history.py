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

    async def test_cursor_scans_10050_rows_with_index_replay_and_incremental_append(self):
        key = self.cipher.index("owner", self.owner_a)
        expected = {}
        with self.store._transaction() as conn:
            for index in range(10050):
                item = {"messageId": f"history-{index:05}", "userId": self.owner_a, "role": "user",
                        "content": f"synthetic {index}", "timestamp": index // 2}
                expected[item["messageId"]] = item
                conn.execute("INSERT INTO chat_messages(id,owner,turn_id,body) VALUES (?,?,?,?)",
                             (item["messageId"], key, self.receipt["turn_id"], self.cipher.encrypt(Domain.SOURCE_MESSAGE, key, item["messageId"], item)))
            plan = conn.execute("EXPLAIN QUERY PLAN SELECT * FROM chat_messages WHERE owner=? AND seq>? AND seq<=? ORDER BY seq LIMIT ?", (key, 0, 10050, 500)).fetchall()
            self.assertTrue(any("chat_messages_owner" in row["detail"] for row in plan))
        cursor, actual, pages = "start", {}, 0
        while True:
            response = await self.client.get("/api/v1/chat/history", params={"cursor": cursor, "limit": 500}, headers=self.headers_a)
            self.assertEqual(response.status_code, 200)
            page = response.json()["data"]
            self.assertEqual(page["historySyncVersion"], 1)
            if pages == 0:
                replay = await self.client.get("/api/v1/chat/history", params={"cursor": cursor, "limit": 500}, headers=self.headers_a)
                self.assertEqual(replay.json()["data"]["items"], page["items"])
                with self.store._transaction() as conn:
                    item = {"messageId": "during-scan", "userId": self.owner_a, "role": "user", "content": "new during scan", "timestamp": 0}
                    conn.execute("INSERT INTO chat_messages(id,owner,turn_id,body) VALUES (?,?,?,?)", (item["messageId"], key, self.receipt["turn_id"], self.cipher.encrypt(Domain.SOURCE_MESSAGE, key, item["messageId"], item)))
            actual.update({item["messageId"]: {k: v for k, v in item.items() if k != "historySource"} for item in page["items"]})
            pages += 1
            cursor = page["nextCursor"]
            if not page["hasMore"]: break
            self.assertLess(pages, 30)
        self.assertEqual(actual, expected)
        self.assertEqual(pages, 21)
        with self.store._transaction() as conn:
            item = {"messageId": "late", "userId": self.owner_a, "role": "user", "content": "late", "timestamp": 1}
            conn.execute("INSERT INTO chat_messages(id,owner,turn_id,body) VALUES (?,?,?,?)", ("late", key, self.receipt["turn_id"], self.cipher.encrypt(Domain.SOURCE_MESSAGE, key, "late", item)))
        self.store = ReliableChatStore(self.path, self.cipher)
        added = await self.client.get("/api/v1/chat/history", params={"cursor": cursor}, headers=self.headers_a)
        self.assertEqual([item["messageId"] for item in added.json()["data"]["items"]], ["during-scan", "late"])
        old = await self.client.get("/api/v1/chat/history?limit=2", headers=self.headers_a)
        self.assertNotIn("historySyncVersion", old.json()["data"])
        self.assertEqual(len(old.json()["data"]["items"]), 2)

    async def test_cursor_legacy_version_sql_authority_owner_tampering_and_restore_reset(self):
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.store.commit_reply(job, snapshot, {"reply": "SQL authoritative"})
        sql_items = self.store.history(self.owner_a)
        legacy = [{**sql_items[0], "content": "old duplicate"},
                  {"messageId": "legacy", "userId": self.owner_a, "role": "user", "content": "old", "timestamp": 9999999999999}]
        with patch.object(http_api, "_load_timeline_async", AsyncMock(return_value=legacy)):
            first = (await self.client.get("/api/v1/chat/history?cursor=start&limit=1", headers=self.headers_a)).json()["data"]
            self.assertEqual([item["messageId"] for item in first["items"]], ["legacy"])
            cursor = first["nextCursor"]
            for bad, headers in ((cursor, self.headers_b), (cursor[:-4] + "abcd", self.headers_a), ("x" * 5000, self.headers_a)):
                denied = await self.client.get("/api/v1/chat/history", params={"cursor": bad}, headers=headers)
                self.assertEqual((denied.status_code, denied.json()["data"]["reason"]), (409, "cursor_reset_required"))
                self.assertNotIn("SQL authoritative", denied.text)
            invalid = await self.client.get("/api/v1/chat/history?cursor=start&since=1", headers=self.headers_a)
            self.assertEqual(invalid.status_code, 400)
            second = (await self.client.get("/api/v1/chat/history", params={"cursor": cursor}, headers=self.headers_a)).json()["data"]
            self.assertEqual(second["items"][0]["content"], sql_items[0]["content"])
            legacy[:] = legacy[-1:]
            changed = await self.client.get("/api/v1/chat/history", params={"cursor": second["nextCursor"]}, headers=self.headers_a)
            self.assertEqual(changed.status_code, 409)
            restarted = (await self.client.get("/api/v1/chat/history?cursor=start", headers=self.headers_a)).json()["data"]
            self.store.mark_restored()
            self.store.verify_restored_permissions(self.store.change_head())
            restored = await self.client.get("/api/v1/chat/history", params={"cursor": restarted["nextCursor"]}, headers=self.headers_a)
            self.assertEqual(restored.status_code, 409)

    async def test_cursor_blocks_low_seq_until_deliverable_without_marking_later_reply(self):
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.store.commit_reply(job, snapshot, {"reply": "first delivery"})
        original_check = self.store._check_reply_delivery
        with patch.object(self.store, "_check_reply_delivery", side_effect=PermissionDenied("synthetic permission wait")):
            response = await self.client.get("/api/v1/chat/history?cursor=start", headers=self.headers_a)
        page = response.json()["data"]
        self.assertTrue(page["blocked"])
        self.assertTrue(page["hasMore"])
        self.assertGreaterEqual(page["pollAfterMs"], 1000)
        self.assertEqual([item["role"] for item in page["items"]], ["user"])
        with self.store._transaction(write=False) as conn:
            _, body = self.store._turn(conn, self.receipt["turn_id"])
            self.assertNotIn("dispatched_at", body)
        resumed = await self.client.get("/api/v1/chat/history", params={"cursor": page["nextCursor"]}, headers=self.headers_a)
        self.assertEqual(resumed.status_code, 200)
        self.assertEqual([item["role"] for item in resumed.json()["data"]["items"]], ["bot"])
        self.assertFalse(resumed.json()["data"]["hasMore"])
        with self.store._transaction(write=False) as conn:
            _, body = self.store._turn(conn, self.receipt["turn_id"])
            self.assertIn("dispatched_at", body)
        self.assertEqual(self.store._check_reply_delivery, original_check)

    async def test_cursor_permission_change_before_transport_does_not_publish_or_skip_reply(self):
        self.store.put_fact(self.owner_a, "display-gate", Domain.USER_FACT, {"text": "synthetic fact", "enabled": True}, expected_revision=0)
        job = self.store.claim(kinds={"chat"})
        snapshot = self.store.begin_turn(job)
        self.store.commit_reply(job, snapshot, {"reply": "must not leave owner fence"})
        original = http_api.ControlledJSONResponse.__call__

        async def change_before_transport(response, scope, receive, send):
            self.store.set_permission(self.owner_a, "display-gate", enabled=False, expected_revision=1)
            await original(response, scope, receive, send)

        with patch.object(http_api.ControlledJSONResponse, "__call__", change_before_transport):
            response = await self.client.get("/api/v1/chat/history?cursor=start", headers=self.headers_a)
        self.assertEqual(response.status_code, 503)
        self.assertIsNone(response.json()["data"])
        self.assertNotIn("must not leave owner fence", response.text)
        with self.store._transaction(write=False) as conn:
            _, body = self.store._turn(conn, self.receipt["turn_id"])
            self.assertNotIn("dispatched_at", body)
        replay = await self.client.get("/api/v1/chat/history?cursor=start", headers=self.headers_a)
        self.assertEqual(replay.status_code, 200)
        self.assertEqual([item["role"] for item in replay.json()["data"]["items"]], ["user"])
        self.assertFalse(replay.json()["data"]["hasMore"])


if __name__ == "__main__":
    unittest.main()
