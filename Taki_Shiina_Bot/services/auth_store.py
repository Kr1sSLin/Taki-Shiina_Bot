from __future__ import annotations

import hashlib
import json
import os
import secrets
import threading
import time


class DeviceLimitError(RuntimeError):
    pass


class InvalidTokenError(RuntimeError):
    pass


class RefreshTokenStore:
    def __init__(self, path: str, max_devices: int = 4):
        self.path = path
        self.max_devices = max_devices
        self._lock = threading.Lock()

    def _load(self) -> dict:
        if not os.path.exists(self.path):
            return {"version": 1, "tokens": []}
        try:
            with open(self.path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if not isinstance(data, dict):
                return {"version": 1, "tokens": []}
            data.setdefault("tokens", [])
            return data
        except Exception:
            return {"version": 1, "tokens": []}

    def _save(self, data: dict) -> None:
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)

    def _hash_token(self, token: str) -> str:
        return hashlib.sha256(token.encode("utf-8")).hexdigest()

    def _prune_expired(self, data: dict, now_ms: int) -> None:
        data["tokens"] = [
            entry for entry in data.get("tokens", []) if int(entry.get("expiresAt", 0)) > now_ms
        ]

    def issue(self, user_id: str, device_id: str, ttl_seconds: int) -> str:
        now_ms = int(time.time() * 1000)
        with self._lock:
            data = self._load()
            self._prune_expired(data, now_ms)

            active_devices = {
                entry.get("deviceId")
                for entry in data.get("tokens", [])
                if entry.get("userId") == user_id
            }
            if device_id not in active_devices and self.max_devices > 0:
                while len(active_devices) >= self.max_devices:
                    oldest = min(
                        (e for e in data["tokens"] if e.get("userId") == user_id),
                        key=lambda e: int(e.get("issuedAt", 0)),
                        default=None
                    )
                    if oldest is None:
                        break
                    data["tokens"].remove(oldest)
                    active_devices = {
                        entry.get("deviceId")
                        for entry in data.get("tokens", [])
                        if entry.get("userId") == user_id
                    }

            data["tokens"] = [
                entry
                for entry in data.get("tokens", [])
                if not (entry.get("userId") == user_id and entry.get("deviceId") == device_id)
            ]

            refresh_token = secrets.token_urlsafe(48)
            data["tokens"].append(
                {
                    "tokenHash": self._hash_token(refresh_token),
                    "userId": user_id,
                    "deviceId": device_id,
                    "issuedAt": now_ms,
                    "expiresAt": now_ms + max(1, int(ttl_seconds)) * 1000,
                }
            )
            self._save(data)
            return refresh_token

    def rotate(self, refresh_token: str, ttl_seconds: int) -> tuple[str, str, str]:
        now_ms = int(time.time() * 1000)
        token_hash = self._hash_token(refresh_token)
        with self._lock:
            data = self._load()
            self._prune_expired(data, now_ms)
            entry = next(
                (item for item in data.get("tokens", []) if item.get("tokenHash") == token_hash),
                None,
            )
            if not entry:
                raise InvalidTokenError("refresh token invalid or expired")

            user_id = str(entry.get("userId") or "")
            device_id = str(entry.get("deviceId") or "")
            if not user_id or not device_id:
                raise InvalidTokenError("refresh token invalid")

            data["tokens"] = [
                item for item in data.get("tokens", []) if item.get("tokenHash") != token_hash
            ]

            new_refresh = secrets.token_urlsafe(48)
            data["tokens"].append(
                {
                    "tokenHash": self._hash_token(new_refresh),
                    "userId": user_id,
                    "deviceId": device_id,
                    "issuedAt": now_ms,
                    "expiresAt": now_ms + max(1, int(ttl_seconds)) * 1000,
                }
            )
            self._save(data)
            return user_id, device_id, new_refresh
