"""API 层集成测试（PRD §8 接口清单 / FR-13 鉴权与幂等 / 6.2 补签流程）。

直接装配一个只包含新路由的 FastAPI app，避免依赖 DeepSeek/Gemini 等运行时依赖。
"""

from __future__ import annotations

import asyncio
import unittest
from unittest.mock import patch

from fastapi import FastAPI
from fastapi.testclient import TestClient

from gamification_fixture import TEST_USER, d, make_rig  # noqa: F401
from api.v1.admin import router as admin_router
from api.v1.deps import AuthRuntime, configure_auth, register_gamification
from api.v1.interaction import router as interaction_router
from api.v1.level import router as level_router
from api.v1.points import router as points_router

TEST_TOKEN = "test-access-token"
USER_HEADER = {"Authorization": f"Bearer {TEST_TOKEN}"}
AUTH_HEADER = {**USER_HEADER, "X-Admin-Token": "test-admin-token"}
ADMIN_HEADER = AUTH_HEADER


class ApiEndpointsTestCase(unittest.TestCase):
    def setUp(self):
        self._admin_api_env = patch.dict(
            "os.environ", {"ENABLE_ADMIN_TEST_API": "1", "ADMIN_API_TOKEN": "test-admin-token"}
        )
        self._admin_api_env.start()
        self.addCleanup(self._admin_api_env.stop)
        self._tmp, self.rig = make_rig(
            ai_outcomes=["谁让你买的。\n收下了。"],
            retry_rules={"max_retries": 1, "total_timeout_seconds": 1.0, "attempt_timeout_seconds": 0.2},
        )
        self.addCleanup(self._tmp.cleanup)

        app = FastAPI()
        app.include_router(interaction_router, prefix="/api/v1")
        app.include_router(points_router, prefix="/api/v1")
        app.include_router(level_router, prefix="/api/v1")
        app.include_router(admin_router, prefix="/api/v1")
        configure_auth(
            AuthRuntime(
                jwt_secret="",  # 使用 legacy token 映射，便于测试
                legacy_token_map=None,
                fallback_tokens=[TEST_TOKEN],
                default_user_id=TEST_USER,
            )
        )
        register_gamification(self.rig.gamification)
        self.client = TestClient(app)

    def test_admin_test_api_can_be_disabled(self):
        with patch.dict("os.environ", {"ENABLE_ADMIN_TEST_API": "0"}):
            response = self.client.post(
                "/api/v1/admin/points/adjust", headers=ADMIN_HEADER, json={"amount": 1}
            )
        self.assertEqual(response.status_code, 403)
        self.assertEqual(response.json()["code"], 40301)

    def test_admin_requires_independent_token_in_addition_to_ordinary_auth(self):
        missing = self.client.get("/api/v1/admin/gamification-config", headers=USER_HEADER)
        wrong = self.client.get(
            "/api/v1/admin/gamification-config",
            headers={**AUTH_HEADER, "X-Admin-Token": "wrong"},
        )
        self.assertEqual(missing.status_code, 403)
        self.assertEqual(wrong.status_code, 403)

    def set_balance(self, amount: int):
        from services.progress_store import PointsStore

        with self.rig.gamification.points_store.transaction() as data:
            account = PointsStore.ensure_account(data, TEST_USER)
            account["balance"] = amount

    # ---------- 鉴权 ----------
    def test_requires_auth(self):
        response = self.client.get("/api/v1/interaction/items")
        self.assertEqual(response.status_code, 401)
        # FastAPI 会把 HTTPException 的 detail 包在 detail 字段里（与既有 http_api 行为一致）
        self.assertEqual(response.json()["detail"]["code"], 40101)

    # ---------- 菜单与发送 ----------
    def test_interaction_items_endpoint(self):
        self.set_balance(20)
        response = self.client.get("/api/v1/interaction/items", headers=AUTH_HEADER)
        self.assertEqual(response.status_code, 200)
        body = response.json()
        self.assertEqual(body["code"], 0)
        self.assertEqual(body["data"]["balance"], 20)
        self.assertEqual(len(body["data"]["items"]), 5)
        self.assertEqual(body["data"]["items"][0]["id"], "coffee")
        self.assertIn("traceId", body)

    def test_interaction_send_success_and_insufficient(self):
        self.set_balance(6)
        ok_response = self.client.post(
            "/api/v1/interaction/send",
            headers=AUTH_HEADER,
            json={"itemId": "coffee", "requestId": "api-req-1"},
        )
        self.assertEqual(ok_response.status_code, 200)
        ok_body = ok_response.json()
        self.assertEqual(ok_body["code"], 0)
        self.assertTrue(ok_body["data"]["success"])
        self.assertEqual(ok_body["data"]["charged"], 5)
        self.assertIn("谁让你买的", ok_body["data"]["reply"])

        # 余额 = 6 - 5 + 1（每日首次）
        balance = self.client.get("/api/v1/points/balance", headers=AUTH_HEADER).json()
        self.assertEqual(balance["data"]["balance"], 2)

        insufficient = self.client.post(
            "/api/v1/interaction/send",
            headers=AUTH_HEADER,
            json={"itemId": "white_dragon", "requestId": "api-req-2"},
        )
        self.assertEqual(insufficient.status_code, 200)
        insufficient_body = insufficient.json()
        self.assertEqual(insufficient_body["code"], 40201)
        self.assertFalse(insufficient_body["data"]["success"])
        self.assertEqual(insufficient_body["data"]["errorCode"], "INSUFFICIENT_POINTS")
        self.assertEqual(insufficient_body["data"]["charged"], 0)

    def test_interaction_send_missing_item_id(self):
        response = self.client.post(
            "/api/v1/interaction/send", headers=AUTH_HEADER, json={"requestId": "x"}
        )
        self.assertEqual(response.status_code, 400)
        self.assertEqual(response.json()["code"], 40001)

    def test_interaction_send_is_idempotent(self):
        self.set_balance(20)
        first = self.client.post(
            "/api/v1/interaction/send",
            headers=AUTH_HEADER,
            json={"itemId": "coffee", "requestId": "same-key"},
        ).json()
        second = self.client.post(
            "/api/v1/interaction/send",
            headers=AUTH_HEADER,
            json={"itemId": "coffee", "requestId": "same-key"},
        ).json()
        self.assertTrue(first["data"]["success"])
        self.assertTrue(second["data"]["duplicate"])
        ledger = self.rig.ledger()
        self.assertEqual(len([e for e in ledger if e["reason_code"] == "ITEM_SEND"]), 1)

    # ---------- 等级 / 流水 ----------
    def test_level_status_and_config(self):
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-03"))
        status = self.client.get("/api/v1/level/status", headers=AUTH_HEADER).json()
        self.assertEqual(status["code"], 0)
        self.assertIn("continuousDays", status["data"])
        self.assertIn("availableMakeupCards", status["data"])

        config = self.client.get("/api/v1/level/config", headers=AUTH_HEADER).json()
        self.assertEqual(config["data"]["defaultLevelCode"], "NONE")
        self.assertEqual(config["data"]["defaultLevelName"], "")
        self.assertEqual(len(config["data"]["levels"]), 7)
        self.assertEqual(config["data"]["levels"][0]["level_name"], "初生熊猫")
        self.assertEqual(config["data"]["levels"][-1]["level_name"], "传奇熊猫")
        self.assertEqual(config["data"]["makeupCardMax"], 12)

    def test_points_history_pagination(self):
        for offset in range(5):
            self.rig.points.record_user_activity(
                TEST_USER, today=d(f"2026-09-0{offset + 1}")
            )
        response = self.client.get(
            "/api/v1/points/history?page=1&pageSize=2", headers=AUTH_HEADER
        ).json()
        self.assertEqual(len(response["data"]["items"]), 2)
        self.assertEqual(response["data"]["total"], 6)  # 每日首次 5 条 + 连续 3 天奖励 1 条
        self.assertTrue(response["data"]["hasMore"])
        # 按流水 id 倒序：最后一天先发每日首次、再发连续奖励，故最新一条为每日首次
        self.assertEqual(response["data"]["items"][0]["reason_code"], "DAILY_FIRST_CHAT")
        page2 = self.client.get(
            "/api/v1/points/history?page=2&pageSize=2", headers=AUTH_HEADER
        ).json()
        self.assertIn(
            "STREAK_3_DAY", [entry["reason_code"] for entry in page2["data"]["items"]]
        )

    # ---------- 补签卡（6.2） ----------
    def test_makeup_card_flow(self):
        for offset in range(5):
            self.rig.points.record_user_activity(
                TEST_USER, today=d(f"2026-09-0{offset + 1}")
            )
        summary = self.client.get("/api/v1/points/makeup-card", headers=AUTH_HEADER).json()
        self.assertGreaterEqual(summary["data"]["available"], 1)
        self.assertEqual(summary["data"]["maxAvailable"], 12)

        candidates = self.client.get(
            "/api/v1/points/makeup-card/candidates", headers=AUTH_HEADER
        ).json()
        dates = [item["date"] for item in candidates["data"]["items"]]
        self.assertNotIn("2026-09-01", dates)
        self.assertIn("2026-09-06", dates)

        # 未指定 targetDate
        bad = self.client.post("/api/v1/points/makeup-card/use", headers=AUTH_HEADER, json={})
        self.assertEqual(bad.status_code, 400)
        self.assertEqual(bad.json()["code"], 40001)

        # 已有有效对话的日期
        dup = self.client.post(
            "/api/v1/points/makeup-card/use",
            headers=AUTH_HEADER,
            json={"targetDate": "2026-09-01"},
        ).json()
        self.assertEqual(dup["code"], 40206)

        # 正常补签
        ok = self.client.post(
            "/api/v1/points/makeup-card/use",
            headers=AUTH_HEADER,
            json={"targetDate": "2026-09-06"},
        ).json()
        self.assertEqual(ok["code"], 0)
        self.assertTrue(ok["data"]["success"])
        self.assertIn("level", ok["data"])

        history = self.client.get(
            "/api/v1/points/makeup-card/history", headers=AUTH_HEADER
        ).json()
        self.assertEqual(history["data"]["summary"]["used"], 1)

    def test_makeup_card_without_card_returns_error_code(self):
        # 新用户当月自动获得 1 张（需求方确认规则），先用掉它再验证“补签卡不足”
        first = self.client.post(
            "/api/v1/points/makeup-card/use",
            headers=AUTH_HEADER,
            json={"targetDate": "2026-09-06"},
        ).json()
        self.assertEqual(first["code"], 0)

        response = self.client.post(
            "/api/v1/points/makeup-card/use",
            headers=AUTH_HEADER,
            json={"targetDate": "2026-09-05"},
        ).json()
        self.assertEqual(response["code"], 40205)
        self.assertFalse(response["data"]["success"])
        self.assertEqual(response["data"]["errorCode"], "MAKEUP_CARD_NOT_ENOUGH")

    # ---------- 后台配置 ----------
    def test_admin_config_read_and_update(self):
        config = self.client.get("/api/v1/admin/gamification-config", headers=AUTH_HEADER).json()
        self.assertEqual(config["code"], 0)
        items = config["data"]["interaction_items"]
        self.assertEqual(len(items), 5)

        updated = self.client.put(
            "/api/v1/admin/gamification-config",
            headers=AUTH_HEADER,
            json={
                "interaction_items": items
                + [
                    {
                        "id": "pudding",
                        "name": "布丁",
                        "icon": "🍮",
                        "cost_points": 8,
                        "prompt_template_id": "coffee",
                        "sort_order": 60,
                    }
                ]
            },
        ).json()
        self.assertEqual(updated["code"], 0)
        self.assertEqual(len(updated["data"]["interaction_items"]), 6)
        # 新物品按 sort_order 追加到平铺列表末尾（FR-5）
        menu = self.client.get("/api/v1/interaction/items", headers=AUTH_HEADER).json()
        self.assertEqual([item["id"] for item in menu["data"]["items"]][-1], "pudding")

    def test_admin_config_rejects_invalid_payload(self):
        response = self.client.put(
            "/api/v1/admin/gamification-config",
            headers=AUTH_HEADER,
            json={"interaction_items": [{"id": "", "name": "x", "cost_points": 1}]},
        )
        self.assertEqual(response.status_code, 400)
        self.assertEqual(response.json()["code"], 40001)

    def test_admin_config_rejects_unknown_section(self):
        response = self.client.put(
            "/api/v1/admin/gamification-config", headers=AUTH_HEADER, json={"nope": 1}
        )
        self.assertEqual(response.status_code, 400)

    def test_admin_user_progress_view(self):
        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-01"))
        body = self.client.get("/api/v1/admin/user-progress", headers=AUTH_HEADER).json()
        self.assertEqual(body["data"]["userId"], TEST_USER)
        self.assertIn("level", body["data"])
        self.assertIn("makeupCard", body["data"])

    # ---------- 客户端字段契约（防止 DTO 与响应字段名漂移）----------
    def test_field_name_contract_for_client_dtos(self):
        """客户端 DTO 依赖的字段名必须稳定；改名会静默变成默认值（曾导致等级列表崩溃）。"""
        level_config = self.client.get("/api/v1/level/config", headers=AUTH_HEADER).json()["data"]
        first_level = level_config["levels"][0]
        self.assertEqual(
            set(first_level.keys()), {"level_code", "level_name", "threshold_days", "sort_order"}
        )
        # 外层仍是 camelCase
        for key in ("levels", "defaultLevelCode", "defaultLevelName", "makeupCardMax", "breakGapDays", "warningGapDays"):
            self.assertIn(key, level_config)

        overview = self.client.get("/api/v1/points/overview", headers=AUTH_HEADER).json()["data"]
        for key in ("balance", "balanceUpdatedAt", "level", "makeupCard"):
            self.assertIn(key, overview)
        for key in ("levelCode", "levelName", "continuousDays", "nextLevelName", "daysToNextLevel"):
            self.assertIn(key, overview["level"])
        for key in ("available", "used", "totalGranted", "maxAvailable", "monthlyGrant", "atLimit"):
            self.assertIn(key, overview["makeupCard"])

        items = self.client.get("/api/v1/interaction/items", headers=AUTH_HEADER).json()["data"]
        self.assertEqual(
            set(items["items"][0].keys()),
            {"id", "name", "icon", "iconUrl", "costPoints", "sortOrder", "affordable"},
        )
        self.assertIn("dailyLimit", items)

        self.rig.points.record_user_activity(TEST_USER, today=d("2026-09-01"))
        ledger_item = self.client.get("/api/v1/points/history", headers=AUTH_HEADER).json()["data"]["items"][0]
        for key in ("id", "user_id", "change_amount", "reason_code", "balance_after", "created_at"):
            self.assertIn(key, ledger_item)

        cards = self.client.get("/api/v1/points/makeup-card/history", headers=AUTH_HEADER).json()["data"]
        self.assertIn("summary", cards)
        if cards["items"]:
            for key in ("id", "user_id", "granted_month", "status", "used_for_date", "created_at"):
                self.assertIn(key, cards["items"][0])

    # ---------- 测试用接口（积分调整 / 进度构造）----------
    def test_admin_points_adjust_increments_and_sets(self):
        granted = self.client.post(
            "/api/v1/admin/points/adjust",
            headers=AUTH_HEADER,
            json={"amount": 1000, "note": "联调"},
        ).json()
        self.assertEqual(granted["code"], 0)
        self.assertEqual(granted["data"]["balance"], 1000)
        self.assertEqual(granted["data"]["reasonCode"], "ADMIN_ADJUST")

        # 绝对值设置
        set_resp = self.client.post(
            "/api/v1/admin/points/adjust", headers=AUTH_HEADER, json={"balance": 30}
        ).json()
        self.assertEqual(set_resp["data"]["balance"], 30)

        # 负增量
        minus = self.client.post(
            "/api/v1/admin/points/adjust", headers=AUTH_HEADER, json={"amount": -10}
        ).json()
        self.assertEqual(minus["data"]["balance"], 20)

        # 账实一致：流水最后一条的 balance_after 等于当前余额
        history = self.client.get("/api/v1/points/history", headers=AUTH_HEADER).json()["data"]
        self.assertEqual(history["items"][0]["balance_after"], 20)
        self.assertEqual(history["items"][0]["reason_code"], "ADMIN_ADJUST")
        self.assertEqual(history["total"], 3)

    def test_admin_points_adjust_rejects_negative_balance_and_empty_body(self):
        over = self.client.post(
            "/api/v1/admin/points/adjust", headers=AUTH_HEADER, json={"amount": -5}
        )
        self.assertEqual(over.status_code, 400)
        self.assertEqual(over.json()["code"], 40001)

        empty = self.client.post("/api/v1/admin/points/adjust", headers=AUTH_HEADER, json={})
        self.assertEqual(empty.status_code, 400)

    def test_admin_progress_backfill_builds_streak_without_points(self):
        response = self.client.post(
            "/api/v1/admin/progress/backfill", headers=AUTH_HEADER, json={"days": 7}
        ).json()
        self.assertEqual(response["code"], 0)
        self.assertEqual(response["data"]["markedCount"], 7)
        self.assertEqual(response["data"]["level"]["continuousDays"], 7)
        self.assertEqual(response["data"]["level"]["levelCode"], "PANDA_LV2")
        # 默认不发放积分：账本应为空
        self.assertEqual(self.rig.ledger(), [])
        self.assertEqual(response["data"]["balance"], 0)

    def test_admin_progress_backfill_with_points(self):
        response = self.client.post(
            "/api/v1/admin/progress/backfill",
            headers=AUTH_HEADER,
            json={"days": 3, "awardPoints": True},
        ).json()
        self.assertEqual(response["data"]["markedCount"], 3)
        reasons = [entry["reason_code"] for entry in self.rig.ledger()]
        self.assertEqual(reasons.count("DAILY_FIRST_CHAT"), 3)
        self.assertEqual(reasons.count("STREAK_3_DAY"), 1)
        self.assertEqual(response["data"]["balance"], 6)

    def test_admin_progress_reset_keeps_balance(self):
        self.client.post(
            "/api/v1/admin/points/adjust", headers=AUTH_HEADER, json={"balance": 500}
        )
        self.client.post("/api/v1/admin/progress/backfill", headers=AUTH_HEADER, json={"days": 30})
        reset = self.client.post("/api/v1/admin/progress/reset", headers=AUTH_HEADER, json={}).json()
        self.assertEqual(reset["data"]["removedActivities"], 30)
        self.assertEqual(reset["data"]["level"]["levelCode"], "NONE")
        self.assertEqual(reset["data"]["level"]["continuousDays"], 0)
        self.assertEqual(reset["data"]["balance"], 500, "重置进度不得影响积分余额（FR-17）")

    def test_admin_progress_backfill_validates_days(self):
        bad = self.client.post(
            "/api/v1/admin/progress/backfill", headers=AUTH_HEADER, json={"days": 0}
        )
        self.assertEqual(bad.status_code, 400)

    def test_admin_interaction_prompt_preview(self):
        preview = self.client.get(
            "/api/v1/admin/interaction-prompt-preview?itemId=coffee", headers=AUTH_HEADER
        ).json()
        self.assertEqual(preview["code"], 0)
        data = preview["data"]
        self.assertEqual(data["itemId"], "coffee")
        self.assertGreaterEqual(data["messageCount"], 3)
        roles = [message["role"] for message in data["messages"]]
        self.assertEqual(roles[0], "system")
        self.assertIn("user", roles)
        self.assertIn("互动事件", data["messages"][-2]["content"])
        self.assertIn("咖啡", data["messages"][-1]["content"])

        missing = self.client.get(
            "/api/v1/admin/interaction-prompt-preview?itemId=nope", headers=AUTH_HEADER
        )
        self.assertEqual(missing.status_code, 404)


if __name__ == "__main__":
    unittest.main()
