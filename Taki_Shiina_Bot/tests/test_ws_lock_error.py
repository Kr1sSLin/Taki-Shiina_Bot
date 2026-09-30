"""Storage lock timeouts must not be classified as AI timeouts."""

import unittest
from unittest.mock import AsyncMock, patch

from ws_api_import_fixture import ensure_ws_api_env

ensure_ws_api_env()
import ws_api
from services.file_lock import FileLockTimeoutError


class WsLockErrorTest(unittest.IsolatedAsyncioTestCase):
    async def test_lock_timeout_reports_service_unavailable(self):
        user = "lock-timeout-test"
        # Inject at an awaited boundary inside the real handler's try block.
        with patch.object(ws_api, "message_buffer", {user: [{"requestId": "req", "content": "hello"}]}), patch.object(ws_api, "is_processing", {}), patch.object(ws_api, "pending_flush", {}), patch.object(ws_api, "broadcast_json", AsyncMock(side_effect=[FileLockTimeoutError("private path"), None])), patch.object(ws_api, "send_error", AsyncMock()) as error:
            await ws_api.process_buffered_messages(user)
            self.assertEqual(error.await_args.args[1], "SERVICE_UNAVAILABLE")
            self.assertNotIn("private path", error.await_args.args[2])
