"""Legacy context quarantine and recovery gate probes with synthetic data."""
import asyncio
import base64
from io import StringIO
import logging
import os
import pathlib
import runpy
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, Mock, patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from app_state import AppState
from handlers.chat_handler import create_chat_handler
from secure_storage import DataCorruptionError, DataDecryptError, DataReadError
from services.application_service import ChatApplicationService
from services.contracts import Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import PermissionDenied
from services.memory_service import MemoryService
from services.permission_guard import PermissionGuard, require_supported_telegram_runtime
from services.privacy_logging import install_privacy_logging
from services.prompt_service import PromptService
from services.reliable_chat_store import ReliableChatStore


class PermissionGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.key = b"g" * 32
        self.environment = patch.dict(os.environ, {
            "DATA_ENC_KEY": base64.urlsafe_b64encode(self.key).decode(),
            "DATA_AUTO_MIGRATE": "0", "DATA_BACKUP_ON_MIGRATE": "0",
            "FOUNDATION_ENABLED": "0", "BOT_DATA_DIR": self.temp.name,
        })
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.path = pathlib.Path(self.temp.name) / "foundation.sqlite3"
        self.store = None

    def sql_store(self):
        self.store = ReliableChatStore(str(self.path), DomainCipher(self.key))
        return self.store

    def disable_fact(self):
        store = self.store or self.sql_store()
        store.put_fact("owner", "fact-1", Domain.USER_FACT, {"text": "synthetic-private-fact", "enabled": True}, expected_revision=0)
        store.set_permission("owner", "fact-1", enabled=False, expected_revision=1)

    def service(self):
        memory = MemoryService(self.temp.name)
        memory.update_profile("owner", "synthetic-private-profile")
        cache = {"owner": {"scene": "synthetic-private-scene"}}
        client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=AsyncMock())))
        service = PromptService(client, memory, AsyncMock(), cache, "synthetic-private-memo", logging.getLogger("synthetic.permission"))
        return memory, service, client

    def test_no_database_preserves_legacy_behavior_without_creating_sql(self):
        guard = PermissionGuard(self.temp.name)
        self.assertTrue(guard.owner_context_allowed("owner"))
        require_supported_telegram_runtime(self.temp.name)
        self.assertFalse(self.path.exists())

    def test_disabled_owner_is_quarantined_even_when_business_switch_is_off(self):
        guard = PermissionGuard(self.temp.name)
        self.assertTrue(guard.owner_context_allowed("owner"))
        self.disable_fact()
        self.assertFalse(guard.owner_context_allowed("owner"))
        self.assertFalse(guard.owner_context_allowed("other-owner"))
        self.store.set_permission("owner", "fact-1", enabled=True, expected_revision=2)
        self.assertFalse(guard.owner_context_allowed("owner"))

    def test_disabled_profile_scene_memo_and_fixed_name_do_not_reenter_prompt(self):
        memory, prompt, client = self.service()
        self.disable_fact()
        self.assertEqual(memory.get_profile("owner"), "")
        with self.assertRaises(PermissionDenied):
            memory.update_profile("owner", "synthetic-private-reintroduction")
        result = asyncio.run(prompt.get_system_prompt("owner"))
        self.assertIn("椎名立希", result)
        self.assertNotIn("synthetic-private", result)
        self.assertNotIn("Kris", result)
        self.assertNotIn("owner", prompt.scene_cache)
        client.chat.completions.create.assert_not_awaited()
        self.assertEqual(asyncio.run(prompt.get_random_scene("owner")), "")
        self.assertEqual(asyncio.run(prompt.refresh_scene("owner")), "")
        self.assertEqual(asyncio.run(prompt.generate_scene("owner")), "")

    def test_application_exposes_false_without_blocking_safe_current_input(self):
        memory, prompt, client = self.service()
        self.disable_fact()
        weather = SimpleNamespace(get_weather_str=AsyncMock(return_value="合成天气"))
        service = ChatApplicationService(AppState(), Mock(), prompt, weather)
        _, system = asyncio.run(service.system_context("owner"))
        self.assertFalse(service.legacy_allowed)
        self.assertNotIn("synthetic-private", system)
        messages = service.messages(system, [], "safe anchor", {"role": "user", "content": "当前输入可以聊天"})
        self.assertEqual(messages[-1]["content"], "当前输入可以聊天")
        client.chat.completions.create.assert_not_awaited()

    def test_unverified_restore_raises_for_all_context_and_scene_paths(self):
        memory, prompt, _ = self.service()
        store = self.sql_store()
        store.mark_restored()
        with self.assertRaises(PermissionDenied):
            memory.get_profile("owner")
        for call in (
            lambda: prompt.get_system_prompt("owner"),
            lambda: prompt.get_system_prompt("owner", allow_private_context=False),
            lambda: prompt.get_random_scene("owner"), lambda: prompt.refresh_scene("owner"),
            lambda: prompt.generate_scene("owner"),
        ):
            with self.subTest(call=call), self.assertRaises(PermissionDenied):
                asyncio.run(call())
        weather = SimpleNamespace(get_weather_str=AsyncMock())
        service = ChatApplicationService(AppState(), Mock(), prompt, weather)
        with self.assertRaises(PermissionDenied):
            asyncio.run(service.system_context("owner", allow_private_context=False))
        weather.get_weather_str.assert_not_awaited()

    def test_restore_closure_during_scene_generation_is_not_swallowed(self):
        memory, prompt, client = self.service()

        async def close_restore_gate(**kwargs):
            self.sql_store().mark_restored()
            return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="synthetic-private-scene"))])

        client.chat.completions.create.side_effect = close_restore_gate
        with self.assertRaises(PermissionDenied):
            asyncio.run(prompt.refresh_scene("owner"))

    def test_existing_empty_or_foreign_sql_is_never_initialised_as_authorised(self):
        for body in (b"", b"synthetic-corrupt-database"):
            self.path.write_bytes(body)
            with self.assertRaises(DataCorruptionError):
                PermissionGuard(self.temp.name).owner_context_allowed("owner")
            self.assertEqual(self.path.read_bytes(), body)

    def test_wrong_key_is_explicit_and_missing_previously_seen_journal_fails(self):
        self.sql_store()
        with patch.dict(os.environ, {"DATA_ENC_KEY": base64.urlsafe_b64encode(b"h" * 32).decode()}):
            with self.assertRaises(DataDecryptError):
                PermissionGuard(self.temp.name).owner_context_allowed("owner")
        guard = PermissionGuard(self.temp.name)
        self.assertFalse(guard.owner_context_allowed("owner"))
        self.path.unlink()
        with self.assertRaises(DataReadError):
            guard.owner_context_allowed("owner")

    def test_existing_sql_refuses_old_telegram_start_even_when_switch_off(self):
        self.sql_store()
        with self.assertRaisesRegex(RuntimeError, "unified_api"):
            require_supported_telegram_runtime(self.temp.name)

    def test_bot_entry_refuses_before_initialising_model_client_or_legacy_stores(self):
        self.sql_store()
        with patch("dotenv.load_dotenv"), patch("openai.AsyncOpenAI") as model_client, patch("services.memory_service.MemoryService") as memory:
            with self.assertRaisesRegex(RuntimeError, "unified_api"):
                runpy.run_path(str(pathlib.Path(__file__).resolve().parents[1] / "bot.py"), run_name="__privacy_startup_probe__")
        model_client.assert_not_called()
        memory.assert_not_called()

    def test_old_chat_factory_quarantines_history_and_skips_extraction(self):
        memory, prompt, client = self.service()
        self.disable_fact()
        client.chat.completions.create.return_value = SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="合成回复"))])
        history = {"owner": [{"role": "user", "content": "synthetic-private-history"}]}
        queued = []

        def run_once(callback, *args, **kwargs):
            queued.append(callback)
            return Mock()

        context = SimpleNamespace(
            chat_data={}, job_queue=SimpleNamespace(run_once=run_once),
            bot=SimpleNamespace(send_chat_action=AsyncMock(), send_message=AsyncMock()),
        )
        update = SimpleNamespace(message=SimpleNamespace(text="当前输入足够长用于测试", message_id=1), effective_chat=SimpleNamespace(id="owner"), effective_user=SimpleNamespace(username="synthetic", first_name="synthetic"))
        handler = create_chat_handler(
            "owner", AsyncMock(), {}, {}, {}, {}, 8, 20, 40, history, Mock(), client,
            prompt.get_system_prompt, AsyncMock(return_value="天气"), {}, {}, Mock(), memory,
            lambda value: value, lambda value: value, logging.getLogger("synthetic.old_chat"),
        )

        async def scenario():
            await handler(update, context)
            context.job = SimpleNamespace(chat_id="owner", data={"token": context.chat_data["debounce_token"]})
            with patch("handlers.chat_handler.asyncio.sleep", new=AsyncMock()):
                await queued[0](context)
            await asyncio.sleep(0)

        asyncio.run(scenario())
        client.chat.completions.create.assert_awaited_once()
        messages = client.chat.completions.create.await_args.kwargs["messages"]
        self.assertNotIn("synthetic-private", str(messages))
        self.assertIn("当前输入足够长用于测试", str(messages))
        self.assertEqual(len(memory.data["owner"]), 1)
        self.assertEqual(history["owner"][0]["content"], "synthetic-private-history")


