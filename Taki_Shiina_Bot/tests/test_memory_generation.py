"""Inspect actual TextGenerator model messages, with an offline model double."""
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import AsyncMock

from services.contracts import ChatCommand, Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import VersionConflict
from services.reliable_chat_store import ReliableChatStore
from services.text_generation import TextGenerator


class MemoryGenerationTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-memory-generator-")
        self.addCleanup(self.temp.cleanup)
        self.store = ReliableChatStore(str(Path(self.temp.name) / "memory.sqlite3"), DomainCipher(b"g"*32))
        self.store.put_fact("owner", "formal", Domain.USER_FACT, {"text": "用户喜欢咖啡", "enabled": True, "subject": "user", "weight": 6, "stage": "formal", "truth_status": "accepted"}, expected_revision=0)
        self.store.put_fact("owner", "candidate", Domain.USER_FACT, {"text": "unverified-candidate 咖啡", "enabled": True, "subject": "user", "weight": 5, "stage": "candidate", "truth_status": "accepted"}, expected_revision=0)
        self.calls = []
        self.client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=AsyncMock(side_effect=self.respond))))
        guard = SimpleNamespace(owner_context_allowed=lambda _: True)
        async def system_prompt(_owner, *, allow_private_context=True):
            self.assertFalse(allow_private_context)
            return "public-character-profile"
        prompt = SimpleNamespace(permission_guard=guard, get_system_prompt=system_prompt)
        state = SimpleNamespace(user_chat_history={"owner": [{"role": "user", "content": "private-history"}, {"role": "assistant", "content": "private-bot-paraphrase"}]})
        weather = SimpleNamespace(get_weather_str=AsyncMock(return_value="synthetic-weather"))
        self.generator = TextGenerator(state, None, prompt, weather, self.client, lambda text: (text, None, None))

    async def respond(self, **kwargs):
        self.calls.append(kwargs["messages"])
        return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="知道了。"))], usage=None)

    def begin(self, text="聊聊咖啡和春日影"):
        self.store.accept([ChatCommand("owner", "request", "client", "device", text)])
        job = self.store.claim(kinds={"chat"})
        return job, self.store.begin_turn(job)

    async def test_reply_model_sees_only_current_input_and_qualified_memory(self):
        job, snapshot = self.begin()
        result = await self.generator(snapshot, self.store)
        messages = str(self.calls)
        self.assertIn("用户喜欢咖啡", messages)
        for forbidden in ("unverified-candidate", "private-history", "private-bot-paraphrase", "只要有你在"):
            self.assertNotIn(forbidden, messages)
        self.assertEqual(len(snapshot["memory_refs"]), 1)
        self.store.commit_reply(job, snapshot, result)
        self.assertEqual(self.store.read_fact("owner", "formal")["data"]["weight"], 7)
        self.assertEqual(self.store.read_fact("owner", "candidate")["data"]["weight"], 5)

    async def test_disable_while_model_runs_fences_actual_generated_reply(self):
        async def disable(**kwargs):
            self.store.set_permission("owner", "formal", enabled=False, expected_revision=1)
            return await self.respond(**kwargs)
        self.client.chat.completions.create.side_effect = disable
        job, snapshot = self.begin()
        result = await self.generator(snapshot, self.store)
        with self.assertRaises(VersionConflict):
            self.store.commit_reply(job, snapshot, result)
        self.assertEqual(self.store.history("owner"), [])
        self.assertEqual(self.store.read_fact("owner", "formal")["data"]["weight"], 6)

    async def test_model_failure_after_selection_has_no_usage_side_effect(self):
        self.client.chat.completions.create.side_effect = TimeoutError("synthetic timeout")
        _, snapshot = self.begin()
        with self.assertRaises(TimeoutError):
            await self.generator(snapshot, self.store)
        self.assertEqual(self.store.read_fact("owner", "formal")["data"]["weight"], 6)
        self.assertEqual(self.store.history("owner"), [])
