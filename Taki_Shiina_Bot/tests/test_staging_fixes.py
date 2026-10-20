"""Regression cases from the 2026-10-20 staging report; no external calls."""
import asyncio
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from services.interaction_config_service import ConfigValidationError, default_config, validate_points_rules
from services.weather_service import WeatherService
from services.text_generation import TextGenerator


class StagingSettingsTests(unittest.TestCase):
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