class GlobalPrivacyLoggingTests(unittest.TestCase):
    def test_uvicorn_access_formatter_is_replaced_without_restoring_sensitive_http_args(self):
        from uvicorn.logging import AccessFormatter

        factory = logging.getLogRecordFactory()
        loggers = [logging.getLogger(), *[item for item in list(logging.Logger.manager.loggerDict.values()) if isinstance(item, logging.Logger)]]
        previous_config = {handler: (list(handler.filters), handler.formatter) for logger in loggers for handler in logger.handlers}
        logger = logging.getLogger("synthetic.uvicorn.access")
        previous_handlers, previous_propagate, previous_level = list(logger.handlers), logger.propagate, logger.level
        output = StringIO()
        handler = logging.StreamHandler(output)
        handler.setFormatter(AccessFormatter('%(client_addr)s - "%(request_line)s" %(status_code)s', use_colors=False))
        logger.handlers, logger.propagate, logger.level = [handler], False, logging.INFO
        try:
            install_privacy_logging()
            self.assertNotIsInstance(handler.formatter, AccessFormatter)
            record = logger.makeRecord(
                logger.name, logging.INFO, "", 1, '%s - "%s %s HTTP/%s" %d',
                ("synthetic-private-client", "GET", "/synthetic-private?lat=12.345678&lon=98.765432", "1.1", 200), None,
            )
            self.assertEqual(record.args, ())
            # Direct format verifies no swallowed Handler.handleError failure.
            rendered = handler.format(handler.filters[0].filter(record))
            handler.handle(record)
            for text in (rendered, output.getvalue()):
                self.assertIn("event=synthetic.uvicorn.access exception=none", text)
                self.assertNotIn("synthetic-private", text)
                self.assertNotIn("12.345678", text)
                self.assertNotIn("98.765432", text)
        finally:
            logging.setLogRecordFactory(factory)
            logger.handlers, logger.propagate, logger.level = previous_handlers, previous_propagate, previous_level
            for configured_handler, (filters, formatter) in previous_config.items():
                configured_handler.filters[:] = filters
                configured_handler.setFormatter(formatter)

    def test_existing_root_third_party_and_later_handlers_remove_sensitive_bodies(self):
        factory = logging.getLogRecordFactory()
        root = logging.getLogger()
        root_level = root.level
        existing_handlers = {handler: list(handler.filters) for logger in [root, *[item for item in logging.Logger.manager.loggerDict.values() if isinstance(item, logging.Logger)]] for handler in logger.handlers}
        name = "synthetic.third_party.guard"
        third = logging.getLogger(name)
        prior_handlers, prior_propagate, prior_level = list(third.handlers), third.propagate, third.level
        root_output, third_output, later_output = StringIO(), StringIO(), StringIO()
        root_handler = logging.StreamHandler(root_output)
        third_handler = logging.StreamHandler(third_output)
        later_handler = logging.StreamHandler(later_output)
        root.addHandler(root_handler)
        third.handlers = [third_handler]
        third.propagate = True
        third.setLevel(logging.ERROR)
        try:
            install_privacy_logging()
            install_privacy_logging()
            third.addHandler(later_handler)
            try:
                raise OSError("synthetic-private-exception 12.345678,98.765432")
            except OSError:
                third.exception("synthetic-private-model %s", "12.345678,98.765432", extra={"body": "synthetic-private-extra"})
            for output in (root_output, third_output, later_output):
                text = output.getvalue()
                self.assertIn("exception=OSError", text)
                self.assertNotIn("synthetic-private", text)
                self.assertNotIn("12.345678", text)
            self.assertEqual(len(root_handler.filters), 1)
            self.assertEqual(len(third_handler.filters), 1)
        finally:
            logging.setLogRecordFactory(factory)
            root.removeHandler(root_handler)
            root.setLevel(root_level)
            third.handlers, third.propagate, third.level = prior_handlers, prior_propagate, prior_level
            for handler, filters in existing_handlers.items():
                handler.filters[:] = filters


if __name__ == "__main__":
    unittest.main()
