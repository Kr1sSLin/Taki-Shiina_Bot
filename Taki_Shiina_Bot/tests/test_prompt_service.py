"""PRD-04 replaces random scenes with qualified daily event context."""
import asyncio
import logging
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock

from services.prompt_service import PromptService
from services.public_policy import PERSONA_VERSION, RELATIONSHIP_VERSION, public_messages, checked_completion


class PromptTests(unittest.TestCase):
    def setUp(self):
        self.client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=AsyncMock())))
        self.memory = SimpleNamespace(permission_guard=SimpleNamespace(owner_context_allowed=Mock(return_value=True)), get_profile=Mock(side_effect=AssertionError("legacy profile must not enter prompt")))
        self.cache = {"u": {"scene": "虚构的已完成活动"}}
        self.service = PromptService(self.client, self.memory, Mock(), self.cache, "unmapped memoir", logging.getLogger(__name__))

    def test_legacy_scene_endpoints_clear_cache_without_model_or_fallback_fact(self):
        for method in (self.service.get_random_scene, self.service.refresh_scene, self.service.generate_scene):
            self.assertEqual(asyncio.run(method("u")), "")
        self.assertEqual(self.cache, {})
        self.client.chat.completions.create.assert_not_called()

    def test_fixed_persona_has_no_legacy_relationship_templates_or_profiles(self):
        for allowed in (True, False):
            prompt = asyncio.run(self.service.get_system_prompt("u", allow_private_context=allowed))
            self.assertIn(PERSONA_VERSION, prompt)
            self.assertIn(RELATIONSHIP_VERSION, prompt)
            self.assertIn("友达以上，恋人未满", prompt)
            self.assertIn("不将对灯的特殊态度直接复制", prompt)
            for forbidden in ("unmapped memoir", "虚构的已完成活动", "气泡轰炸版", "核心关系 - 笨拙的爱", "想一直粘着"):
                self.assertNotIn(forbidden, prompt)
        self.memory.get_profile.assert_not_called()

    def test_a07_all_task_types_have_same_fixed_relationship_and_persona(self):
        for task in ("chat", "reply_check", "chat_rewrite", "fact_extraction", "summary", "daily_candidate", "daily_check", "image_description"):
            envelope = public_messages([], task)[0]["content"]
            self.assertIn("用户缺席不改变关系", envelope)
            self.assertIn("积分系统不改变关系", envelope)
            self.assertIn(PERSONA_VERSION, envelope)
            self.assertIn(RELATIONSHIP_VERSION, envelope)

    def test_a06_missing_or_modified_contract_blocks_actual_client_call(self):
        for task in ("chat", "summary", "chat_rewrite", "fact_extraction", "daily_candidate", "daily_check"):
            for change in ("missing", "persona", "relationship", "public_body"):
                with self.subTest(task=task, change=change):
                    messages = public_messages([], task)
                    if change == "missing":
                        messages = [{"role": "user", "content": "没有公共规则"}]
                    else:
                        old = {"persona": PERSONA_VERSION, "relationship": RELATIONSHIP_VERSION, "public_body": "严禁自行创作重大事件"}[change]
                        messages[0]["content"] = messages[0]["content"].replace(old, "changed")
                    with self.assertRaises(ValueError):
                        asyncio.run(checked_completion(self.client, messages=messages, model="synthetic"))
        self.client.chat.completions.create.assert_not_awaited()


if __name__ == "__main__":
    unittest.main()
