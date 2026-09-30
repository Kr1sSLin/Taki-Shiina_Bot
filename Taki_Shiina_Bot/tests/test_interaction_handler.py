"""互动发送处理器单测（PRD 4.1 / 4.2 / 6.3 / FR-1 ~ FR-9 / EDGE-2）。"""

from __future__ import annotations

import asyncio
import unittest

from gamification_fixture import TEST_USER, d, make_rig  # noqa: F401
from services.progress_store import (
    REASON_DAILY_FIRST_CHAT,
    REASON_ITEM_REFUND,
    REASON_ITEM_SEND,
    PointsStore,
)

FAST_RETRY = {"max_retries": 2, "total_timeout_seconds": 1.5, "attempt_timeout_seconds": 0.2}


def run(coro):
    return asyncio.run(coro)


class InteractionHandlerTestCase(unittest.TestCase):
    def setUp(self):
        self._tmp, self.rig = make_rig(ai_outcomes=["啧，谁让你买的。\n……收下了。"], retry_rules=FAST_RETRY)
        self.addCleanup(self._tmp.cleanup)

    def set_balance(self, amount: int):
        with self.rig.gamification.points_store.transaction() as data:
            account = PointsStore.ensure_account(data, TEST_USER)
            account["balance"] = amount

    def ledger_reasons(self):
        return [entry["reason_code"] for entry in self.rig.ledger()]

    # ---------- 菜单（FR-1 / FR-4 / FR-5） ----------
    def test_list_items_sorted_with_cost_and_affordability(self):
        self.set_balance(9)
        payload = self.rig.handler.list_items(TEST_USER)
        self.assertEqual(payload["balance"], 9)
        self.assertEqual(
            [item["id"] for item in payload["items"]],
            ["coffee", "noodles", "gamepad", "white_dragon", "energy_bar"],
        )
        self.assertEqual(
            [item["costPoints"] for item in payload["items"]], [5, 7, 13, 16, 10]
        )
        affordable = {item["id"]: item["affordable"] for item in payload["items"]}
        self.assertTrue(affordable["coffee"])
        self.assertTrue(affordable["noodles"])
        self.assertFalse(affordable["gamepad"])
        self.assertFalse(affordable["white_dragon"])
        # 能量棒 10 分 > 余额 9：同样应判为积分不足（US-1 置灰）
        self.assertFalse(affordable["energy_bar"])
        # FR-4：互动菜单不设每日发送次数上限
        self.assertIsNone(payload["dailyLimit"])

    # ---------- 成功链路（6.3 / FR-3 / FR-7 / FR-8） ----------
    def test_send_success_deducts_and_pushes_reply(self):
        self.set_balance(20)
        result = run(self.rig.handler.send(TEST_USER, "coffee", request_id="req-1"))

        self.assertTrue(result.ok, result.message)
        self.assertEqual(result.charged, 5)
        self.assertEqual(result.attempts, 1)
        self.assertIn("谁让你买的", result.reply)
        self.assertEqual(result.balance, 16)  # 20 - 5 + 1（每日首次）
        self.assertIn(REASON_ITEM_SEND, self.ledger_reasons())
        self.assertIn(REASON_DAILY_FIRST_CHAT, self.ledger_reasons())

        sent = next(e for e in self.rig.ledger() if e["reason_code"] == REASON_ITEM_SEND)
        self.assertEqual(sent["change_amount"], -5)
        self.assertEqual(sent["related_item_id"], "coffee")
        self.assertEqual(sent["balance_after"], 15)

        events = self.rig.event_types()
        self.assertIn("points.changed", events)
        self.assertIn("chat.typing", events)
        self.assertIn("chat.reply.stream", events)
        reply_event = self.rig.events_of("chat.reply.stream")[-1]["payload"]
        self.assertEqual(reply_event["messageKind"], "interaction")
        self.assertEqual(reply_event["interactionItemId"], "coffee")
        self.assertTrue(reply_event["done"])

        # 时间线写入用户侧动作与 Taki 回复，走与普通聊天一致的历史通道（FR-8）
        self.assertEqual(len(self.rig.timeline), 2)
        self.assertEqual(self.rig.timeline[0]["role"], "user")
        self.assertEqual(self.rig.timeline[0]["itemId"], "coffee")
        self.assertEqual(self.rig.timeline[1]["role"], "bot")

    def test_send_retries_then_succeeds(self):
        self.set_balance(20)
        self.rig.client.completions.outcomes = [asyncio.TimeoutError(), "哈？行吧。"]
        result = run(self.rig.handler.send(TEST_USER, "noodles", request_id="req-retry"))
        self.assertTrue(result.ok, result.message)
        self.assertEqual(result.attempts, 2)
        self.assertIn("行吧", result.reply)

    # ---------- 余额不足（FR-3） ----------
    def test_send_with_insufficient_points_returns_error_code(self):
        self.set_balance(3)
        result = run(self.rig.handler.send(TEST_USER, "white_dragon", request_id="req-poor"))
        self.assertFalse(result.ok)
        self.assertEqual(result.error_code, 40201)
        self.assertEqual(result.balance, 3)
        self.assertEqual(self.ledger_reasons(), [])
        self.assertNotIn("chat.reply.stream", self.rig.event_types())

    def test_send_unknown_or_inactive_item(self):
        self.set_balance(100)
        result = run(self.rig.handler.send(TEST_USER, "not-exist", request_id="req-x"))
        self.assertFalse(result.ok)
        self.assertEqual(result.error_code, 40202)
        # 下架物品同样拒绝（FR-5：新增/下架走后台配置）
        self.rig.gamification.config.update_sections(
            {
                "interaction_items": [
                    {**item, "is_active": item["id"] != "coffee"}
                    for item in self.rig.gamification.config.get_config()["interaction_items"]
                ]
            }
        )
        offline = run(self.rig.handler.send(TEST_USER, "coffee", request_id="req-off"))
        self.assertFalse(offline.ok)
        self.assertEqual(offline.error_code, 40202)

    # ---------- 失败退款（FR-9 / EDGE-2） ----------
    def test_ai_failure_refunds_points_and_records_ledger(self):
        self.set_balance(20)
        self.rig.client.completions.outcomes = [
            asyncio.TimeoutError(),
            RuntimeError("boom"),
            asyncio.TimeoutError(),
        ]
        result = run(self.rig.handler.send(TEST_USER, "coffee", request_id="req-fail"))

        self.assertFalse(result.ok)
        self.assertEqual(result.error_code, 40204)
        self.assertTrue(result.refunded)
        self.assertEqual(result.balance, 21)  # 20 - 5扣分 + 1每日首次 + 5退回
        self.assertEqual(result.attempts, 3)

        reasons = self.ledger_reasons()
        self.assertIn(REASON_ITEM_SEND, reasons)
        self.assertIn(REASON_ITEM_REFUND, reasons)
        refund = next(e for e in self.rig.ledger() if e["reason_code"] == REASON_ITEM_REFUND)
        self.assertEqual(refund["change_amount"], 5, "退回记录记为正数")
        self.assertEqual(refund["related_item_id"], "coffee")

        fallback = self.rig.events_of("chat.reply.stream")[-1]["payload"]
        self.assertEqual(fallback["messageKind"], "interaction_failed")
        self.assertEqual(fallback["finalContent"], result.fallback_text)
        self.assertTrue(self.rig.alerts, "最终失败应触发飞书告警（PRD 九·可观测性）")

    def test_refund_is_idempotent(self):
        self.set_balance(20)
        refund = self.rig.points.refund_item(TEST_USER, "coffee", 5, "same-key")
        again = self.rig.points.refund_item(TEST_USER, "coffee", 5, "same-key")
        self.assertTrue(refund.created)
        self.assertFalse(again.created)
        refunds = [e for e in self.rig.ledger() if e["reason_code"] == REASON_ITEM_REFUND]
        self.assertEqual(len(refunds), 1)

    def test_timeline_failure_refunds_once_and_never_sends_done(self):
        self.set_balance(20)

        async def broken_timeline(_items):
            raise RuntimeError("disk unavailable")

        self.rig.handler.append_timeline = broken_timeline
        result = run(self.rig.handler.send(TEST_USER, "coffee", request_id="storage-fail"))
        self.assertFalse(result.ok)
        self.assertEqual(result.error_code, 40205)
        self.assertTrue(result.refunded)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 21)
        self.assertFalse(any(event.get("payload", {}).get("done") is True for _user, event in self.rig.events))
        self.assertEqual(self.rig.events_of("bot.error")[-1]["payload"]["errorCode"], "STORAGE_FAILED_REFUNDED")

        replay = run(self.rig.handler.send(TEST_USER, "coffee", request_id="storage-fail"))
        self.assertTrue(replay.duplicate)
        refunds = [e for e in self.rig.ledger() if e["reason_code"] == REASON_ITEM_REFUND]
        self.assertEqual(len(refunds), 1)

    # ---------- 幂等与并发（FR-13 / EDGE-1） ----------
    def test_duplicate_request_is_not_charged_twice(self):
        self.set_balance(20)
        first = run(self.rig.handler.send(TEST_USER, "coffee", request_id="dup-1"))
        second = run(self.rig.handler.send(TEST_USER, "coffee", request_id="dup-1"))
        self.assertTrue(first.ok)
        self.assertTrue(second.ok)
        self.assertTrue(second.duplicate)
        sends = [e for e in self.rig.ledger() if e["reason_code"] == REASON_ITEM_SEND]
        self.assertEqual(len(sends), 1)
        self.assertEqual(self.rig.points.get_balance(TEST_USER), 16)

    def test_sequential_concurrent_deductions_never_go_negative(self):
        """EDGE-1：余额 12 时连续两次 7 分物品，只允许扣成功一次。"""
        self.set_balance(12)

        async def scenario():
            return await asyncio.gather(
                self.rig.handler.send(TEST_USER, "noodles", request_id="c-1"),
                self.rig.handler.send(TEST_USER, "noodles", request_id="c-2"),
            )

        results = run(scenario())
        successes = [r for r in results if r.ok]
        self.assertEqual(len(successes), 1)
        self.assertGreaterEqual(self.rig.points.get_balance(TEST_USER), 0)

    # ---------- 计入每日有效对话（需求方确认） ----------
    def test_interaction_counts_as_daily_activity(self):
        self.set_balance(50)
        result = run(self.rig.handler.send(TEST_USER, "energy_bar", request_id="act-1"))
        self.assertTrue(result.ok)

        snapshot = self.rig.gamification.progress_store.snapshot()
        entries = [
            entry
            for entry in snapshot["daily_activity_log"].values()
            if entry["user_id"] == TEST_USER
        ]
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0]["source"], "INTERACTION")

        level = self.rig.gamification.get_level_status(TEST_USER)
        self.assertEqual(level["continuousDays"], 1)
        self.assertIn(REASON_DAILY_FIRST_CHAT, self.ledger_reasons())

    # ---------- 配置热更新（EDGE-9 / US-3） ----------
    def test_item_cost_change_takes_effect_for_new_requests(self):
        items = self.rig.gamification.config.get_config()["interaction_items"]
        updated = [
            {**item, "cost_points": 1} if item["id"] == "coffee" else item for item in items
        ]
        self.rig.gamification.config.update_sections({"interaction_items": updated})
        self.set_balance(10)
        result = run(self.rig.handler.send(TEST_USER, "coffee", request_id="hot-1"))
        self.assertTrue(result.ok)
        self.assertEqual(result.charged, 1)

    # ---------- 时段注入（服务端判定） ----------
    def test_prompt_carries_server_decided_period(self):
        preview = run(self.rig.handler.build_prompt_preview(TEST_USER, "coffee"))
        system_message = preview["messages"][0]["content"]
        self.assertIn("【当前北京时间】：", system_message)
        self.assertIn("【当前时段】：", system_message)
        self.assertIn("系统已判定", system_message)
        # 互动指令里引导模型读【当前时段】，而不是自己按小时猜
        self.assertIn("【当前时段】", preview["promptTemplate"])
        self.assertNotIn("23:00–05:00", preview["promptTemplate"], "不应再写死时间阈值")

    # ---------- 模板与图标 ----------
    def test_all_items_render_without_leftover_placeholders(self):
        for item in self.rig.gamification.config.get_active_items():
            with self.subTest(item=item["id"]):
                rendered = self.rig.gamification.config.render_prompt(item)
                self.assertNotIn("{", rendered)
                self.assertNotIn("}", rendered)
                self.assertIn(item["name"], rendered)

    def test_white_dragon_is_cigarette_with_smoking_icon(self):
        item = self.rig.gamification.config.get_item("white_dragon")
        self.assertEqual(item["icon"], "🚬", "白龙图标应改为香烟 emoji")
        self.assertEqual(item["name"], "白龙", "名称维持原样（PRD v0.8：无内容审查需求）")
        template = self.rig.gamification.config.render_prompt(item)
        self.assertIn("白龙是烟的代称", template)
        self.assertIn("嘴炮", template)
        self.assertIn("关心", template)
        self.assertIn("只写一种", template)

    def test_coffee_and_energy_bar_reference_mood(self):
        for item_id in ("coffee", "energy_bar"):
            with self.subTest(item=item_id):
                template = self.rig.gamification.config.render_prompt(
                    self.rig.gamification.config.get_item(item_id)
                )
                self.assertIn("情绪底色", template)
                self.assertIn("【当前时段】", template)
                self.assertIn("收下", template)

    # ---------- O5：文字 + 送礼 ----------
    def test_attachment_text_is_merged_into_interaction_turn(self):
        self.set_balance(50)
        result = run(
            self.rig.handler.send(
                TEST_USER, "coffee", request_id="o5-1", attachment="今天好累啊"
            )
        )
        self.assertTrue(result.ok)

        preview = run(
            self.rig.handler.build_prompt_preview(TEST_USER, "coffee", attachment="今天好累啊")
        )
        self.assertIn("今天好累啊", preview["messages"][-1]["content"])
        self.assertEqual(preview["messages"][-1]["role"], "user")
        self.assertEqual(preview["attachment"], "今天好累啊")

        # 历史与会话时间线都要带上附言，保证模型与客户端看到同一条
        history = self.rig.state.user_chat_history[TEST_USER]
        self.assertIn("今天好累啊", history[-2]["content"])
        self.assertIn("今天好累啊", self.rig.timeline[0]["content"])
        self.assertEqual(self.rig.timeline[0]["attachment"], "今天好累啊")

    def test_without_attachment_turn_is_plain_item(self):
        self.set_balance(50)
        run(self.rig.handler.send(TEST_USER, "coffee", request_id="o5-2"))
        preview = run(self.rig.handler.build_prompt_preview(TEST_USER, "coffee"))
        self.assertEqual(preview["messages"][-1]["content"], "（☕咖啡）")
        self.assertIsNone(self.rig.timeline[0]["attachment"])

    def test_pending_buffer_text_is_visible_to_gift_reply(self):
        """先发文字、防抖没到就送礼：礼物回复也要知道那句话（客户端零改动兜底）。"""
        buffer = {TEST_USER: ["今天好累啊", "陪我一会儿"]}
        self._tmp.cleanup()
        self._tmp, self.rig = make_rig(
            ai_outcomes=["行吧。"],
            retry_rules=FAST_RETRY,
            pending_text_provider=lambda uid: list(buffer.get(uid, [])),
        )
        self.set_balance(50)

        preview = run(self.rig.handler.build_prompt_preview(TEST_USER, "coffee"))
        anchor = preview["messages"][-2]["content"]
        self.assertIn("【他刚刚还说】", anchor)
        self.assertIn("今天好累啊", anchor)
        self.assertIn("陪我一会儿", anchor)

        # 有附言时优先用附言，不再追加缓冲内容（避免同一句话重复出现）
        with_attachment = run(
            self.rig.handler.build_prompt_preview(TEST_USER, "coffee", attachment="在忙")
        )
        self.assertIn("在忙", with_attachment["messages"][-1]["content"])
        self.assertNotIn("【他刚刚还说】", with_attachment["messages"][-2]["content"])

    # ---------- B 方案：文字 + 礼物合并成一条回复 ----------
    def _rig_with_merge(self, merged_items: list[dict], outcomes=None):
        self._tmp.cleanup()

        async def waiter(_user_id: str):
            return list(merged_items)

        self._tmp, self.rig = make_rig(
            ai_outcomes=outcomes or ["行吧，收下了。"],
            retry_rules=FAST_RETRY,
            pending_flush_waiter=waiter,
        )

    def test_buffered_messages_are_merged_into_single_interaction_reply(self):
        self._rig_with_merge(
            [
                {"requestId": "chat-1", "content": "今天好累啊"},
                {"requestId": "chat-2", "content": "陪我一会儿"},
            ]
        )
        self.set_balance(50)
        result = run(self.rig.handler.send(TEST_USER, "coffee", request_id="merge-1"))

        self.assertTrue(result.ok)
        self.assertEqual(result.merged_request_ids, ["chat-1", "chat-2"])

        # 客户端据此把「文字」气泡标记为已发送（否则会一直停在发送中）
        reply_event = self.rig.events_of("chat.reply.stream")[-1]["payload"]
        self.assertEqual(reply_event["requestIds"], ["merge-1", "chat-1", "chat-2"])
        self.assertEqual(reply_event["messageKind"], "interaction")
        self.assertEqual(result.to_http_data()["mergedCount"], 2)

        # 合并进来的文字进入模型历史（模型据此一起回答）
        history = self.rig.state.user_chat_history[TEST_USER]
        self.assertIn("今天好累啊", history[-2]["content"])
        self.assertIn("陪我一会儿", history[-2]["content"])

        # 时间线里的礼物气泡只标物品（文字在客户端本来就是独立气泡，避免重复展示）
        #
        # 注：断言从「timeline[0]」改成按确定性 messageId 查找。
        # 摘走的文字消息现在**也会各自写一行**（messageId == 客户端 requestId），
        # 它们在时序上早于礼物，因此排在礼物行之前 —— 用下标取值会随行数变化而脆断，
        # 而这里真正要锁的是「礼物行内容只标物品」这条语义。
        gift_row = next(x for x in self.rig.timeline if x["messageId"] == "interaction_user_merge-1")
        self.assertEqual(gift_row["content"], "☕ 咖啡")
        self.assertIsNone(gift_row.get("attachment"))

        # 被摘走的文字消息必须逐条写进时间线：客户端本地那几条气泡在 live 帧丢失后
        # 只能靠 GET /chat/history 和解回 sent，缺行就是永久「发送失败」。
        merged_rows = [x for x in self.rig.timeline if x["messageId"] in ("chat-1", "chat-2")]
        self.assertEqual(
            [(x["messageId"], x["content"]) for x in merged_rows],
            [("chat-1", "今天好累啊"), ("chat-2", "陪我一会儿")],
            "被合并的消息必须各写一行，且内容是该条自己的文本",
        )
        # 时序：被摘消息 < 礼物行 < Bot 回复，且不得出现时间戳并列
        stamps = [x["timestamp"] for x in self.rig.timeline]
        self.assertEqual(len(stamps), len(set(stamps)), f"时间线时间戳不得并列: {stamps}")
        self.assertLess(
            next(x["timestamp"] for x in self.rig.timeline if x["messageId"] == "chat-2"),
            gift_row["timestamp"],
            "被摘走的文字在时序上必须早于礼物行",
        )
        self.assertLess(gift_row["timestamp"], max(stamps), "礼物行必须早于 Bot 回复行")

    def test_no_merge_when_client_sends_attachment(self):
        """客户端已把输入框内容作为附言送出时，不再去摘缓冲（两者不叠加）。"""
        self._rig_with_merge([{"requestId": "chat-1", "content": "不该被用到的文字"}])
        self.set_balance(50)
        result = run(
            self.rig.handler.send(TEST_USER, "coffee", request_id="merge-2", attachment="在忙")
        )
        self.assertTrue(result.ok)
        self.assertEqual(result.merged_request_ids, [])
        reply_event = self.rig.events_of("chat.reply.stream")[-1]["payload"]
        self.assertEqual(reply_event["requestIds"], ["merge-2"])
        history = self.rig.state.user_chat_history[TEST_USER]
        self.assertIn("在忙", history[-2]["content"])
        self.assertNotIn("不该被用到的文字", history[-2]["content"])

    def test_merge_failure_falls_back_to_immediate_reply(self):
        self._tmp.cleanup()

        async def broken_waiter(_user_id: str):
            raise RuntimeError("boom")

        self._tmp, self.rig = make_rig(
            ai_outcomes=["行吧。"], retry_rules=FAST_RETRY, pending_flush_waiter=broken_waiter
        )
        self.set_balance(50)
        result = run(self.rig.handler.send(TEST_USER, "coffee", request_id="merge-3"))
        self.assertTrue(result.ok, "等待失败不应影响互动本身")
        self.assertEqual(result.merged_request_ids, [])


if __name__ == "__main__":
    unittest.main()
