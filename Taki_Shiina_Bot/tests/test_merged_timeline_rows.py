"""防抖合并批次的时间线契约：**每条被合并的消息都要有自己的一行**。

故障复盘
--------
客户端本地每条用户消息的主键就是它自己的 `requestId`；和解链路是
`GET /chat/history` → 按 `messageId` 对齐本地行 → 把 `error` 就地修成 `sent`。

服务端有两处会把防抖缓冲里的用户消息「摘走」（摘走后它们不会再有自己独立的回复）：

  A. `ws_api.process_buffered_messages`：正常聊天链路，整批合并成一条回复；
  B. `InteractionHandler.send`：用户刚发文字就点礼物，文字被合进礼物回复。

两处原先都只在 live 帧里回传 `requestIds` / `mergedRequestIds` 做批量置为已送达，
**时间线里没有这些消息的行**（A 只写了 `request_ids[-1]` 一行，B 一行都不写）。
于是只要 live 帧没到达（断线、超时、应用被杀），这些消息就永久停在
「发送失败」——重试、重启、全量同步都救不回来。这正是「后端收到了、界面说失败」
里最不可恢复的那一类。

本用例锁定契约（对 A 与 B 共用的构造函数）：
  1. 每条消息一行，且 `messageId == requestId`；
  2. 行内容是该条**自己的**文本（不能是合并后的整段文本）；
  3. 时间戳逐条递增且全部 < 回复时间戳；
  4. 缺 `requestId` 的条目被跳过而不是产生畸形行。
"""

from __future__ import annotations

import unittest

from gamification_fixture import setup_crypto_env  # noqa: F401  (夹具已处理 sys.path)
from services.debounce_merge import build_user_timeline_items
from ws_api_import_fixture import ensure_ws_api_env  # noqa: F401

ensure_ws_api_env()

USER = "u1"
NOW_MS = 1_700_000_000_000


def _item(request_id: str, content: str) -> dict:
    return {"requestId": request_id, "content": content}


class BuildUserTimelineItemsTestCase(unittest.TestCase):
    def test_every_merged_message_gets_its_own_row_keyed_by_request_id(self):
        items = [
            _item("req-1", "今天好累"),
            _item("req-2", "想喝咖啡"),
            _item("req-3", "你陪我聊聊"),
        ]
        rows = build_user_timeline_items(items, NOW_MS, user_id=USER)

        self.assertEqual(len(rows), 3, "被合并的每一条消息都必须有一行")
        self.assertEqual(
            [r["messageId"] for r in rows],
            ["req-1", "req-2", "req-3"],
            "messageId 必须等于客户端 requestId，否则客户端无法对齐本地行",
        )
        self.assertTrue(all(r["role"] == "user" for r in rows))
        self.assertTrue(all(r["userId"] == USER for r in rows))

    def test_row_content_is_the_individual_message_not_the_merged_text(self):
        items = [_item("req-1", "第一句"), _item("req-2", "第二句")]
        rows = build_user_timeline_items(items, NOW_MS, user_id=USER)

        self.assertEqual([r["content"] for r in rows], ["第一句", "第二句"])
        for row in rows:
            self.assertNotIn(
                "\n",
                row["content"],
                "不得写入合并后的整段文本：否则同步会把客户端最后一条气泡就地改写成重复内容",
            )

    def test_timestamps_are_strictly_increasing_and_before_reply(self):
        items = [_item(f"req-{i}", f"内容{i}") for i in range(4)]
        rows = build_user_timeline_items(items, NOW_MS, user_id=USER)

        stamps = [r["timestamp"] for r in rows]
        self.assertEqual(stamps, sorted(stamps), "时间戳必须递增以保持气泡顺序")
        self.assertEqual(len(set(stamps)), len(stamps), "同一批内不得出现并列时间戳")
        self.assertTrue(
            all(s < NOW_MS for s in stamps),
            "所有用户行都必须早于回复时间戳（既有约定：user = now-1，bot = now）",
        )

    def test_single_message_keeps_original_timestamp_semantics(self):
        rows = build_user_timeline_items([_item("req-only", "只有一条")], NOW_MS, user_id=USER)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["timestamp"], NOW_MS - 1, "单条消息的既有时间戳语义不得改变")

    def test_items_without_request_id_are_skipped(self):
        items = [_item("", "没有 id"), {"content": "也没有 id"}, _item("req-ok", "正常")]
        rows = build_user_timeline_items(items, NOW_MS, user_id=USER)
        self.assertEqual([r["messageId"] for r in rows], ["req-ok"])

    def test_empty_batch_produces_no_rows(self):
        self.assertEqual(build_user_timeline_items([], NOW_MS, user_id=USER), [])

    def test_missing_content_becomes_empty_string_not_none(self):
        rows = build_user_timeline_items([{"requestId": "req-img"}], NOW_MS, user_id=USER)
        self.assertEqual(rows[0]["content"], "", "纯图片消息没有文本，不得写成 None")


class BothWritePathsUseTheSharedBuilderTestCase(unittest.TestCase):
    """两个「摘走消息」的写入路径都必须调用本构造函数。

    这条断言是防回归的关键：缺陷之所以能同时存活在两条路径里，
    正是因为它们是两处独立实现，只有一处被修也无法覆盖另一处。
    """

    def test_ws_chat_path_writes_per_request_id_rows(self):
        import inspect

        import ws_api

        source = inspect.getsource(ws_api.process_buffered_messages)
        self.assertIn(
            "build_user_timeline_items(buffered",
            source,
            "聊天主链路必须把整批 buffered 消息逐条写入时间线",
        )

    def test_interaction_path_writes_per_request_id_rows(self):
        import inspect

        from handlers.interaction_handler import InteractionHandler

        source = inspect.getsource(InteractionHandler.send)
        self.assertIn(
            "build_user_timeline_items(merged_items",
            source,
            "礼物链路必须把摘走的缓冲消息逐条写入时间线",
        )


if __name__ == "__main__":
    unittest.main()
