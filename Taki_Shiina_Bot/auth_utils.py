from __future__ import annotations

import base64
import hashlib
import hmac
import json
import time
from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class AuthContext:
    user_id: str
    device_id: str


@dataclass(frozen=True)
class DeviceEntry:
    user_id: str
    device_id: str
    token: str


def normalize_token(token: str) -> str:
    raw = (token or "").strip()
    if raw.lower().startswith("bearer "):
        return raw[7:].strip()
    return raw


def parse_device_tokens(
    raw: str,
    default_user_id: str,
    max_devices: int = 4,
) -> tuple[dict[str, AuthContext], dict[str, DeviceEntry]]:
    if not raw:
        return {}, {}
    entries = [item.strip() for item in raw.split(",") if item.strip()]
    if not entries:
        return {}, {}
    if max_devices > 0 and len(entries) > max_devices:
        raise ValueError(f"BOT_DEVICE_TOKENS 超过最大设备数限制 ({max_devices})")

    token_map: dict[str, AuthContext] = {}
    allowlist: dict[str, DeviceEntry] = {}
    device_ids: set[str] = set()
    for entry in entries:
        parts = [part.strip() for part in entry.split(":", 2)]
        if len(parts) == 2:
            device_id, token = parts
            user_id = default_user_id
        elif len(parts) == 3:
            user_id, device_id, token = parts
        else:
            raise ValueError(f"BOT_DEVICE_TOKENS 格式错误: {entry}")

        if not user_id or not device_id or not token:
            raise ValueError(f"BOT_DEVICE_TOKENS 存在空字段: {entry}")

        normalized = normalize_token(token)
        if not normalized:
            raise ValueError(f"BOT_DEVICE_TOKENS token 不能为空: {entry}")
        if normalized in token_map:
            raise ValueError("BOT_DEVICE_TOKENS 存在重复 token")
        if device_id in device_ids:
            raise ValueError("BOT_DEVICE_TOKENS 存在重复 device_id")

        device_ids.add(device_id)
        token_map[normalized] = AuthContext(user_id=user_id, device_id=device_id)
        allowlist[device_id] = DeviceEntry(user_id=user_id, device_id=device_id, token=normalized)

    return token_map, allowlist


def _b64url_encode(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode("utf-8")


def _b64url_decode(data: str) -> bytes:
    padding = "=" * (-len(data) % 4)
    return base64.urlsafe_b64decode(data + padding)


def _sign_jwt(message: bytes, secret: str) -> str:
    digest = hmac.new(secret.encode("utf-8"), message, hashlib.sha256).digest()
    return _b64url_encode(digest)


def encode_jwt(payload: dict[str, Any], secret: str) -> str:
    header = {"alg": "HS256", "typ": "JWT"}
    header_b64 = _b64url_encode(json.dumps(header, separators=(",", ":")).encode("utf-8"))
    payload_b64 = _b64url_encode(json.dumps(payload, separators=(",", ":")).encode("utf-8"))
    signing_input = f"{header_b64}.{payload_b64}".encode("utf-8")
    signature = _sign_jwt(signing_input, secret)
    return f"{header_b64}.{payload_b64}.{signature}"


def decode_jwt(token: str, secret: str) -> dict[str, Any] | None:
    parts = token.split(".")
    if len(parts) != 3:
        return None
    header_b64, payload_b64, signature = parts
    signing_input = f"{header_b64}.{payload_b64}".encode("utf-8")
    expected = _sign_jwt(signing_input, secret)
    if not hmac.compare_digest(signature, expected):
        return None
    try:
        payload_raw = _b64url_decode(payload_b64)
        payload = json.loads(payload_raw.decode("utf-8"))
    except Exception:
        return None
    return payload if isinstance(payload, dict) else None


def create_access_token(
    user_id: str,
    device_id: str,
    secret: str,
    expires_in_seconds: int,
) -> str:
    now = int(time.time())
    payload = {
        "sub": user_id,
        "device_id": device_id,
        "typ": "access",
        "iat": now,
        "exp": now + max(1, int(expires_in_seconds)),
    }
    return encode_jwt(payload, secret)


def verify_access_token(token: str, secret: str) -> AuthContext | None:
    payload = decode_jwt(token, secret)
    if not payload:
        return None
    if payload.get("typ") != "access":
        return None
    exp = payload.get("exp")
    if not isinstance(exp, int) or exp <= int(time.time()):
        return None
    user_id = str(payload.get("sub") or "").strip()
    device_id = str(payload.get("device_id") or "").strip()
    if not user_id or not device_id:
        return None
    return AuthContext(user_id=user_id, device_id=device_id)


def hash_password(password: str, salt: str, iterations: int = 200_000) -> str:
    dk = hashlib.pbkdf2_hmac(
        "sha256",
        password.encode("utf-8"),
        salt.encode("utf-8"),
        iterations,
    )
    return _b64url_encode(dk)


def verify_password(password: str, salt: str, expected_hash: str, iterations: int = 200_000) -> bool:
    if not expected_hash or not salt:
        return False
    calculated = hash_password(password, salt, iterations)
    return hmac.compare_digest(calculated, expected_hash)


def resolve_auth_context(
    token: str,
    jwt_secret: str | None,
    device_allowlist: dict[str, DeviceEntry],
    legacy_token_map: dict[str, AuthContext],
    fallback_tokens: list[str],
    default_user_id: str,
) -> AuthContext | None:
    normalized = normalize_token(token)
    if not normalized:
        return None

    if jwt_secret:
        jwt_context = verify_access_token(normalized, jwt_secret)
        if jwt_context:
            if device_allowlist:
                entry = device_allowlist.get(jwt_context.device_id)
                if not entry or entry.user_id != jwt_context.user_id:
                    return None
            return jwt_context

    if legacy_token_map:
        return legacy_token_map.get(normalized)

    allowed = {normalize_token(value) for value in fallback_tokens if value}
    if normalized in allowed:
        return AuthContext(user_id=default_user_id, device_id="legacy")
    return None
