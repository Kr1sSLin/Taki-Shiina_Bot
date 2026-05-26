from __future__ import annotations

import base64
import json
import os
import shutil
import time
from dataclasses import dataclass
from typing import Any

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


@dataclass(frozen=True)
class EncryptionConfig:
    key: bytes
    auto_migrate: bool
    backup_on_migrate: bool


class DataKeyError(RuntimeError):
    pass


class DataDecryptError(RuntimeError):
    pass


def _b64_decode(raw: str) -> bytes:
    padding = "=" * (-len(raw) % 4)
    return base64.urlsafe_b64decode(raw + padding)


def _b64_encode(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode("utf-8")


def load_encryption_config() -> EncryptionConfig:
    raw_key = (os.getenv("DATA_ENC_KEY", "") or "").strip()
    if not raw_key:
        raise DataKeyError("DATA_ENC_KEY 未配置")

    key = None
    try:
        key = _b64_decode(raw_key)
    except Exception:
        try:
            key = bytes.fromhex(raw_key)
        except Exception as exc:
            raise DataKeyError("DATA_ENC_KEY 格式错误（需 base64 或 hex）") from exc

    if key is None or len(key) not in (16, 24, 32):
        raise DataKeyError("DATA_ENC_KEY 长度无效（需 16/24/32 字节）")

    auto_migrate = (os.getenv("DATA_AUTO_MIGRATE", "1") or "1").strip() != "0"
    backup_on_migrate = (os.getenv("DATA_BACKUP_ON_MIGRATE", "1") or "1").strip() != "0"
    return EncryptionConfig(key=key, auto_migrate=auto_migrate, backup_on_migrate=backup_on_migrate)


def _is_encrypted_payload(obj: Any) -> bool:
    return (
        isinstance(obj, dict)
        and obj.get("ver") == 1
        and obj.get("alg") == "AESGCM"
        and "nonce" in obj
        and "ciphertext" in obj
    )


class SecureJsonStore:
    def __init__(self, path: str, logger, config: EncryptionConfig | None = None):
        self.path = path
        self.logger = logger
        self.config = config or load_encryption_config()

    def _encrypt(self, data: Any) -> dict[str, Any]:
        raw = json.dumps(data, ensure_ascii=False).encode("utf-8")
        nonce = os.urandom(12)
        aes = AESGCM(self.config.key)
        ciphertext = aes.encrypt(nonce, raw, None)
        return {
            "ver": 1,
            "alg": "AESGCM",
            "nonce": _b64_encode(nonce),
            "ciphertext": _b64_encode(ciphertext),
        }

    def _decrypt(self, payload: dict[str, Any]) -> Any:
        try:
            nonce = _b64_decode(str(payload["nonce"]))
            ciphertext = _b64_decode(str(payload["ciphertext"]))
            aes = AESGCM(self.config.key)
            raw = aes.decrypt(nonce, ciphertext, None)
            return json.loads(raw.decode("utf-8"))
        except Exception as exc:
            raise DataDecryptError("解密失败，请检查 DATA_ENC_KEY") from exc

    def _write_atomic(self, payload: dict[str, Any]) -> None:
        dir_path = os.path.dirname(self.path)
        if dir_path:
            os.makedirs(dir_path, exist_ok=True)
        tmp_path = f"{self.path}.tmp"
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        os.replace(tmp_path, self.path)

    def _backup_plaintext(self) -> None:
        if not self.config.backup_on_migrate:
            return
        timestamp = time.strftime("%Y%m%d%H%M%S")
        backup_path = f"{self.path}.bak.{timestamp}"
        shutil.copy2(self.path, backup_path)

    def load(self, default: Any) -> Any:
        if not os.path.exists(self.path):
            return default
        try:
            with open(self.path, "r", encoding="utf-8") as f:
                payload = json.load(f)
        except Exception as exc:
            self.logger.error(f"读取数据文件失败: {exc}")
            return default

        if _is_encrypted_payload(payload):
            try:
                return self._decrypt(payload)
            except DataDecryptError as exc:
                self.logger.error(str(exc))
                raise

        if self.config.auto_migrate:
            try:
                self._backup_plaintext()
                self._write_atomic(self._encrypt(payload))
                self.logger.info(f"已迁移明文数据并加密: {os.path.basename(self.path)}")
            except Exception as exc:
                self.logger.error(f"迁移明文数据失败: {exc}")
        return payload

    def save(self, data: Any) -> None:
        try:
            payload = self._encrypt(data)
            self._write_atomic(payload)
        except Exception as exc:
            self.logger.error(f"加密保存失败: {exc}")
