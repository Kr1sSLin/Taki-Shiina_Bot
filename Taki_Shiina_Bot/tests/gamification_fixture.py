"""互动积分/等级体系测试夹具：在临时目录里装配一整套服务（含 Fake DeepSeek）。"""

from __future__ import annotations

import base64
import logging
import os
import sys
import tempfile
from datetime import date
from types import SimpleNamespace
from typing import Any

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
for _path in (PACKAGE_ROOT, CURRENT_DIR):
    if _path not in sys.path:
        sys.path.insert(0, _path)

from app_state import AppState  # noqa: E402
from handlers.interaction_handler import InteractionHandler  # noqa: E402
from jobs.points_jobs import PointsJobs  # noqa: E402
from services.gamification_service import GamificationService  # noqa: E402

TEST_USER = "test-user"
TEST_KEY = base64.urlsafe_b64encode(b"1" * 32).decode("utf-8").rstrip("=")


def setup_crypto_env() -> None:
    os.environ["DATA_ENC_KEY"] = TEST_KEY
    os.environ["DATA_AUTO_MIGRATE"] = "0"
    os.environ["DATA_BACKUP_ON_MIGRATE"] = "0"


# ==================== Fake 外部依赖 ====================

class FakeCompletions:
    def __init__(self, outcomes: list[Any]):
        # outcomes 元素为字符串（成功）或异常实例（失败）
        self.outcomes = list(outcomes)
        self.calls = 0

    async def create(self, **_kwargs):
        self.calls += 1
        if not self.outcomes:
            raise RuntimeError("no_more_outcomes")
        outcome = self.outcomes.pop(0)
        if isinstance(outcome, BaseException):
            raise outcome
        return SimpleNamespace(
            choices=[SimpleNamespace(message=SimpleNamespace(content=outcome))]
        )


class FakeDeepSeekClient:
    def __init__(self, outcomes: list[Any] | None = None):
        self.completions = FakeCompletions(outcomes or [])
        self.chat = SimpleNamespace(completions=self.completions)


class FakeWeatherService:
    async def get_weather_str(self, force: bool = True):
        return "【天气】：晴"


class FakePromptService:
    async def get_system_prompt(self, user_id: str):
        return "【人设】：椎名立希"


class FakeHistoryStore:
    def __init__(self):
        self.save_count = 0

    def save(self, _data):
        self.save_count += 1


class Rig:
    """一整套服务 + 事件收集器。"""

    def __init__(
        self,
        tmp_dir: str,
        ai_outcomes: list[Any] | None = None,
        retry_rules: dict | None = None,
        pending_text_provider=None,
        pending_flush_waiter=None,
    ):
        self.tmp_dir = tmp_dir
        self.logger = logging.getLogger("test-gamification")
        self.logger.addHandler(logging.NullHandler())
        self.gamification = GamificationService(
            base_dir=tmp_dir, logger=self.logger, default_user_id=TEST_USER
        )
        if retry_rules:
            self.gamification.config.update_sections(
                {"points_rules": {**self.gamification.config.get_points_rules(), "deepseek_retry": retry_rules}}
            )

        self.events: list[tuple[str, dict]] = []
        self.timeline: list[dict] = []
        self.alerts: list[str] = []
        self.client = FakeDeepSeekClient(ai_outcomes)
        self.state = AppState(history_file=os.path.join(tmp_dir, "chat_history.json"))
        self.history_store = FakeHistoryStore()

        self.handler = InteractionHandler(
            config_service=self.gamification.config,
            points_service=self.gamification.points,
            makeup_card_service=self.gamification.makeup,
            level_service=self.gamification.level,
            client=self.client,
            prompt_service=FakePromptService(),
            weather_service=FakeWeatherService(),
            state=self.state,
            history_store=self.history_store,
            append_timeline=self._append_timeline,
            broadcast_json=self._broadcast,
            pending_text_provider=pending_text_provider,
            pending_flush_waiter=pending_flush_waiter,
            logger=self.logger,
            alert_sender=self.alerts.append,
        )
        self.gamification.interaction = self.handler
        self.jobs = PointsJobs(
            gamification=self.gamification,
            logger=self.logger,
            broadcast_json=self._broadcast,
            alert_sender=self.alerts.append,
        )

    async def _broadcast(self, user_id: str, event: dict):
        self.events.append((user_id, event))

    async def _append_timeline(self, items: list[dict]):
        self.timeline.extend(items)

    # ---------- 便捷访问 ----------
    @property
    def points(self):
        return self.gamification.points

    @property
    def level(self):
        return self.gamification.level

    @property
    def makeup(self):
        return self.gamification.makeup

    def event_types(self) -> list[str]:
        return [event.get("type") for _user, event in self.events]

    def events_of(self, event_type: str) -> list[dict]:
        return [event for _user, event in self.events if event.get("type") == event_type]

    def ledger(self, user_id: str = TEST_USER) -> list[dict]:
        return self.points.get_history(user_id, page=1, page_size=100)["items"]


def make_rig(
    ai_outcomes: list[Any] | None = None,
    retry_rules: dict | None = None,
    pending_text_provider=None,
    pending_flush_waiter=None,
):
    """返回 (tmp_dir, rig)；调用方需在 with 语句中持有 tmp_dir 生命周期。"""
    tmp = tempfile.TemporaryDirectory()
    setup_crypto_env()
    rig = Rig(
        tmp.name,
        ai_outcomes=ai_outcomes,
        retry_rules=retry_rules,
        pending_text_provider=pending_text_provider,
        pending_flush_waiter=pending_flush_waiter,
    )
    return tmp, rig


def d(text: str) -> date:
    return date.fromisoformat(text)
