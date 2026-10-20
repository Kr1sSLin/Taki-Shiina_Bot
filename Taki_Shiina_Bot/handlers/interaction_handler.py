"""
互动礼物 · 发送处理器（PRD §3.2 handlers/interaction_handler.py）

职责（对应 PRD FR-1 ~ FR-9、6.3 状态机）：

1. 校验物品与积分余额（FR-3），原子扣减并记 ``ITEM_SEND`` 流水
2. 扣分成功后按物品匹配 Prompt 模板（FR-6），在**当前用户 session 上下文**中
   拼接历史后调用 DeepSeek（FR-7）
3. 回复通过既有 WebSocket 通道下发（FR-8），展示方式与普通聊天消息完全一致
4. DeepSeek 调用失败自动重试，超过重试次数或配置总预算（默认 60 秒）后
   自动退回积分并记 ``ITEM_REFUND`` 流水，客户端收到兜底文案（FR-9 / EDGE-2）
5. 幂等（FR-13）：``requestId`` 作为幂等键，重复请求不会重复扣分
6. EDGE-9：Prompt 模板在**请求发起时**快照，后台更新不影响进行中的请求
"""

from __future__ import annotations
from services.public_policy import public_messages, checked_completion

import asyncio
import logging
import os
import re
import time
import uuid
from dataclasses import dataclass, field
from datetime import date
from typing import Any, Callable

from text_utils import (
    clean_short_term_history,
    inject_emojis,
    sanitize_taki_reply,
    strip_polluted_tail,
    with_history_timestamp,
)
from services.debounce_merge import (
    buffered_request_ids,
    build_user_timeline_items,
    merge_buffered_texts,
)
from time_utils import build_time_block, get_business_today_str
from services.points_events import level_changed_event, makeup_card_changed_event, points_changed_event
from services.points_service import InsufficientPointsError
from services.progress_store import SOURCE_INTERACTION
from services.application_service import ChatApplicationService
from services.contracts import ChatCommand
from services.chat_runtime import ChatFailed
from services.foundation_store import PermissionDenied
from services.privacy import redact_locations

# 错误码（PRD FR-3 要求“返回明确错误码”）
CODE_OK = 0
CODE_INVALID_PARAM = 40200
CODE_INSUFFICIENT_POINTS = 40201
CODE_ITEM_NOT_FOUND = 40202
CODE_AI_FAILED_REFUNDED = 40204
CODE_STORAGE_FAILED_REFUNDED = 40205
# 最终失败兜底文案（PRD FR-9：已扣减积分自动退回 + 失败兜底文案）
DEFAULT_FALLBACK_TEXT = "这次没能完成，请稍后再试。"

TIMER_PATTERN = re.compile(r"\[\[TIMER:(\d{1,2}:\d{2})\|(.*?)\]\]")

TIME_ENFORCEMENT = "\n".join(
    (
        "1. **时间锚定**：你必须无条件信任上述【当前北京时间】，绝不能凭训练数据或常识猜测当前时间。",
        "2. **全天候禁止催睡**：无论现在是凌晨几点，绝对禁止主动建议用户去睡觉或休息。",
        "3. **深夜默契**：如果用户深夜还在发消息，表示他想通宵，你要配合而不是扫兴。",
    )
)


def parse_timer_instruction(raw_reply: str):
    match = TIMER_PATTERN.search(raw_reply or "")
    if not match:
        return raw_reply, None, None
    cleaned = TIMER_PATTERN.sub("", raw_reply).strip()
    return cleaned, match.group(1), match.group(2)


@dataclass
class InteractionSendResult:
    ok: bool
    user_id: str
    item_id: str
    request_id: str
    error_code: int = CODE_OK
    message: str = "ok"
    item: dict[str, Any] | None = None
    duplicate: bool = False
    charged: int = 0
    refunded: bool = False
    balance: int = 0
    reply: str | None = None
    message_id: str | None = None
    attempts: int = 0
    fallback_text: str | None = None
    timer_instruction: dict[str, Any] | None = None
    level_event: dict[str, Any] | None = None
    rewards: list[dict[str, Any]] = field(default_factory=list)
    # B 方案：本次回复合并掉的聊天消息 requestId（客户端据此把那些气泡标记为已发送）
    merged_request_ids: list[str] = field(default_factory=list)

    def to_http_data(self) -> dict[str, Any]:
        data: dict[str, Any] = {
            "success": self.ok,
            "itemId": self.item_id,
            "requestId": self.request_id,
            "balance": self.balance,
            "charged": self.charged,
            "refunded": self.refunded,
            "duplicate": self.duplicate,
        }
        if self.merged_request_ids:
            data["mergedRequestIds"] = list(self.merged_request_ids)
            data["mergedCount"] = len(self.merged_request_ids)
        if self.item:
            data["item"] = {
                "id": self.item.get("id"),
                "name": self.item.get("name"),
                "icon": self.item.get("icon"),
                "costPoints": self.item.get("cost_points"),
            }
        if self.reply is not None:
            data["reply"] = self.reply
            data["messageId"] = self.message_id
        if self.fallback_text is not None:
            data["fallbackText"] = self.fallback_text
        if self.timer_instruction:
            data["timerInstruction"] = self.timer_instruction
        if not self.ok:
            data["errorCode"] = self._error_code_text()
            data["messageText"] = self.message
        return data

    def _error_code_text(self) -> str:
        return {
            CODE_INSUFFICIENT_POINTS: "INSUFFICIENT_POINTS",
            CODE_ITEM_NOT_FOUND: "ITEM_NOT_FOUND",
            CODE_AI_FAILED_REFUNDED: "AI_FAILED_REFUNDED",
            CODE_STORAGE_FAILED_REFUNDED: "STORAGE_FAILED_REFUNDED",
            CODE_INVALID_PARAM: "INVALID_PARAM",
        }.get(self.error_code, "UNKNOWN")


