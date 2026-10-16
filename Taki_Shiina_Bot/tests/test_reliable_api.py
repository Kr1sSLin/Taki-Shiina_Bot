"""Real HTTP/WS adapter calls over isolated SQL and synthetic async handlers."""
import asyncio
import base64
from contextlib import closing
from copy import deepcopy
from io import BytesIO
import json
import os
import pathlib
import sqlite3
import sys
import tempfile
import unittest
from unittest.mock import AsyncMock, MagicMock, patch
from PIL import Image

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from ws_api_import_fixture import ensure_ws_api_env

ensure_ws_api_env()

import http_api
import ws_api
from app_state import AppState
from auth_utils import create_access_token
from fastapi import WebSocketDisconnect
from services.chat_runtime import ChatRuntime
from services.contracts import Domain
from services.domain_cipher import DomainCipher
from services.reliable_chat_store import ReliableChatStore
from starlette.requests import Request


class SyntheticSocket:
    def __init__(self, token, messages):
        self.headers = {"authorization": "Bearer " + token}
        self.query_params = {}
        self.messages = list(messages)
        self.sent = []
        self.accepted = False

    async def accept(self, **_kwargs):
        self.accepted = True

    async def receive_text(self):
        if self.messages:
            return json.dumps(self.messages.pop(0))
        raise WebSocketDisconnect()

    async def send_text(self, value):
        self.sent.append(json.loads(value))

    async def close(self, **_kwargs):
        pass


class ReliableApiTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-reliable-api-")
        self.addCleanup(self.temp.cleanup)
        self.path = str(pathlib.Path(self.temp.name) / "foundation.sqlite3")
        self.cipher = DomainCipher(b"a" * 32)
        self.store = ReliableChatStore(self.path, self.cipher, busy_timeout=1)
        self.model_calls, self.extractions, self.frames = [], [], []
        self.started, self.release, self.delivered = asyncio.Event(), asyncio.Event(), asyncio.Event()
        self.release.set()
        self.runtime = self.new_runtime()
        self.runtimes = [self.runtime]
        self.token = create_access_token("authenticated-owner", "http-device", "synthetic-jwt-secret", 3600)
        self.ws_token = create_access_token("authenticated-owner", "ws-device", "synthetic-jwt-secret", 3600)
        shared_state = AppState(history_file=str(pathlib.Path(self.temp.name) / "unused-history.json"))
        self.real_broadcast = ws_api.broadcast_json
        replacements = (
            (http_api, "get_reliable_runtime", lambda: self.runtime),
            (ws_api, "get_reliable_runtime", lambda: self.runtime),
            (http_api, "state", shared_state),
            (ws_api, "state", shared_state),
            (ws_api, "message_buffer", {}),
            (ws_api, "ACTIVE_CONNECTIONS", {}),
            (http_api, "_load_timeline_async", AsyncMock(return_value=[])),
            (http_api, "_load_memory_timeline_async", AsyncMock(return_value=[])),
            (http_api, "_chat_turn", AsyncMock(side_effect=AssertionError("legacy HTTP path must not run"))),
            (ws_api, "_process_buffered_messages", AsyncMock(side_effect=AssertionError("legacy WS path must not run"))),
            (ws_api, "broadcast_json", AsyncMock(side_effect=self.capture_frame)),
            (ws_api, "record_chat_activity", AsyncMock()),
        )
        for module, name, value in replacements:
            self.replace(module, name, value)
        for module in (http_api, ws_api):
            for name, value in (("AUTH_JWT_SECRET", "synthetic-jwt-secret"), ("DEVICE_ALLOWLIST", {}), ("DEVICE_TOKEN_MAP", {}), ("BOT_HTTP_TOKEN", ""), ("BOT_WS_TOKEN", "")):
                self.replace(module, name, value)

    def replace(self, module, name, value):
        replacement = patch.object(module, name, value)
        replacement.start()
        self.addCleanup(replacement.stop)

    async def asyncTearDown(self):
        self.release.set()
        await asyncio.gather(*(runtime.stop() for runtime in self.runtimes))

    def new_runtime(self):
        runtime = ChatRuntime(self.store, self.generate, self.extract, ws_api.deliver_reliable, poll_interval=0.005)
        for worker in runtime.workers:
            worker.retry_delay = 0.01
        return runtime

    async def generate(self, snapshot, _store):
        self.model_calls.append(deepcopy(snapshot))
        self.started.set()
        await self.release.wait()
        return {"reply": "synthetic committed reply", "model": "synthetic", "usage": {"totalTokens": 1}}

    async def extract(self, text):
        self.extractions.append(text)
        return []

    async def capture_frame(self, owner, frame):
        self.frames.append((owner, deepcopy(frame)))
        if frame.get("type") == "chat.reply.stream":
            self.delivered.set()
        return 1

    def request(self):
        request = Request({"type": "http", "method": "POST", "path": "/api/v1/chat", "headers": []})
        request.state.trace_id = "synthetic-trace"
        return request

    def payload(self, identity="shared-request", *, text="synthetic input"):
        return http_api.ChatRequest(requestId=identity, clientMessageId=identity, userId="spoofed-owner", message=text)

    async def http_chat(self, identity="shared-request", *, text="synthetic input"):
        return await http_api.chat(self.payload(identity, text=text), self.request(), authorization="Bearer " + self.token)

    def body(self, response):
        return json.loads(response.body) if hasattr(response, "body") else response

    def counts(self):
        with closing(sqlite3.connect(self.path)) as conn:
            return {table: conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0] for table in ("requests", "chat_turns", "chat_messages", "events", "outbox")}

    async def wait_for_jobs(self):
        async def settled():
            while True:
                with closing(sqlite3.connect(self.path)) as conn:
                    count = conn.execute("SELECT COUNT(*) FROM jobs WHERE status NOT IN ('succeeded','failed','cancelled')").fetchone()[0]
                if not count:
                    return
                await asyncio.sleep(0.005)
        await asyncio.wait_for(settled(), timeout=3)

    def queue_ws(self, identity="shared-request", *, text="synthetic input"):
        ws_api.message_buffer["authenticated-owner"] = [{"requestId": identity, "clientMessageId": identity, "deviceId": "ws-device", "content": text, "images": []}]

    async def test_same_request_concurrent_http_ws_commits_and_delivers_one_result(self):
        self.release.clear()
        http_task = asyncio.create_task(self.http_chat())
        await asyncio.wait_for(self.started.wait(), 2)
        self.queue_ws()
        ws_task = asyncio.create_task(ws_api.process_buffered_messages("authenticated-owner"))
        await asyncio.sleep(0.03)
        self.assertFalse(http_task.done())
        self.assertFalse(ws_task.done())
        self.assertEqual(self.frames, [], "unfinished model drafts must not reach clients")
        self.release.set()
        response, _ = await asyncio.wait_for(asyncio.gather(http_task, ws_task), 3)
        self.assertEqual(response.status_code, 200)
        await self.wait_for_jobs()
        self.assertEqual((len(self.model_calls), len(self.extractions)), (1, 1))
        self.assertEqual(self.counts(), {"requests": 1, "chat_turns": 1, "chat_messages": 2, "events": 1, "outbox": 1})
        with closing(sqlite3.connect(self.path)) as conn:
            kinds = [self.cipher.decrypt(Domain.JOB, row["owner"], row["id"], row["body"])["kind"] for row in self._job_rows(conn)]
        self.assertEqual(sorted(kinds), ["chat", "deliver", "extract"])
        final_frames = [frame for _, frame in self.frames if frame["type"] == "chat.reply.stream"]
        self.assertEqual(len(final_frames), 1)
        self.assertTrue(final_frames[0]["payload"]["done"])
        self.assertEqual(final_frames[0]["payload"]["delta"], "")
        self.assertEqual(final_frames[0]["payload"]["messageId"], self.body(response)["data"]["messageId"])

    @staticmethod
    def _job_rows(conn):
        conn.row_factory = sqlite3.Row
        return conn.execute("SELECT id,owner,body FROM jobs").fetchall()

    async def test_http_authenticated_owner_overrides_payload_and_conflicts_are_409(self):
        response = await self.http_chat()
        self.assertEqual(response.status_code, 200)
        self.assertEqual(self.model_calls[0]["owner"], "authenticated-owner")
        self.assertEqual(self.model_calls[0]["sources"][0]["owner"], "authenticated-owner")
        history = self.store.history("authenticated-owner")
        self.assertEqual(len(history), 2)
        self.assertTrue(all(item["userId"] == "authenticated-owner" for item in history))
        self.assertEqual(self.store.history("spoofed-owner"), [])
        conflict = await self.http_chat(text="changed input")
        self.assertEqual(conflict.status_code, 409)
        self.assertEqual(len(self.model_calls), 1)

    async def test_reply_commit_failure_never_returns_success_or_publishes_draft(self):
        original = self.store._change

        def fail_after_outbox(*args, **kwargs):
            original(*args, **kwargs)
            raise OSError("synthetic disk failure after outbox")

        with patch.object(self.store, "_change", side_effect=fail_after_outbox):
            response = await asyncio.wait_for(self.http_chat(), 4)
        self.assertEqual(response.status_code, 503)
        self.assertIsNone(self.body(response)["data"])
        self.assertEqual((self.counts()["chat_messages"], self.counts()["events"], self.counts()["outbox"]), (0, 0, 0))
        self.assertEqual(len(self.model_calls), 3)
        self.assertFalse(any(frame.get("type") == "chat.reply.stream" for _, frame in self.frames))

    async def test_retry_after_runtime_restart_replays_original_message_without_regeneration(self):
        first = await self.http_chat()
        await self.wait_for_jobs()
        original = self.body(first)["data"]
        await self.runtime.stop()
        self.store = ReliableChatStore(self.path, self.cipher, busy_timeout=1)
        self.runtime = self.new_runtime()
        self.runtimes.append(self.runtime)
        second = await self.http_chat()
        self.assertEqual(second.status_code, 200)
        replay = self.body(second)["data"]
        for identity in ("messageId", "eventId", "deliveryId"):
            self.assertEqual(replay[identity], original[identity])
        self.assertEqual((len(self.model_calls), len(self.extractions)), (1, 1))
        self.assertEqual(self.counts()["chat_messages"], 2)

    async def test_unverified_restore_is_503_for_http_and_unavailable_for_ws(self):
        self.store.mark_restored()
        response = await self.http_chat()
        self.assertEqual(response.status_code, 503)
        self.queue_ws()
        await ws_api.process_buffered_messages("authenticated-owner")
        errors = [frame for _, frame in self.frames if frame.get("type") == "bot.error"]
        self.assertEqual(errors[-1]["payload"]["errorCode"], "SERVICE_UNAVAILABLE")
        self.assertEqual(self.model_calls, [])
        self.assertEqual(self.counts()["requests"], 0)

    async def test_restored_history_and_memory_endpoints_report_503(self):
        self.store.mark_restored()
        history = await http_api.chat_history(self.request(), authorization="Bearer " + self.token)
        facts = await http_api.memory_facts(self.request(), authorization="Bearer " + self.token)
        self.assertEqual(history.status_code, 503)
        self.assertEqual(facts.status_code, 503)

    async def test_sql_facts_are_visible_only_to_authenticated_owner_in_memory_api(self):
        self.store.put_fact("authenticated-owner", "own-fact", Domain.USER_FACT, {"text": "synthetic owned fact", "enabled": True}, expected_revision=0, targets=())
        self.store.put_fact("other-owner", "other-fact", Domain.USER_FACT, {"text": "synthetic other fact", "enabled": True}, expected_revision=0, targets=())
        response = await http_api.memory_facts(self.request(), userId="other-owner", authorization="Bearer " + self.token)
        items = self.body(response)["data"]["items"]
        self.assertEqual([item["factId"] for item in items], ["own-fact"])
        self.assertEqual(items[0]["fact"], "synthetic owned fact")

    async def test_actual_websocket_image_is_encrypted_then_replayed_from_original_source(self):
        picture = BytesIO()
        exif = Image.Exif()
        exif[270] = "synthetic EXIF GPS 12.345678,98.765432"
        Image.new("RGB", (2, 2), "white").save(picture, format="JPEG", exif=exif)
        original = picture.getvalue()
        encoded = base64.b64encode(original).decode("ascii")
        socket = SyntheticSocket(self.ws_token, [{"type": "chat.message", "requestId": "image-request", "payload": {"content": "", "userId": "spoofed-owner", "images": [{"mimeType": "image/jpeg", "dataBase64": encoded}]}}])
        with patch.object(ws_api, "GEMINI_API_KEY", "synthetic-key"), patch.object(ws_api, "schedule_debounce"):
            await ws_api.websocket_chat(socket)
        self.assertTrue(socket.accepted)
        self.assertEqual(self.counts()["requests"], 1)
        receipt = self.store.request("authenticated-owner", "image-request")
        image = receipt["source"]["metadata"]["images"][0]
        self.assertEqual(base64.b64decode(image["dataBase64"]), original)
        self.assertEqual(receipt["source"]["owner"], "authenticated-owner")
        self.assertNotIn(encoded.encode(), pathlib.Path(self.path).read_bytes())
        await self.runtime.ready("authenticated-owner", [receipt["turn_id"]])
        await asyncio.wait_for(ws_api.process_buffered_messages("authenticated-owner"), 3)
        await self.wait_for_jobs()
        self.assertEqual(len(self.model_calls), 1)
        self.assertEqual(self.model_calls[0]["sources"][0]["metadata"]["images"][0], image)
        self.assertEqual(self.extractions, [])
        final = [frame for _, frame in self.frames if frame["type"] == "chat.reply.stream"]
        self.assertEqual(len(final), 1)
        self.assertEqual(final[0]["payload"]["contentType"], "image")

    async def test_image_base64_that_looks_like_gps_text_remains_byte_exact(self):
        picture = BytesIO()
        exif = Image.Exif()
        exif[270] = "synthetic EXIF GPS 12.345678,98.765432"
        Image.new("RGB", (2, 2), "white").save(picture, format="JPEG", exif=exif)
        original = picture.getvalue()
        original += b"\0" * (-len(original) % 3) + base64.b64decode("/GPS12/A")
        encoded = base64.b64encode(original).decode("ascii")
        self.assertTrue(encoded.endswith("/GPS12/A"))
        request = {"type": "chat.message", "requestId": "gps-looking-base64", "payload": {"content": "caption", "images": [{"mimeType": "image/jpeg", "dataBase64": encoded}]}}
        with patch.object(ws_api, "GEMINI_API_KEY", "synthetic-key"), patch.object(ws_api, "schedule_debounce"):
            await ws_api.websocket_chat(SyntheticSocket(self.ws_token, [request]))
            await ws_api.websocket_chat(SyntheticSocket(self.ws_token, [request]))
        self.assertEqual(self.counts()["requests"], 1)
        saved = self.store.request("authenticated-owner", "gps-looking-base64")["source"]["metadata"]["images"][0]
        self.assertEqual(saved["dataBase64"], encoded)
        self.assertEqual(base64.b64decode(saved["dataBase64"]), original)
        self.assertFalse(any(frame["type"] == "bot.error" for _, frame in self.frames))

    async def test_invalid_image_is_rejected_before_any_persistent_source(self):
        socket = SyntheticSocket(self.ws_token, [{"type": "chat.message", "requestId": "bad-image", "payload": {"content": "", "images": [{"mimeType": "image/jpeg", "dataBase64": base64.b64encode(b"not a JPEG").decode()}]}}])
        with patch.object(ws_api, "GEMINI_API_KEY", "synthetic-key"):
            await ws_api.websocket_chat(socket)
        self.assertEqual(self.counts()["requests"], 0)
        errors = [frame for _, frame in self.frames if frame["type"] == "bot.error"]
        self.assertEqual(errors[-1]["payload"]["errorCode"], "VISION_INVALID_MIME")

    async def test_invalid_second_image_does_not_save_or_queue_first_image(self):
        picture = BytesIO()
        Image.new("RGB", (2, 2), "white").save(picture, format="JPEG")
        socket = SyntheticSocket(self.ws_token, [{"type": "chat.message", "requestId": "partial-image", "payload": {"content": "caption", "images": [
            {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(picture.getvalue()).decode()},
            {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(b"broken").decode()},
        ]}}])
        with patch.object(ws_api, "GEMINI_API_KEY", "synthetic-key"):
            await ws_api.websocket_chat(socket)
        self.assertEqual(self.counts()["requests"], 0)
        self.assertEqual(ws_api.message_buffer, {})
        self.assertFalse(any(frame["type"] in {"chat.message.echo", "chat.queued"} for _, frame in self.frames))
        errors = [frame for _, frame in self.frames if frame["type"] == "bot.error"]
        self.assertEqual(errors[-1]["payload"]["errorCode"], "VISION_INVALID_MIME")

    async def test_inline_budget_rejects_whole_multi_image_message(self):
        picture = BytesIO()
        Image.new("RGB", (2, 2), "white").save(picture, format="JPEG")
        raw = picture.getvalue()
        payload = {"content": "caption", "images": [
            {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode()},
            {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode()},
        ]}
        socket = SyntheticSocket(self.ws_token, [{"type": "chat.message", "requestId": "over-inline-budget", "payload": payload}])
        with patch.object(ws_api, "GEMINI_API_KEY", "synthetic-key"), patch.object(ws_api, "MAX_INLINE_IMAGE_BYTES", len(raw)):
            await ws_api.websocket_chat(socket)
        self.assertEqual(self.counts()["requests"], 0)
        self.assertEqual(ws_api.message_buffer, {})
        errors = [frame for _, frame in self.frames if frame["type"] == "bot.error"]
        self.assertEqual(errors[-1]["payload"]["errorCode"], "VISION_IMAGE_TOO_LARGE")

    async def test_decompression_bomb_is_reported_without_dropping_socket(self):
        socket = SyntheticSocket(self.ws_token, [{"type": "chat.message", "requestId": "oversized-pixels", "payload": {"images": [
            {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(b"synthetic bytes").decode()}
        ]}}])
        with patch.object(ws_api, "GEMINI_API_KEY", "synthetic-key"), patch.object(ws_api.Image, "open", side_effect=Image.DecompressionBombError("too many pixels")):
            await ws_api.websocket_chat(socket)
        self.assertEqual(self.counts()["requests"], 0)
        errors = [frame for _, frame in self.frames if frame["type"] == "bot.error"]
        self.assertEqual(errors[-1]["payload"]["errorCode"], "VISION_INVALID_MIME")

    async def test_image_without_vision_key_is_rejected_before_persistence(self):
        socket = SyntheticSocket(self.ws_token, [{"type": "chat.message", "requestId": "no-key-image", "payload": {"images": [{"mimeType": "image/jpeg", "dataBase64": "YWJj"}]}}])
        with patch.object(ws_api, "GEMINI_API_KEY", ""):
            await ws_api.websocket_chat(socket)
        self.assertEqual(self.counts()["requests"], 0)
        errors = [frame for _, frame in self.frames if frame["type"] == "bot.error"]
        self.assertEqual(errors[-1]["payload"]["errorCode"], "GEMINI_NOT_CONFIGURED")

    async def test_vision_adapter_sends_original_exif_bytes_to_gemini(self):
        picture = BytesIO()
        exif = Image.Exif()
        exif[270] = "synthetic EXIF GPS 12.345678,98.765432"
        Image.new("RGB", (2, 2), "white").save(picture, format="JPEG", exif=exif)
        original = picture.getvalue()
        model = MagicMock()
        model.generate_content.return_value.text = "synthetic picture"
        with patch.dict(os.environ, {"GEMINI_API_KEY": "synthetic-key"}), patch.object(ws_api.genai, "configure") as configure, patch.object(ws_api.genai, "GenerativeModel", return_value=model):
            description = await ws_api.describe_images([{"mimeType": "image/jpeg", "raw": original}], "caption")
        self.assertEqual(description, "synthetic picture")
        configure.assert_called_once_with(api_key="synthetic-key", transport="rest")
        parts = model.generate_content.call_args.args[0]
        self.assertIn("caption", parts[0])
        self.assertEqual(parts[1], {"mime_type": "image/jpeg", "data": original})

    async def test_http_fact_cursor_pages_past_500_equal_timestamps_and_final_disable(self):
        self.store.clock = lambda: 1000.0
        response = await self.http_chat("fact-page-preface")
        self.assertEqual(response.status_code, 200)
        await self.wait_for_jobs()
        await self.runtime.stop()
        for index in range(507):
            self.store.put_fact("authenticated-owner", f"http-page-{index:04}", Domain.USER_FACT, {"text": f"synthetic HTTP fact {index}", "enabled": True}, expected_revision=0, targets=())
            if index % 90 == 0:
                self.store.put_fact("other-owner", f"other-page-{index}", Domain.USER_FACT, {"text": "synthetic foreign fact", "enabled": True}, expected_revision=0, targets=())
        self.store.set_permission("authenticated-owner", "http-page-0000", enabled=False, expected_revision=1, targets=())
        cursor, collected, sizes = 0, [], []
        for _ in range(4):
            response = await http_api.memory_facts(self.request(), since=1_000_000, limit=500, cursor=cursor, userId="other-owner", authorization="Bearer " + self.token)
            page = self.body(response)["data"]
            self.assertGreater(page["nextCursor"], cursor)
            cursor = page["nextCursor"]
            collected.extend(page["items"])
            sizes.append(len(page["items"]))
            if not page["hasMore"]:
                break
        else:
            self.fail("HTTP fact cursor did not terminate")
        self.assertEqual(sizes, [500, 8])
        self.assertEqual(len({item["eventId"] for item in collected}), 508)
        self.assertEqual([item["sequence"] for item in collected], sorted(item["sequence"] for item in collected))
        self.assertEqual({item["timestamp"] for item in collected}, {1_000_000})
        self.assertTrue(all(item["userId"] == "authenticated-owner" and item["action"] != "reply_completed" for item in collected))
        self.assertEqual((collected[-1]["action"], collected[-1]["enabled"], collected[-1]["fact"]), ("disabled", False, ""))
        disabled_replays = [item for item in collected if item["factId"] == "http-page-0000"]
        self.assertEqual([item["eventRevision"] for item in disabled_replays], [1, 2])
        self.assertEqual([item["revision"] for item in disabled_replays], [2, 2])
        self.assertTrue(all(not item["enabled"] and item["fact"] == "" for item in disabled_replays))
        http_api._load_memory_timeline_async.assert_not_awaited()
        response = await http_api.memory_facts(self.request(), cursor=cursor, authorization="Bearer " + self.token)
        self.assertEqual(self.body(response)["data"], {"items": [], "nextCursor": cursor, "hasMore": False})


if __name__ == "__main__":
    unittest.main()
