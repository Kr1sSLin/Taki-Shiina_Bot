"""
互动积分 · 等级体系 —— 服务容器（装配层）

把配置、积分、等级、补签卡、互动发送处理器装配成一个对象，供
``api/v1`` 路由与 ``jobs/points_jobs.py`` 共用，避免各处重复组装依赖。

对应 PRD §3.2 建议的分层：

- ``services/interaction_config_service.py``  后台可配置项（物品 / 模板 / 等级 / 规则）
- ``services/points_service.py``              积分余额与流水、每日首次、连续 3 天、纪念日、退款
- ``services/level_service.py``               连续陪伴天数推算与等级判定
- ``services/makeup_card_service.py``         补签卡发放与使用
- ``handlers/interaction_handler.py``         互动发送（扣分 → Prompt → DeepSeek → WS 推送）
"""

from __future__ import annotations

import os
import time
from datetime import date
from typing import Any

from services.interaction_config_service import InteractionConfigService
from services.level_service import SOURCE_ADMIN, LevelService
from services.makeup_card_service import MakeupCardService
from services.points_service import PointsService
from services.progress_store import LEVEL_NONE, PointsStore, ProgressStore

POINTS_DATA_FILENAME = "points_account.json"
PROGRESS_DATA_FILENAME = "progress_data.json"
CONFIG_DIR_NAME = "config"


class GamificationService:
    def __init__(self, base_dir: str, logger, default_user_id: str | None = None):
        self.base_dir = base_dir
        self.logger = logger
        self.default_user_id = default_user_id

        self.config = InteractionConfigService(
            config_dir=os.path.join(base_dir, CONFIG_DIR_NAME), logger=logger
        )
        self.points_store = PointsStore(os.path.join(base_dir, POINTS_DATA_FILENAME), logger)
        self.progress_store = ProgressStore(os.path.join(base_dir, PROGRESS_DATA_FILENAME), logger)
        self.level = LevelService(self.progress_store, self.config, logger)
        self.points = PointsService(self.points_store, self.progress_store, self.level, self.config, logger)
        self.makeup = MakeupCardService(self.progress_store, self.level, self.config, logger)
        self.makeup.points_service = self.points
        # 由 ws_api 在构造后注入（需要 DeepSeek 客户端、历史与 WS 推送依赖）
        self.interaction = None

    # ==================== 用户集合（定时任务用） ====================
    def known_user_ids(self) -> list[str]:
        users: set[str] = set()
        progress_data = self.progress_store.snapshot()
        users.update(progress_data.get("user_progress", {}).keys())
        for card in progress_data.get("makeup_cards", []):
            if card.get("user_id"):
                users.add(str(card["user_id"]))
        points_data = self.points_store.snapshot()
        users.update(points_data.get("points_account", {}).keys())
        if self.default_user_id:
            users.add(self.default_user_id)
        return sorted(user for user in users if user)

    def touch_user(self, user_id: str) -> dict[str, Any] | None:
        """用户首次出现时初始化进度并发放当月补签卡（需求方确认）。"""
        with self.progress_store.transaction() as data:
            ProgressStore.ensure_progress(data, user_id)
        return self.makeup.ensure_initial_grant(user_id)

    # ==================== 测试/运维：进度构造（不产生积分流水） ====================
    def backfill_activity(
        self,
        user_id: str,
        days: int,
        today=None,
        award_points: bool = False,
    ) -> dict[str, Any]:
        """把最近 N 天（含今天）标记为「有效对话」，用于免等天数测试等级/断签。

        默认**不发放积分**（不写 points_ledger），只影响连续陪伴天数与等级；
        需要同时验证积分发放时可传 ``award_points=True``（按天逐日补发，走正规发放链路）。
        """
        from datetime import timedelta

        from time_utils import get_business_today
        from services.progress_store import SOURCE_CHAT

        anchor = today or get_business_today()
        marked: list[str] = []
        with self.progress_store.transaction() as data:
            ProgressStore.ensure_progress(data, user_id)
            for offset in range(max(0, int(days))):
                day = (anchor - timedelta(days=offset)).isoformat()
                if ProgressStore.record_activity(data, user_id, day, SOURCE_CHAT):
                    marked.append(day)

        rewards: list[dict[str, Any]] = []
        if award_points:
            # 从最早一天开始逐日补发，保证「每日首次」「连续 3 天」按真实顺序结算
            for day_text in sorted(marked):
                result = self.points.record_user_activity(
                    user_id, source=SOURCE_CHAT, today=date.fromisoformat(day_text)
                )
                rewards.extend(result.rewards)

        change = self.level.recompute(
            user_id, today=anchor, change_source=SOURCE_ADMIN, persist=True
        )
        return {
            "userId": user_id,
            "markedDays": marked,
            "markedCount": len(marked),
            "rewards": rewards,
            "balance": self.points.get_balance(user_id),
            "level": change.to_status_dict(),
        }

    def reset_progress(
        self,
        user_id: str,
        today=None,
        clear_makeup_cards: bool = False,
    ) -> dict[str, Any]:
        """清空该账号的每日有效对话流水与等级（**保留积分余额**），用于重复测试升级/断签。"""
        from time_utils import get_business_today

        anchor = today or get_business_today()
        with self.progress_store.transaction() as data:
            keys = [
                key
                for key, entry in data["daily_activity_log"].items()
                if entry.get("user_id") == user_id
            ]
            for key in keys:
                data["daily_activity_log"].pop(key, None)
            progress = ProgressStore.ensure_progress(data, user_id)
            progress["continuous_days"] = 0
            progress["level_code"] = LEVEL_NONE
            progress["highest_level_code"] = LEVEL_NONE
            progress["last_valid_date"] = None
            progress["level_updated_at"] = None
            progress["updated_at"] = int(time.time() * 1000)
            if clear_makeup_cards:
                data["makeup_cards"] = [
                    card for card in data["makeup_cards"] if card.get("user_id") != user_id
                ]
                data["meta"].get("makeup_grant_months", {}).pop(user_id, None)
        change = self.level.recompute(
            user_id, today=anchor, change_source=SOURCE_ADMIN, persist=True
        )
        return {
            "userId": user_id,
            "removedActivities": len(keys),
            "clearMakeupCards": clear_makeup_cards,
            "balance": self.points.get_balance(user_id),
            "level": change.to_status_dict(),
        }

    # ==================== 查询（FR-11 / FR-15） ====================
    def get_level_status(self, user_id: str, today=None) -> dict[str, Any]:
        self.touch_user(user_id)
        change = self.level.get_status(user_id, today=today)
        payload = change.to_status_dict()
        payload["availableMakeupCards"] = self.makeup.get_available_count(user_id)
        return payload

    def get_points_overview(self, user_id: str, today=None) -> dict[str, Any]:
        self.touch_user(user_id)
        detail = self.points.get_balance_detail(user_id)
        change = self.level.get_status(user_id, today=today)
        return {
            "balance": detail["balance"],
            "balanceUpdatedAt": detail.get("updatedAt"),
            "level": change.to_status_dict(),
            "makeupCard": self.makeup.get_summary(user_id),
        }
