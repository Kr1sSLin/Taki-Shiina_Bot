"""Shared application slice for HTTP/WS/interaction; protocol policies stay in adapters."""
from contextlib import asynccontextmanager
import asyncio
import copy

from services.privacy import scrub_locations
from services.permission_guard import PermissionGuard
from text_utils import clean_short_term_history, with_history_timestamp
from time_utils import build_time_block

HISTORY_SEPARATOR = "──── 新的一天 ────"
TIME_ENFORCEMENT = "\n".join((
    "1. **时间锚定**：你必须无条件信任上述【当前北京时间】，绝不能凭训练数据或常识猜测当前时间。",
    "2. **全天候禁止催睡**：无论现在是凌晨几点，绝对禁止主动建议用户去睡觉或休息。",
    "3. **深夜默契**：如果用户深夜还在发消息，表示他想通宵，你要配合而不是扫兴。",
))


class SessionCoordinator:
    def __init__(self):
        self._locks = {}

    @asynccontextmanager
    async def session(self, owner):
        lock = self._locks.setdefault(owner, asyncio.Lock())
        async with lock:
            yield


class ChatApplicationService:
    def __init__(self, state, history_store, prompt_service, weather_service):
        self.state = state
        self.history_store = history_store
        self.prompt_service = prompt_service
        self.weather_service = weather_service
        self.permission_guard = getattr(prompt_service, "permission_guard", None) or PermissionGuard()
        self.legacy_allowed = True

    async def system_context(self, owner, *, at=None, allow_private_context=True):
        self.legacy_allowed = bool(self.permission_guard.owner_context_allowed(owner) and allow_private_context)
        weather = await self.weather_service.get_weather_str(force=True)
        if self.legacy_allowed:
            prompt = await self.prompt_service.get_system_prompt(owner)
        else:
            prompt = await self.prompt_service.get_system_prompt(owner, allow_private_context=False)
        if self.legacy_allowed and not self.permission_guard.owner_context_allowed(owner):
            self.legacy_allowed = False
            prompt = await self.prompt_service.get_system_prompt(owner, allow_private_context=False)
        time = build_time_block(at) if at is not None else build_time_block()
        return scrub_locations(prompt), scrub_locations(f"{prompt}\n{time}\n{weather}\n\n{TIME_ENFORCEMENT}")

    @staticmethod
    def history_context(history, *, limit, after_separator):
        if after_separator:
            separator = next((i for i in range(len(history) - 1, -1, -1) if history[i].get("role") == "system" and history[i].get("content") == HISTORY_SEPARATOR), -1)
            history = history[separator + 1:]
            history = [m for m in history if not (m.get("role") == "system" and m.get("content") == HISTORY_SEPARATOR)]
        return clean_short_term_history(scrub_locations(history[-limit:]), min_repeat=3)

    @staticmethod
    def messages(system, history, anchor, user_message):
        return scrub_locations([{"role": "system", "content": system}, *[with_history_timestamp(m) for m in history], {"role": "system", "content": anchor}, user_message])

    def commit_history(self, owner, additions):
        # Publish in-memory context only after durable save succeeds.
        updated = copy.deepcopy(self.state.user_chat_history)
        updated[owner] = [*updated.get(owner, []), *scrub_locations(additions)][-300:]
        self.history_store.save(updated)
        self.state.user_chat_history.clear()
        self.state.user_chat_history.update(updated)