class InteractionAIError(RuntimeError):
    def __init__(self, message: str, attempts: int, elapsed_ms: int):
        super().__init__(message)
        self.attempts = attempts
        self.elapsed_ms = elapsed_ms


class InteractionHandler:
    def __init__(
        self,
        *,
        config_service,
        points_service,
        makeup_card_service,
        level_service,
        client,
        prompt_service,
        weather_service,
        state,
        history_store,
        append_timeline: Callable[[list[dict]], Any],
        broadcast_json: Callable[[str, dict], Any],
        pending_text_provider: Callable[[str], list[str]] | None = None,
        pending_flush_waiter: Callable[[str], Any] | None = None,
        logger: logging.Logger | None = None,
        alert_sender: Callable[[str], Any] | None = None,
        runtime_provider: Callable[[], Any] | None = None,
    ):
        self.config_service = config_service
        self.points_service = points_service
        self.makeup_card_service = makeup_card_service
        self.level_service = level_service
        self.client = client
        self.prompt_service = prompt_service
        self.weather_service = weather_service
        self.state = state
        self.history_store = history_store
        self.append_timeline = append_timeline
        self.broadcast_json = broadcast_json
        self.logger = logger or logging.getLogger(__name__)
        self.alert_sender = alert_sender
        self.runtime_provider = runtime_provider
        # O5：读取「防抖缓冲里尚未处理」的用户文字，供礼物回复参考（客户端零改动也能兜住）
        self.pending_text_provider: Callable[[str], list[str]] | None = pending_text_provider
        # B 方案：等静默窗口结束并摘走缓冲消息，使「文字 + 礼物」只产生一条回复
        self.pending_flush_waiter = pending_flush_waiter
        # 幂等重放的短期缓存：requestId → 结果（进程内，配合账本幂等键双重防重）
        self._recent_results: dict[tuple[str, str], InteractionSendResult] = {}
        self._recent_order: list[tuple[str, str]] = []
        self._recent_limit = 200
        self._ai_failure_count = 0

    # ==================== 菜单（FR-1 / FR-5） ====================
    def list_items(self, user_id: str) -> dict[str, Any]:
        items = self.config_service.get_active_items()
        balance = self.points_service.get_balance(user_id)
        return {
            "balance": balance,
            "items": [
                {
                    "id": item["id"],
                    "name": item["name"],
                    "icon": item.get("icon", ""),
                    "iconUrl": item.get("icon_url", ""),
                    "costPoints": int(item["cost_points"]),
                    "sortOrder": int(item["sort_order"]),
                    "affordable": balance >= int(item["cost_points"]),
                }
                for item in items
            ],
            "dailyLimit": self.config_service.get_points_rules().get("interaction_daily_limit"),
        }

    # ==================== 发送（6.3 状态机） ====================
    async def send(
        self,
        user_id: str,
        item_id: str,
        request_id: str | None = None,
        device_id: str | None = None,
        attachment: str | None = None,
    ) -> InteractionSendResult:
        async with self.state.sessions.session(user_id):
            return await self._send(user_id, item_id, request_id, device_id, attachment)

    async def _send(self, user_id, item_id, request_id, device_id, attachment):
        """`attachment`（O5）：送礼时用户输入框里的附言，会随礼物一起交给模型。"""
        request_id = (request_id or "").strip() or uuid.uuid4().hex
        attachment = redact_locations((attachment or "").strip())
        try:
            runtime = self.runtime_provider() if self.runtime_provider is not None else None
        except PermissionDenied:
            return InteractionSendResult(ok=False, user_id=user_id, item_id=item_id, request_id=request_id,
                                         error_code=50301, message="服务暂时不可用，请稍后重试")
        if runtime is not None:
            return await self._send_reliable(runtime, user_id, item_id, request_id, device_id, attachment)

        cached = self._recent_results.get((user_id, request_id))
        if cached is not None:
            replay = InteractionSendResult(**{**cached.__dict__, "duplicate": True})
            return replay

        item = self.config_service.get_item(item_id)
        if not item or not item.get("is_active"):
            return InteractionSendResult(
                ok=False,
                user_id=user_id,
                item_id=item_id,
                request_id=request_id,
                error_code=CODE_ITEM_NOT_FOUND,
                message="物品不存在或已下架",
                balance=self.points_service.get_balance(user_id),
            )

        cost = int(item["cost_points"])
        business_date = get_business_today_str()

        # ---- 1) 原子扣减 + ITEM_SEND 流水 ----
        try:
            deduction = self.points_service.deduct_for_item(
                user_id=user_id,
                item_id=item["id"],
                cost_points=cost,
                idempotency_key=request_id,
                business_date=business_date,
            )
        except InsufficientPointsError:
            balance = self.points_service.get_balance(user_id)
            self.logger.info(f"[互动] 积分不足: user={user_id}, item={item['id']}, balance={balance}")
            return InteractionSendResult(
                ok=False,
                user_id=user_id,
                item_id=item["id"],
                request_id=request_id,
                error_code=CODE_INSUFFICIENT_POINTS,
                message=f"积分不足，需要 {cost} 积分",
                item=item,
                balance=balance,
            )

        balance = self.points_service.get_balance(user_id)
        if not deduction.created:
            # 账本里已存在同一幂等键：视为重放，不重复扣分也不重复生成回复
            self.logger.info(f"[互动] 幂等重放: user={user_id}, key={request_id}")
            result = InteractionSendResult(
                ok=True,
                user_id=user_id,
                item_id=item["id"],
                request_id=request_id,
                item=item,
                duplicate=True,
                balance=balance,
                message="重复请求，已忽略",
            )
            self._remember(request_id, result)
            return result

        await self._push(user_id, points_changed_event(deduction.entry, balance=balance))

        result = InteractionSendResult(
            ok=True,
            user_id=user_id,
            item_id=item["id"],
            request_id=request_id,
            item=item,
            charged=cost,
            balance=balance,
        )

        activity_date = get_business_today_str()

        # 新用户首次互动时补发当月补签卡（需求方确认规则）
        try:
            granted = self.makeup_card_service.ensure_initial_grant(user_id)
            if granted:
                summary = self.makeup_card_service.get_summary(user_id)
                await self._push(user_id, makeup_card_changed_event(summary, "MONTHLY_GRANT"))
        except Exception as exc:
            self.logger.warning(f"[互动] 补签卡首发检查失败: {exc}")

        # ---- 3a) B 方案：用户刚发文字就点礼物 → 等静默窗口结束并摘走那批消息，只回一条 ----
        merged_items: list[dict] = []
        merged_request_ids: list[str] = []
        if not attachment and self.pending_flush_waiter is not None:
            # 先让客户端进入「思考中」，因为接下来可能等上几秒到二十几秒
            await self._push(
                user_id,
                {"type": "chat.typing", "payload": {"typing": True, "stage": "interaction_merge"}},
            )
            try:
                merged_items = await self.pending_flush_waiter(user_id) or []
            except Exception as exc:
                self.logger.warning(f"[互动] 等待防抖窗口失败，按原流程立即回复: {exc}")
                merged_items = []
            if merged_items:
                merged_request_ids = buffered_request_ids(merged_items)
                self.logger.info(
                    f"[互动] 已合并 {len(merged_items)} 条待回复消息到礼物回复: user={user_id}, "
                    f"item={item['id']}, requestIds={merged_request_ids}"
                )

        # 摘到的文字当作附言使用（进 user 回合与模型历史）；客户端发来的 text 优先级更高
        merged_text = merge_buffered_texts(merged_items) if merged_items else ""
        prompt_attachment = attachment or merged_text

        # ---- 3b) 组装 Prompt（EDGE-9：此处快照模板） ----
        await self._push(user_id, {"type": "chat.typing", "payload": {"typing": True, "stage": "interaction"}})

        try:
            messages, polluted_tails = await self._build_messages(
                user_id, item, attachment=prompt_attachment
            )
        except Exception as exc:
            self.logger.exception(f"[互动] 组装上下文失败: {exc}")
            messages, polluted_tails = [], []

        # ---- 4) 调用 DeepSeek（失败重试 + 总耗时兜底） ----
        started_ms = int(time.time() * 1000)
        try:
            raw_reply, attempts = await self._call_deepseek(messages)
        except InteractionAIError as exc:
            elapsed_ms = int(time.time() * 1000) - started_ms
            self._ai_failure_count += 1
            self.logger.error(
                f"[互动] DeepSeek 最终失败: user={user_id}, item={item['id']}, "
                f"attempts={exc.attempts}, elapsedMs={elapsed_ms}, 累计失败={self._ai_failure_count}"
            )
            refunded = await self._handle_ai_failure(
                user_id, item, request_id, cost, str(exc), exc.attempts, elapsed_ms
            )
            result.ok = False
            result.error_code = CODE_AI_FAILED_REFUNDED
            result.message = "Taki 暂时没回应，积分已退回" if refunded else "互动失败，请联系管理员核对积分"
            result.refunded = refunded
            result.attempts = exc.attempts
            result.fallback_text = DEFAULT_FALLBACK_TEXT if refunded else None
            result.balance = self.points_service.get_balance(user_id)
            self._remember(request_id, result)
            await self._push(user_id, {"type": "chat.typing", "payload": {"typing": False}})
            return result

        result.attempts = attempts

        # ---- 5) 清洗 + 落库 + WS 下发（FR-8：与普通聊天消息同一通道） ----
        cleaned_reply, timer_at, timer_text = parse_timer_instruction(raw_reply)
        cleaned_reply = strip_polluted_tail(cleaned_reply, polluted_tails)
        final_reply = inject_emojis(sanitize_taki_reply(cleaned_reply)) or DEFAULT_FALLBACK_TEXT
        message_id = str(uuid.uuid4())
        now_ms = int(time.time() * 1000)

        # 模型历史里带上「附言」（客户端 text 或摘来的缓冲文字都算）
        # 时间线只并入**客户端传来的附言**：摘来的缓冲文字在客户端本来就已是一条独立气泡，
        # 再并进来会重复显示，因此这里只标出物品本身。
        timeline_user_content = f"{item.get('icon', '')} {item['name']}".strip()
        if attachment:
            timeline_user_content = f"{timeline_user_content} · {attachment}"
        try:
            self._append_history(user_id, item, final_reply, now_ms, attachment=prompt_attachment)
            await self.append_timeline(
                # 被摘走（合进本次礼物回复）的聊天消息也必须逐条写时间线。
                build_user_timeline_items(merged_items, now_ms - 1, user_id=user_id)
                + [
                    {
                        "messageId": f"interaction_user_{request_id}",
                        "userId": user_id,
                        "role": "user",
                        "content": timeline_user_content,
                        "timestamp": now_ms - 1,
                        "contentType": "interaction",
                        "itemId": item["id"],
                        "itemName": item["name"],
                        "itemIcon": item.get("icon", ""),
                        "costPoints": cost,
                        "attachment": attachment or None,
                    },
                    {
                        "messageId": message_id,
                        "userId": user_id,
                        "role": "bot",
                        "content": final_reply,
                        "timestamp": now_ms,
                        "contentType": "interaction",
                        "itemId": item["id"],
                        "itemName": item["name"],
                        "itemIcon": item.get("icon", ""),
                    },
                ]
            )
        except Exception:
            self.logger.exception("[互动] 时间线写入失败，执行幂等退款")
            refunded = await self._refund_charge(user_id, item, request_id, cost)
            result.ok = False
            result.error_code = CODE_STORAGE_FAILED_REFUNDED
            result.message = "保存失败，积分已退回" if refunded else "保存失败，请联系管理员核对积分"
            result.refunded = refunded
            result.balance = self.points_service.get_balance(user_id)
            self._remember(request_id, result)
            await self._push(user_id, {"type": "chat.typing", "payload": {"typing": False}})
            await self._push(
                user_id,
                {
                    "type": "bot.error",
                    "requestId": request_id,
                    "payload": {
                        "errorCode": "STORAGE_FAILED_REFUNDED",
                        "message": "消息保存失败，请稍后重试",
                        "requestIds": [request_id, *merged_request_ids],
                    },
                },
            )
            return result

        try:
            await self._record_success_activity(user_id, activity_date, result)
        except Exception as error:
            self.logger.warning("interaction activity failed (%s)", type(error).__name__)

        if timer_at and timer_text:
            result.timer_instruction = {"target": timer_at, "text": timer_text}

        await self._push(
            user_id,
            {
                "type": "chat.reply.stream",
                "requestId": request_id,
                "payload": {
                    "delta": "",
                    "done": True,
                    "messageId": message_id,
                    "finalContent": final_reply,
                    "timestamp": now_ms,
                    # B 方案：合进本次回复的那批聊天消息也要一起标记为已送达，
                    # 否则客户端对应气泡会永远停在「发送中」
                    "requestIds": [request_id, *merged_request_ids],
                    "messageKind": "interaction",
                    "interactionItemId": item["id"],
                    "interactionItemName": item["name"],
                    "interactionItemIcon": item.get("icon", ""),
                    "modelProvider": "deepseek",
                    "timerInstruction": result.timer_instruction,
                    "originDeviceId": device_id,
                },
            },
        )
        await self._push(user_id, {"type": "chat.typing", "payload": {"typing": False}})

        result.reply = final_reply
        result.message_id = message_id
        result.merged_request_ids = merged_request_ids
        self._remember(request_id, result)
        self.logger.info(
            f"[互动] 发送成功: user={user_id}, item={item['id']}, attempts={attempts}, "
            f"cost={cost}, balance={result.balance}"
        )
        return result

    async def _send_reliable(self, runtime, user_id, item_id, request_id, device_id, attachment):
        """Keep the points ledger's rules; SQL owns the single durable chat result."""
        existing = await runtime.request(user_id, request_id)
        charge = self.points_service.find_ledger_entry(user_id, f"item_send:{user_id}:{request_id}")
        refund = self.points_service.find_ledger_entry(user_id, f"item_refund:{user_id}:{request_id}")
        if existing:
            source = existing["source"]
            metadata = source.get("metadata") or {}
            item = metadata.get("item") or {}
            if source.get("intent") != "interaction" or item.get("id") != item_id or metadata.get("attachment", "") != attachment:
                return InteractionSendResult(False, user_id, item_id, request_id, error_code=CODE_INVALID_PARAM,
                                             message="请求标识已用于其他输入", balance=self.points_service.get_balance(user_id))
        else:
            item = self.config_service.get_item(item_id)
            if not item or (not item.get("is_active") and charge is None):
                return InteractionSendResult(False, user_id, item_id, request_id, error_code=CODE_ITEM_NOT_FOUND,
                                             message="物品不存在或已下架", balance=self.points_service.get_balance(user_id))
            metadata = {"item": {key: item.get(key) for key in ("id", "name", "icon", "cost_points")},
                        "attachment": attachment, "prompt_template": self.config_service.render_prompt(item),
                        "business_date": get_business_today_str(),
                        "retry_rules": self.config_service.get_points_rules()["deepseek_retry"]}
        if charge and charge.get("related_item_id") != item_id:
            return InteractionSendResult(False, user_id, item_id, request_id, error_code=CODE_INVALID_PARAM,
                                         message="请求标识已用于其他物品", balance=self.points_service.get_balance(user_id))
        cost = abs(int(charge["change_amount"])) if charge else int(item["cost_points"])
        if refund:
            return InteractionSendResult(False, user_id, item_id, request_id, error_code=CODE_AI_FAILED_REFUNDED,
                                         message="此前互动失败，积分已退回", item=item, duplicate=True, refunded=True,
                                         balance=self.points_service.get_balance(user_id), fallback_text=DEFAULT_FALLBACK_TEXT)
        result = InteractionSendResult(True, user_id, item_id, request_id, item=item,
                                       duplicate=charge is not None,
                                       balance=self.points_service.get_balance(user_id))
        turn_id = existing["turn_id"] if existing else None
        try:
            if turn_id is None:
                text = f"{item.get('icon', '')} {item['name']}".strip()
                if attachment:
                    text += f" · {attachment}"
                command = ChatCommand(user_id, request_id, f"interaction_user_{request_id}", device_id or "interaction",
                                      text, channel="ws", intent="interaction", metadata=metadata)
                receipts = await runtime.accept([command], delay=20 if not attachment else 0, merge_pending=not attachment)
                turn_id = receipts[0]["turn_id"]
            try:
                deduction = await runtime.charge_interaction(
                    user_id, request_id,
                    lambda: self.points_service.deduct_for_item(user_id, item_id, cost, request_id,
                                                               metadata.get("business_date") or get_business_today_str()),
                )
            except InsufficientPointsError:
                await runtime.cancel(user_id, turn_id)
                return InteractionSendResult(False, user_id, item_id, request_id, error_code=CODE_INSUFFICIENT_POINTS,
                                             message=f"积分不足，需要 {cost} 积分", item=item, balance=self.points_service.get_balance(user_id))
            result.duplicate = not deduction.created
            result.charged = cost if deduction.created else 0
            result.balance = self.points_service.get_balance(user_id)
            if deduction.created:
                await self._push(user_id, points_changed_event(deduction.entry, balance=result.balance))
                try:
                    if self.makeup_card_service.ensure_initial_grant(user_id):
                        await self._push(user_id, makeup_card_changed_event(self.makeup_card_service.get_summary(user_id), "MONTHLY_GRANT"))
                except Exception as error:
                    self.logger.warning("interaction makeup grant failed (%s)", type(error).__name__)
            # No model can claim this gift until its durable payment gate opens.
            await runtime.confirm_interaction(user_id, request_id)
            if not attachment and self.pending_flush_waiter is not None and existing is None:
                try:
                    # These sources were accepted by WS already. Never insert them a second time.
                    await self.pending_flush_waiter(user_id)
                except Exception as error:
                    self.logger.warning("interaction merge wait failed (%s)", type(error).__name__)
            await runtime.ready(user_id, [turn_id])
            # The persisted turn deadline owns the generation budget. Queueing
            # and extraction must not make this waiter cancel it prematurely.
            durable = await runtime.wait(user_id, turn_id, timeout=125)
        except asyncio.CancelledError:
            # Disconnecting a waiter does not revoke a paid, accepted operation.
            raise
        except Exception as error:
            self.logger.warning("interaction operation failed (%s)", type(error).__name__)
            if turn_id is None:
                # An accept error can occur after commit. Resolve uncertainty before refunding.
                recovered = await runtime.request(user_id, request_id)
                turn_id = recovered["turn_id"] if recovered else None
            durable = await runtime.cancel(user_id, turn_id) if turn_id else None
            if durable is None:
                charge = self.points_service.find_ledger_entry(user_id, f"item_send:{user_id}:{request_id}")
                if charge is not None:
                    result.refunded = await self._refund_charge(user_id, item, request_id, abs(int(charge["change_amount"])))
                result.ok = False
                storage_failure = not isinstance(error, (ChatFailed, TimeoutError)) or getattr(error, "category", "") == "storage"
                result.error_code = CODE_STORAGE_FAILED_REFUNDED if storage_failure else CODE_AI_FAILED_REFUNDED
                result.message = "互动失败，积分已退回" if result.refunded else ("消息保存失败，未扣积分" if charge is None else "互动失败，请联系管理员核对积分")
                result.balance = self.points_service.get_balance(user_id)
                result.fallback_text = DEFAULT_FALLBACK_TEXT if result.refunded else None
                await self._push(user_id, {"type": "chat.typing", "payload": {"typing": False}})
                await self._push(user_id, {"type": "bot.error", "requestId": request_id,
                                            "payload": {"errorCode": result._error_code_text(), "message": result.message,
                                                        "requestIds": [request_id]}})
                return result
        # The SQL result and required outbox are already committed. No fallible
        # mirror writes or direct reply pushes may turn this into a refund.
        result.reply = durable["reply"]
        result.message_id = durable["messageId"]
        result.timer_instruction = durable.get("timerInstruction")
        result.attempts = durable.get("attempts", 1)
        result.merged_request_ids = [identity for identity in durable.get("requestIds", []) if identity != request_id]
        try:
            await self._record_success_activity(user_id, metadata.get("business_date"), result)
        except Exception as error:
            # The durable reconciliation job retries rewards after a crash or
            # ledger failure. A committed reply must never be refunded here.
            self.logger.warning("interaction activity deferred (%s)", type(error).__name__)
        return result

    async def _record_success_activity(self, user_id, business_date=None, result=None):
        activity = self.points_service.record_user_activity(
            user_id, source=SOURCE_INTERACTION,
            today=date.fromisoformat(business_date) if business_date else None,
        )
        for entry in activity.rewards:
            await self._push(user_id, points_changed_event(entry))
        event = None
        if activity.level is not None and activity.level.level_changed:
            event = level_changed_event(activity.level)
            await self._push(user_id, event)
        if result is not None:
            result.rewards = [dict(entry) for entry in activity.rewards]
            result.balance = activity.balance
            result.level_event = event

    async def reconcile_reliable(self, source):
        """Compensate an unconfirmed durable intent after a crash, exactly once.

        The repository has already moved its payment gate to ``reconciling``;
        neither a late confirmation nor a model result can succeed afterwards.
        """
        if source.get("intent") != "interaction":
            raise ValueError("reconciliation requires an interaction source")
        if source.get("completed"):
            await self._record_success_activity(source["owner"], source["metadata"].get("business_date"))
            return
        runtime = self.runtime_provider() if self.runtime_provider else None
        if runtime is None:
            raise RuntimeError("reconciliation runtime is unavailable")
        owner, request_id = source["owner"], source["request_id"]
        item = source["metadata"]["item"]
        charge = self.points_service.find_ledger_entry(owner, f"item_send:{owner}:{request_id}")
        if charge is not None:
            amount = int(charge.get("change_amount", 0))
            if charge.get("related_item_id") != item["id"] or amount >= 0:
                raise ValueError("reconciliation charge identity is inconsistent")
            refund = self.points_service.find_ledger_entry(owner, f"item_refund:{owner}:{request_id}")
            if refund is None:
                if not await self._refund_charge(owner, item, request_id, -amount):
                    raise RuntimeError("interaction refund remains unresolved")
            elif refund.get("related_item_id") != item["id"] or int(refund.get("change_amount", 0)) != -amount:
                raise ValueError("reconciliation refund identity is inconsistent")
        await runtime.cancel(owner, source["turn_id"])

    # ==================== 内部：DeepSeek ====================
    async def _call_deepseek(self, messages: list[dict]) -> tuple[str, int]:
        """按配置调用 DeepSeek：默认最多重试 5 次，总预算 60 秒。"""
        retry_rules = self.config_service.get_points_rules().get("deepseek_retry", {})
        max_retries = int(retry_rules.get("max_retries", 5) or 0)
        total_timeout = float(retry_rules.get("total_timeout_seconds", 60))
        attempt_timeout = float(retry_rules.get("attempt_timeout_seconds", 20))

        model = os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro")
        started = time.monotonic()
        attempts = 0
        last_error: Exception | None = None

        while attempts <= max_retries:
            remaining = total_timeout - (time.monotonic() - started)
            if remaining <= 0:
                break
            attempts += 1
            try:
                response = await asyncio.wait_for(
                    checked_completion(self.client,
                        model=model,
                        messages=public_messages(messages, 'handlers/interaction_handler.py'),
                        temperature=0.8,
                    ),
                    timeout=min(attempt_timeout, remaining),
                )
                content = (response.choices[0].message.content or "").strip()
                if not content:
                    raise RuntimeError("empty_reply")
                return content, attempts
            except asyncio.TimeoutError as exc:
                last_error = exc
                self.logger.warning(
                    f"[互动] DeepSeek 第 {attempts} 次调用超时（已耗时 {time.monotonic() - started:.1f}s）"
                )
            except Exception as exc:  # noqa: BLE001 - 任何异常都按可重试处理
                last_error = exc
                self.logger.warning(f"[互动] DeepSeek 第 {attempts} 次调用失败: {exc}")

        elapsed_ms = int((time.monotonic() - started) * 1000)
        raise InteractionAIError(f"DeepSeek 调用失败: {last_error}", attempts=attempts, elapsed_ms=elapsed_ms)

    async def _handle_ai_failure(
        self,
        user_id: str,
        item: dict[str, Any],
        request_id: str,
        cost: int,
        reason: str,
        attempts: int,
        elapsed_ms: int,
    ) -> bool:
        """FR-9：幂等退回积分；仅在失败记录持久化后下发兜底完成帧。"""
        refunded = await self._refund_charge(user_id, item, request_id, cost)
        # Detailed AI failure reason remains in logs/alerts only, never client events.

        now_ms = int(time.time() * 1000)
        failure_message_id = f"interaction_fail_{uuid.uuid4().hex}"
        try:
            await self.append_timeline(
                [
                    {
                        "messageId": failure_message_id,
                        "userId": user_id,
                        "role": "bot",
                        "content": DEFAULT_FALLBACK_TEXT,
                        "timestamp": now_ms,
                        "contentType": "interaction",
                        "itemId": item["id"],
                        "interactionFailed": True,
                    }
                ]
            )
        except Exception:
            self.logger.exception("[互动] AI失败兜底时间线写入失败")
            await self._push(
                user_id,
                {
                    "type": "bot.error",
                    "requestId": request_id,
                    "payload": {
                        "errorCode": "STORAGE_FAILED_REFUNDED",
                        "message": "消息保存失败，请稍后重试",
                        "requestIds": [request_id],
                    },
                },
            )
        else:
            await self._push(
                user_id,
                {
                    "type": "chat.reply.stream",
                    "requestId": request_id,
                    "payload": {
                        "delta": "",
                        "done": True,
                        "messageId": failure_message_id,
                        "finalContent": DEFAULT_FALLBACK_TEXT,
                        "timestamp": now_ms,
                        "requestIds": [request_id],
                        "messageKind": "interaction_failed",
                        "interactionItemId": item["id"],
                        "interactionFailed": True,
                    },
                },
            )
        self._alert(
            f"⚠️ 互动回复生成失败（已退款）\n用户: {user_id}\n物品: {item['id']}（{cost} 积分）\n"
            f"重试次数: {attempts}\n耗时: {elapsed_ms}ms\n原因: {reason}\n"
            f"累计失败次数: {self._ai_failure_count}"
        )
        return refunded

    async def _refund_charge(
        self, user_id: str, item: dict[str, Any], request_id: str, cost: int
    ) -> bool:
        """Refund an interaction charge exactly once via the points ledger key."""
        try:
            refund = self.points_service.refund_item(
                user_id=user_id,
                item_id=item["id"],
                amount=cost,
                idempotency_key=request_id,
                business_date=get_business_today_str(),
                note=f"互动失败退回（{item['name']}）",
            )
            if refund.created:
                balance = self.points_service.get_balance(user_id)
                await self._push(user_id, points_changed_event(refund.entry, balance=balance))
            return True
        except Exception as exc:
            self.logger.exception(f"[互动] 积分退回失败，需人工核对: user={user_id}, key={request_id}, err={exc}")
            self._alert(
                f"❗ 互动积分退回失败\n用户: {user_id}\n物品: {item['id']}\n"
                f"幂等键: {request_id}\n金额: {cost}\n错误: {exc}"
            )
            return False

    # ==================== 内部：上下文组装（FR-7） ====================
    async def build_prompt_preview(
        self, user_id: str, item_id: str, attachment: str = ""
    ) -> dict[str, Any] | None:
        """返回真实发送给 DeepSeek 的完整 messages（供后台核对 Prompt 效果）。

        与 ``send`` 走同一套组装逻辑，唯一的差别是不会发起模型调用、也不产生任何副作用。
        """
        item = self.config_service.get_item(item_id)
        if not item or not item.get("is_active"):
            return None
        messages, _polluted = await self._build_messages(user_id, item, attachment=attachment)
        return {
            "itemId": item["id"],
            "itemName": item["name"],
            "costPoints": item.get("cost_points"),
            "model": os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
            "temperature": 0.8,
            "promptTemplateId": item.get("prompt_template_id"),
            "promptTemplate": self.config_service.render_prompt(item),
            "attachment": attachment or None,
            "messageCount": len(messages),
            "messages": messages,
        }

    async def _build_messages(
        self,
        user_id: str,
        item: dict[str, Any],
        attachment: str = "",
    ):
        """组装发送给 DeepSeek 的 messages（PRD FR-7）。

        `attachment` 为「附言」文本（O5）：
        - 用户在输入框里写了字再点礼物 → 走 `attachment`，直接拼在互动回合里；
        - 用户先发了消息、防抖还没处理就点了礼物 → 由 `_pending_user_texts()` 从缓冲里补，
          以「【他刚刚还说】」的形式附在指令块，避免答非所问。
        """
        history = self.state.user_chat_history.setdefault(user_id, [])
        service = ChatApplicationService(self.state, self.history_store, self.prompt_service, self.weather_service)
        _, system_content = await service.system_context(user_id)
        recent_history, polluted_tails = service.history_context(
            history if getattr(service, "legacy_allowed", True) else [], limit=10, after_separator=True
        )

        # EDGE-9：模板在请求发起时快照，后台更新只对之后的请求生效
        interaction_prompt = self.config_service.render_prompt(item)

        # 附言优先；没有附言时才回退到防抖缓冲（两者不叠加，避免同一句话出现两次）
        attachment = (attachment or "").strip()
        pending_text = "" if attachment else self._pending_user_texts(user_id)
        pending_block = f"\n【他刚刚还说】：{pending_text}" if pending_text else ""

        anchor_prompt = (
            "【强制提醒】：保持“酷但笨拙”人设；禁止动作叙事；回复长短跟随内容。"
            "\n【注意力锚定】：只针对本次互动事件回复，不要回应历史中已结束的话题。"
            "\n【去重要求】：避免复用最近10次回复的开头词、句式骨架和结尾口头禅。"
            "\n【禁止时间戳】：历史消息中的【时间】标记仅供你理解时间线，绝对禁止在回复中输出时间戳。"
            f"{pending_block}"
            f"\n\n{interaction_prompt}"
        )

        user_turn = f"（{item.get('icon', '')}{item['name']}）"
        if attachment and attachment.strip():
            user_turn = f"{user_turn}{attachment.strip()}"

        messages = service.messages(system_content, recent_history, anchor_prompt, {"role": "user", "content": user_turn})
        return messages, polluted_tails

    def _pending_user_texts(self, user_id: str, limit: int = 160) -> str:
        """取该用户**尚未进入模型处理**的输入（防抖缓冲里的文字），供礼物回复参考。"""
        if not self.pending_text_provider:
            return ""
        try:
            texts = self.pending_text_provider(user_id) or []
        except Exception as exc:
            self.logger.warning(f"[互动] 读取待处理消息失败: {exc}")
            return ""
        merged = " ".join(text.strip() for text in texts if text and text.strip())
        return merged[-limit:].strip()

    def _append_history(
        self,
        user_id: str,
        item: dict[str, Any],
        reply: str,
        now_ms: int,
        attachment: str = "",
    ) -> None:
        user_record = f"（用户送了你{item.get('icon', '')}{item['name']}）"
        if attachment and attachment.strip():
            user_record = f"{user_record}{attachment.strip()}"
        service = ChatApplicationService(self.state, self.history_store, self.prompt_service, self.weather_service)
        service.commit_history(user_id, [
            {"role": "user", "content": user_record, "ts": now_ms - 1},
            {"role": "assistant", "content": reply, "ts": now_ms},
        ])

    # ==================== 内部：杂项 ====================
    async def push_event(self, user_id: str, event: dict[str, Any]) -> None:
        """对外暴露的事件推送入口（补签、定时任务等复用同一条 WS 通道）。"""
        await self._push(user_id, event)

    async def _push(self, user_id: str, event: dict[str, Any]) -> None:
        if event.get("type") == "bot.error":
            event.setdefault("payload", {}).setdefault("timestamp", int(time.time() * 1000))
        try:
            await self.broadcast_json(user_id, event)
        except Exception as exc:
            self.logger.warning(f"[互动] 推送事件失败: {exc}")

    def _remember(self, request_id: str, result: InteractionSendResult) -> None:
        key = (result.user_id, request_id)
        self._recent_results[key] = result
        self._recent_order.append(key)
        while len(self._recent_order) > self._recent_limit:
            stale = self._recent_order.pop(0)
            self._recent_results.pop(stale, None)

    def _alert(self, text: str) -> None:
        if not self.alert_sender:
            return
        try:
            self.alert_sender(text)
        except Exception as exc:
            self.logger.warning(f"[互动] 飞书告警发送失败: {exc}")
