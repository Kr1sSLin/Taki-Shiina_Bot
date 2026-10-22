"""Regression cases from the 2026-10-20 staging report; no external calls."""
import asyncio
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
import json
import logging
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import AsyncMock, patch

from services.interaction_config_service import ConfigValidationError, ConfigRevisionConflict, ConfigUnavailableError, InteractionConfigService, default_config, validate_points_rules
from gamification_fixture import make_rig, TEST_USER
from services.weather_service import WeatherService
from services.text_generation import TextGenerator


class StagingSettingsTests(unittest.TestCase):
    def test_config_recovery_restart_manual_revision_and_snapshot_isolation(self):
        tmp, rig = make_rig()
        with tmp:
            service = rig.gamification.config
            original = service.get_config()
            changed = service.update_sections({"interaction_items": [{**item, "cost_points": 17} for item in original["interaction_items"]]}, expected_revision=original["revision"])
            detached = service.get_config()
            detached["interaction_items"][0]["cost_points"] = 99
            Path(service.path).write_text("{损坏", encoding="utf-8")
            for candidate in (service, InteractionConfigService(service.config_dir, logging.getLogger("test"))):
                self.assertEqual(candidate.get_item("coffee")["cost_points"], 17)
                self.assertTrue(candidate.degraded)
            manual = {**changed, "interaction_items": [{**item, "cost_points": 19} for item in changed["interaction_items"]]}
            Path(service.path).write_text(json.dumps(manual), encoding="utf-8")
            current = service.get_config()
            self.assertGreater(current["revision"], changed["revision"])
            self.assertEqual(current["interaction_items"][0]["cost_points"], 19)
            with self.assertRaises(ConfigRevisionConflict):
                service.update_sections({"points_rules": original["points_rules"]}, expected_revision=changed["revision"])

    def test_config_rejects_templates_and_parallel_stale_writes(self):
        tmp, rig = make_rig()
        with tmp:
            service = rig.gamification.config
            original = service.get_config()
            for template in ("{unknown}", "{item_name", "{item_name.__class__}", "{cost_points:wrong}", "{}", "{cost_points:{item_name}}"):
                with self.subTest(template=template), self.assertRaises(ConfigValidationError):
                    service.update_sections({"prompt_templates": {**original["prompt_templates"], "coffee": template}})
            other = InteractionConfigService(service.config_dir, logging.getLogger("test"))
            def update(candidate):
                try:
                    return candidate.update_sections({"points_rules": original["points_rules"]}, expected_revision=original["revision"])
                except ConfigRevisionConflict:
                    return None
            with ThreadPoolExecutor(max_workers=2) as pool:
                results = list(pool.map(update, (service, other)))
            self.assertEqual(sum(result is not None for result in results), 1)
            self.assertEqual(service.get_config()["revision"], original["revision"] + 1)

    def test_unrecoverable_config_stops_new_gifts_but_keeps_refunds(self):
        tmp, rig = make_rig()
        with tmp:
            rig.points.adjust_balance(TEST_USER, 20)
            rig.points.deduct_for_item(TEST_USER, "coffee", 5, "old-paid")
            service = rig.gamification.config
            for path in (service.path, service.last_good_path):
                Path(path).write_text("{损坏", encoding="utf-8")
            restarted = InteractionConfigService(service.config_dir, logging.getLogger("test"))
            rig.points.config_service = restarted
            with self.assertRaises(ConfigUnavailableError):
                restarted.get_config()
            with self.assertRaises(ConfigUnavailableError):
                rig.points.deduct_for_item(TEST_USER, "coffee", 5, "new")
            self.assertTrue(rig.points.refund_item(TEST_USER, "coffee", 5, "old-paid").created)
            self.assertEqual(rig.points.get_balance(TEST_USER), 20)
            self.assertEqual(restarted.get_points_rules()["daily_first_chat_points"], 1)

    def test_config_dual_write_failure_is_not_reported_as_publication_success(self):
        tmp, rig = make_rig()
        with tmp:
            service = rig.gamification.config
            original = service.get_config()
            write = service._write_atomic
            def fail_current(config, path=None):
                if path is None:
                    raise OSError("合成当前配置同步失败")
                write(config, path)
            with patch.object(service, "_write_atomic", side_effect=fail_current), self.assertRaises(OSError):
                service.update_sections({"points_rules": {**original["points_rules"], "interaction_daily_limit": 2}})

    def test_default_budget_and_invalid_configuration(self):
        rules = default_config()["points_rules"]
        self.assertEqual(rules["deepseek_retry"]["total_timeout_seconds"], 60)
        self.assertEqual(rules["deepseek_retry"]["attempt_timeout_seconds"], 20)
        for field in ("total_timeout_seconds", "attempt_timeout_seconds"):
            for value in (0, -1, "nan", "inf", "invalid", None):
                with self.subTest(field=field, value=value), self.assertRaises(ConfigValidationError):
                    validate_points_rules({**rules, "deepseek_retry": {**rules["deepseek_retry"], field: value}})

    def test_invalid_city_never_overwrites_previous_setting(self):
        with tempfile.TemporaryDirectory() as directory:
            service = WeatherService(directory, "", "", "")
            service.set_city("北京")
            for city in ("", "39。9042116，4。074", "39.9042,116.4074", "91,181", "NaN,Infinity", "北京，", "1234", "北京\n上海"):
                with self.subTest(city=city), self.assertRaises(ValueError):
                    service.set_city(city)
                self.assertEqual(Path(service.city_file).read_text(encoding="utf-8"), "北京")
            for city in ("上海", "New York", "St. Louis", "Xi'an", "101010100", "auto_ip"):
                service.set_city(city)
                self.assertEqual(service.get_current_city(), city)


class InteractionBudgetTests(unittest.IsolatedAsyncioTestCase):
    async def test_single_generation_can_use_twenty_seconds_not_fifteen(self):
        generator = TextGenerator(None, None, None, None, None, None)
        response = SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="收下了。"))])
        invoke = AsyncMock(return_value=response)
        actual_wait_for = asyncio.wait_for
        timeouts = []

        async def wait_for(coro, *, timeout):
            timeouts.append(timeout)
            return await actual_wait_for(coro, timeout=timeout)

        with patch("services.text_generation.asyncio.wait_for", side_effect=wait_for):
            result, attempts = await generator._interaction_response([], AsyncMock(), invoke,
                {"max_retries": 5, "total_timeout_seconds": 60, "attempt_timeout_seconds": 20})
        self.assertIs(result, response)
        self.assertEqual(attempts, 1)
        self.assertEqual(timeouts, [20])
