"""Storage lock timeouts must not be classified as AI timeouts."""

import asyncio
import json
import os
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import AsyncMock, patch
from types import SimpleNamespace

from ws_api_import_fixture import ensure_ws_api_env

ensure_ws_api_env()
import ws_api
from services.file_lock import FileLockTimeoutError
from services.foundation_store import StorageBusy
from services.async_storage import gamification_call
from services.file_lock import acquire_file_lock, release_file_lock
from gamification_fixture import TEST_USER, make_rig
from fastapi import WebSocketDisconnect


class WsLockErrorTest(unittest.IsolatedAsyncioTestCase):
    async def test_lock_timeout_reports_service_unavailable(self):
        user = "lock-timeout-test"
        # Inject at an awaited boundary inside the real handler's try block.
        runtime = SimpleNamespace(accept=AsyncMock(side_effect=StorageBusy("private path")))
        with patch.object(ws_api, "get_reliable_runtime", return_value=runtime), patch.object(ws_api, "message_buffer", {user: [{"requestId": "req", "content": "hello"}]}), patch.object(ws_api, "send_error", AsyncMock()) as error:
            await ws_api.process_buffered_messages(user)
            self.assertEqual(error.await_args.args[1], "SERVICE_UNAVAILABLE")
            self.assertNotIn("private path", error.await_args.args[2])


class SyntheticSocket:
    headers = {}
    query_params = {}

    def __init__(self):
        self.incoming = asyncio.Queue()
        self.outgoing = asyncio.Queue()

    async def accept(self):
        pass

    async def receive_text(self):
        frame = await self.incoming.get()
        if frame is None:
            raise WebSocketDisconnect()
        return frame

    async def send_text(self, raw):
        self.outgoing.put_nowait(json.loads(raw))


class WsFrameTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.sockets = [SyntheticSocket(), SyntheticSocket()]
        self.auth = patch.object(ws_api, "_resolve_ws_context", return_value=SimpleNamespace(user_id=TEST_USER, device_id="synthetic"))
        self.auth.start()
        self.addCleanup(self.auth.stop)
        self.connections = patch.object(ws_api, "ACTIVE_CONNECTIONS", {})
        self.connections.start()
        self.addCleanup(self.connections.stop)
        self.tasks = [asyncio.create_task(ws_api.websocket_chat(socket)) for socket in self.sockets]
        await asyncio.sleep(0)

    async def asyncTearDown(self):
        for socket in self.sockets:
            socket.incoming.put_nowait(None)
        await asyncio.gather(*self.tasks)

    async def frame(self, frame):
        self.sockets[1].incoming.put_nowait(json.dumps(frame))
        result = await asyncio.wait_for(self.sockets[1].outgoing.get(), 1)
        self.assertTrue(self.sockets[0].outgoing.empty())
        return result

    async def test_ping_is_connection_local_and_preserves_legacy_shapes(self):
        for frame, echo in (({"type": "ping", "timestamp": 123}, 123),
                            ({"type": "ping", "payload": {"timestamp": 456}}, 456),
                            ({"type": "ping", "timestamp": 0, "payload": {"timestamp": 0}}, 0),
                            ({"type": "ping"}, None)):
            with self.subTest(frame=frame):
                pong = await self.frame(frame)
                self.assertEqual(pong["type"], "pong")
                self.assertIsInstance(pong["timestamp"], int)
                self.assertNotIn("payload", pong)
                self.assertEqual(pong.get("pingTimestamp"), echo)

    async def test_bad_frames_are_local_and_do_not_close_the_connection(self):
        self.sockets[1].incoming.put_nowait("{broken")
        malformed = await asyncio.wait_for(self.sockets[1].outgoing.get(), 1)
        self.assertEqual(malformed["payload"]["errorCode"], "INVALID_JSON")
        self.assertTrue(self.sockets[0].outgoing.empty())
        frames = [[], None, "private-marker", {"type": []}, {}, {"type": "ping", "payload": []},
                  {"type": "chat.message", "requestId": []},
                  {"type": "chat.message", "payload": {"clientMessageId": {}}},
                  {"type": "chat.message", "payload": {"content": 123}},
                  {"type": "ping", "timestamp": True}, {"type": "ping", "timestamp": "123"},
                  {"type": "ping", "timestamp": float("nan")},
                  {"type": "ping", "timestamp": 1, "payload": {"timestamp": 2}}]
        for value in frames:
            with self.subTest(frame=value):
                error = await self.frame(value)
                self.assertEqual(error["payload"]["errorCode"], "INVALID_JSON")
                self.assertNotIn("private-marker", json.dumps(error))
                self.assertEqual((await self.frame({"type": "ping"}))["type"], "pong")
        error = await self.frame({"type": "chat.message", "payload": {"content": "   "}})
        self.assertEqual(error["payload"]["errorCode"], "EMPTY_MESSAGE")
        self.assertEqual((await self.frame({"type": "unknown"}))["payload"]["errorCode"], "UNKNOWN_TYPE")

    async def test_points_file_lock_for_two_seconds_does_not_block_ping(self):
        asyncio.get_running_loop().set_default_executor(ThreadPoolExecutor(max_workers=3))
        temp, rig = make_rig()
        with temp:
            held = threading.Event()
            def hold():
                fd = os.open(rig.gamification.points_store._lock_path, os.O_CREAT | os.O_RDWR, 0o600)
                try:
                    acquire_file_lock(fd, exclusive=True, timeout=1)
                    held.set()
                    time.sleep(2)
                finally:
                    release_file_lock(fd)
                    os.close(fd)
            holder = asyncio.create_task(asyncio.to_thread(hold))
            self.assertTrue(await asyncio.to_thread(held.wait, 1))
            queries = [asyncio.create_task(gamification_call(rig.gamification.get_points_overview, TEST_USER)) for _ in range(8)]
            query = queries[0]
            await asyncio.sleep(0.02)
            self.assertFalse(query.done())
            start = time.perf_counter()
            pong = await self.frame({"type": "ping", "timestamp": 789})
            rtt = time.perf_counter() - start
            loop_start = time.perf_counter()
            await asyncio.sleep(0.01)
            lag = max(0, time.perf_counter() - loop_start - 0.01)
            print(f"P3 A3: Windows, points file lock=2s, ping RTT={rtt*1000:.2f}ms, loop lag={lag*1000:.2f}ms")
            self.assertEqual(pong["pingTimestamp"], 789)
            self.assertLess(rtt, 0.2)
            self.assertTrue(await asyncio.wait_for(asyncio.to_thread(lambda: True), 0.2))
            await holder
            self.assertTrue(all(value["balance"] >= 0 for value in await asyncio.gather(*queries)))
