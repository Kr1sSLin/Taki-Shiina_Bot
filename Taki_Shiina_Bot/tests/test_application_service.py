import asyncio
import pathlib
import sys
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from app_state import AppState
from services.application_service import ChatApplicationService, HISTORY_SEPARATOR


class ApplicationTests(unittest.TestCase):
    def test_context_preserves_protocol_windows_and_separator_policy(self):
        history = [{"role": "user", "content": str(i)} for i in range(25)]
        history.insert(22, {"role": "system", "content": HISTORY_SEPARATOR})
        http, _ = ChatApplicationService.history_context(history, limit=20, after_separator=False)
        ws, _ = ChatApplicationService.history_context(history, limit=10, after_separator=True)
        self.assertEqual(len(http), 20)
        self.assertEqual([m["content"] for m in ws], ["22", "23", "24"])

    def test_failed_history_commit_never_publishes_in_memory_success(self):
        state = AppState()
        state.user_chat_history["owner"] = [{"role": "user", "content": "original"}]
        store = Mock()
        store.save.side_effect = OSError("disk full")
        service = ChatApplicationService(state, store, None, None)
        with self.assertRaises(OSError):
            service.commit_history("owner", [{"role": "assistant", "content": "new"}])
        self.assertEqual(state.user_chat_history["owner"], [{"role": "user", "content": "original"}])

    def test_model_boundary_strips_locations_from_every_context_partition(self):
        messages = ChatApplicationService.messages("lat=31.234567", [{"role": "user", "content": "31.234567,121.765432"}], "longitude:121.765432", {"role": "user", "content": "hello 31.234567,121.765432"})
        self.assertNotIn("31.234567", str(messages))
        self.assertNotIn("121.765432", str(messages))

    def test_shared_session_serializes_http_ws_and_interaction_for_same_owner(self):
        async def scenario():
            state = AppState()
            entered = []
            active = 0
            async def run(protocol):
                nonlocal active
                async with state.sessions.session("owner"):
                    active += 1
                    self.assertEqual(active, 1)
                    entered.append(protocol)
                    await asyncio.sleep(0)
                    active -= 1
            await asyncio.gather(*(run(p) for p in ("http", "ws", "interaction")))
            self.assertEqual(len(entered), 3)
        asyncio.run(scenario())
