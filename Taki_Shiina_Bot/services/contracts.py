"""Stage A shared identities. Adapters supply authenticated owner, never the model."""
import base64
import binascii
from dataclasses import dataclass, field
from enum import StrEnum
import hashlib
from uuid import uuid4


def new_id() -> str:
    return uuid4().hex


class Domain(StrEnum):
    USER_FACT = "user_fact"
    CHARACTER_DAILY = "character_daily"
    OFFICIAL_LORE = "official_lore"
    SOURCE_MESSAGE = "source_message"
    EVIDENCE = "evidence"
    DERIVED = "derived"
    JOB = "job"
    EVENT = "event"
    DELIVERY = "delivery"


@dataclass(frozen=True)
class ChatCommand:
    owner: str
    request_id: str
    client_message_id: str
    device_id: str
    text: str
    context_revision: int = 0
    channel: str = "http"
    intent: str = "chat"
    metadata: dict | None = None

    def __post_init__(self):
        if not all(isinstance(v, str) and v.strip() for v in (self.owner, self.request_id, self.client_message_id, self.device_id, self.text)):
            raise ValueError("chat identity and text are required")
        if any(len(v) > 200 for v in (self.owner, self.request_id, self.client_message_id, self.device_id)):
            raise ValueError("chat identity is too long")
        if self.intent not in ("chat", "interaction") or (self.metadata is not None and not isinstance(self.metadata, dict)):
            raise ValueError("unsupported chat intent or metadata")
        if self.metadata is not None and "images" in self.metadata:
            images = self.metadata["images"]
            if not isinstance(images, list):
                raise ValueError("images must be a list")
            for image in images:
                if not isinstance(image, dict) or set(image) != {"mimeType", "dataBase64", "sha256"}:
                    raise ValueError("invalid image source")
                mime, encoded, digest = image["mimeType"], image["dataBase64"], image["sha256"]
                if not isinstance(mime, str) or not mime.startswith("image/") or not isinstance(encoded, str) or not isinstance(digest, str):
                    raise ValueError("invalid image source")
                try:
                    raw = base64.b64decode(encoded, validate=True)
                except (ValueError, binascii.Error) as exc:
                    raise ValueError("invalid image source") from exc
                if not raw or base64.b64encode(raw).decode("ascii") != encoded or hashlib.sha256(raw).hexdigest() != digest:
                    raise ValueError("invalid image source")


@dataclass(frozen=True)
class LogicalTurn:
    source_message_ids: tuple[str, ...]
    created_at: float
    deadline: float
    turn_id: str = field(default_factory=new_id)
    generation: int = 1
    status: str = "pending"


@dataclass(frozen=True)
class FactRef:
    fact_id: str
    revision: int


@dataclass(frozen=True)
class GenerationContext:
    turn_id: str
    generation: int
    source_message_ids: tuple[str, ...]
    eligibility: tuple[FactRef, ...]
    prompt_version: str
    deadline: float
