"""Text generation outside SQL; endpoint framing stays in protocol adapters."""
import asyncio
import base64
import binascii
import hashlib
import json
import os
import time

from app_constants import EMOTIONAL_TRIGGERS, LORE_TRIGGERS
from services.application_service import ChatApplicationService
from services.privacy import scrub_locations
from services.vision_limits import MAX_INLINE_IMAGE_BYTES
from text_utils import inject_emojis, is_repetitive_reply, sanitize_taki_reply, strip_polluted_tail


class TextGenerator:
    def __init__(self, state, history_store, prompt_service, weather_service, client, parse_timer, *, interaction_retry_provider=None, vision_provider=None):
        self.state, self.history_store = state, history_store
        self.prompt_service, self.weather_service = prompt_service, weather_service
        self.client, self.parse_timer = client, parse_timer
        self.interaction_retry_provider = interaction_retry_provider
        self.vision_provider = vision_provider

    @staticmethod
    def _source_images(sources):
        """Decode a bounded image batch without changing any of its original bytes."""
        max_count = max(1, int(os.getenv("VISION_MAX_IMAGE_COUNT", "3")))
        max_bytes = max(1, int(os.getenv("VISION_MAX_IMAGE_MB", "20"))) * 1024 * 1024
        allowed = {mime.strip() for mime in os.getenv("VISION_ALLOWED_MIME", "image/jpeg,image/png").split(",") if mime.strip()}
        encoded_images = []
        for source in sources:
            metadata = source.get("metadata") or {}
            if not isinstance(metadata, dict) or not isinstance(metadata.get("images", []), list):
                raise ValueError("invalid image source metadata")
            encoded_images.extend(metadata.get("images", []))
        if len(encoded_images) > max_count:
            raise ValueError("too many images in one turn")
        decoded = []
        total_bytes = 0
        for image in encoded_images:
            if not isinstance(image, dict) or image.get("mimeType") not in allowed:
                raise ValueError("unsupported image type")
            data = image.get("dataBase64")
            if not isinstance(data, str) or len(data) > ((max_bytes + 2) // 3) * 4 + 4:
                raise ValueError("invalid image data")
            try:
                raw = base64.b64decode(data, validate=True)
            except (ValueError, binascii.Error):
                raise ValueError("invalid image data") from None
            if not raw or len(raw) > max_bytes or base64.b64encode(raw).decode("ascii") != data:
                raise ValueError("invalid image data")
            total_bytes += len(raw)
            if total_bytes > MAX_INLINE_IMAGE_BYTES:
                raise ValueError("images exceed Gemini inline request budget")
            expected_hash = image.get("sha256")
            if expected_hash is not None and (not isinstance(expected_hash, str) or hashlib.sha256(raw).hexdigest() != expected_hash):
                raise ValueError("image digest mismatch")
            decoded.append({"mimeType": image["mimeType"], "raw": raw})
        return decoded

    async def __call__(self, snapshot, store):
        owner = snapshot["owner"]
        service = ChatApplicationService(self.state, self.history_store, self.prompt_service, self.weather_service)
        _, system = await service.system_context(owner, allow_private_context=snapshot["legacy_allowed"])
        legacy_allowed = snapshot["legacy_allowed"] and getattr(service, "legacy_allowed", True)
        sql_history = await asyncio.to_thread(store.history, owner, for_model=True)
        history = list(self.state.user_chat_history.get(owner, [])) if legacy_allowed else []
        history += [{"role": "assistant" if row["role"] == "bot" else "user", "content": row["content"], "ts": row["timestamp"]} for row in sql_history]
        is_http = snapshot["sources"][-1].get("channel", "http") == "http"
        history, tails = service.history_context(history, limit=20 if is_http else 10, after_separator=not is_http)
        text = scrub_locations("\n".join(source["text"] for source in snapshot["sources"]))

        async def check_snapshot():
            await asyncio.to_thread(store.assert_snapshot, snapshot)

        images = self._source_images(snapshot["sources"])
        if images:
            if self.vision_provider is None:
                raise RuntimeError("vision model is not configured")
            await check_snapshot()
            description = await self.vision_provider(images, text)
            if not isinstance(description, str) or not description.strip():
                raise ValueError("vision model returned no description")
            text += "\n\n图片内容：" + scrub_locations(description.strip())
        emotional = next((instruction for keyword, instruction in EMOTIONAL_TRIGGERS.items() if keyword in text), "")
        lore = next((reaction for keyword, (reaction, _) in LORE_TRIGGERS.items() if keyword.casefold() in text.casefold()), "")
        anchor = "保持既有人设，只回复当前输入，不输出动作叙事、后台阶段或历史时间戳。避免复用最近回复的开头和结尾。" + emotional + lore
        interactions = [source for source in snapshot["sources"] if source.get("intent") == "interaction"]
        for source in interactions:
            anchor += "\n\n" + (source.get("metadata") or {}).get("prompt_template", "")
        messages = service.messages(system, history, anchor, {"role": "user", "content": text})
        attempts = 1
        if interactions:
            response, attempts = await self._interaction_response(messages, check_snapshot)
        else:
            await check_snapshot()
            response = await self.client.chat.completions.create(model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"), messages=messages, temperature=0.75)
        cleaned, timer_at, timer_text = self.parse_timer(response.choices[0].message.content or "")
        cleaned = strip_polluted_tail(cleaned, tails)
        recent = [message["content"] for message in history if message.get("role") == "assistant"]
        if not interactions and (not cleaned.strip() or is_repetitive_reply(cleaned, recent)):
            await check_snapshot()
            retry = await self.client.chat.completions.create(model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"), messages=[*messages, {"role": "system", "content": "请用自然、不同于近期回复的完整文字回答当前输入。"}], temperature=0.9)
            cleaned, timer_at, timer_text = self.parse_timer(retry.choices[0].message.content or "")
            cleaned = strip_polluted_tail(cleaned, tails)
        final = scrub_locations(inject_emojis(sanitize_taki_reply(cleaned)))
        if not final.strip():
            raise ValueError("model returned no usable reply")
        usage = getattr(response, "usage", None)
        result = {"reply": final, "model": os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"), "usage": {"promptTokens": getattr(usage,"prompt_tokens",0), "completionTokens": getattr(usage,"completion_tokens",0), "totalTokens": getattr(usage,"total_tokens",0)}}
        if interactions:
            source = interactions[-1]
            metadata = source.get("metadata") or {}
            item = metadata.get("item") or {}
            result["interaction"] = {"requestId": source["request_id"], "itemId": item.get("id"), "itemName": item.get("name"),
                                     "itemIcon": item.get("icon", ""), "costPoints": item.get("cost_points"),
                                     "attachment": metadata.get("attachment"), "originDeviceId": source["device_id"]}
            result["attempts"] = attempts
        if timer_at and timer_text:
            result["timerInstruction"] = {"target": timer_at, "text": scrub_locations(timer_text)}
        return result

    async def _interaction_response(self, messages, check_snapshot):
        """Preserve the interaction's six-attempt and 15-second model budget."""
        rules = self.interaction_retry_provider() if self.interaction_retry_provider else {}
        max_attempts = int(rules.get("max_retries", 5) or 0) + 1
        deadline = time.monotonic() + min(15, float(rules.get("total_timeout_seconds", 15) or 15))
        attempt_timeout = float(rules.get("attempt_timeout_seconds", 5) or 5)
        last_error = None
        for attempt in range(1, max_attempts + 1):
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            await check_snapshot()
            try:
                response = await asyncio.wait_for(self.client.chat.completions.create(
                    model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"), messages=messages, temperature=0.8),
                    timeout=min(attempt_timeout, remaining))
                if not (response.choices[0].message.content or "").strip():
                    raise ValueError("empty interaction reply")
                return response, attempt
            except Exception as error:
                last_error = error
        raise RuntimeError("interaction model retry budget exhausted") from last_error

    async def extract(self, text):
        response = await self.client.chat.completions.create(model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"), messages=[{"role":"system", "content":"只从当前用户自述中提取用户的客观事实，忽略他人、推测和闲聊。返回 JSON 字符串数组，无事实返回 []。"}, {"role":"user", "content": scrub_locations(text)}], temperature=0.3)
        raw = (response.choices[0].message.content or "[]").strip()
        if raw.startswith("```"):
            raw = raw.split("\n",1)[-1].rsplit("```",1)[0].strip()
        return json.loads(raw)
