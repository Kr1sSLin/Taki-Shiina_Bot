from __future__ import annotations

import base64
import json
import os
import tempfile
from dataclasses import dataclass
from typing import Any

from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.exceptions import InvalidTag
from services.privacy import scrub_locations


@dataclass(frozen=True)
class EncryptionConfig:
    key: bytes
    auto_migrate: bool
    backup_on_migrate: bool


class DataKeyError(RuntimeError):
    pass


class DataDecryptError(RuntimeError):
    pass


class DataCorruptionError(ValueError):
    """Invalid JSON, encrypted envelope or decoded data; never an empty store."""


class DataReadError(OSError):
    pass


class DataWriteError(OSError):
    pass


def _b64_decode(raw: str) -> bytes:
    padding = "=" * (-len(raw) % 4)
    return base64.b64decode(raw + padding, altchars=b"-_", validate=True)


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
        pass
    if key is None or len(key) not in (16, 24, 32):
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
        raw = json.dumps(scrub_locations(data), ensure_ascii=False).encode("utf-8")
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
        if not _is_encrypted_payload(payload):
            raise DataCorruptionError("加密数据格式或版本无效")
        try:
            nonce = _b64_decode(str(payload["nonce"]))
            ciphertext = _b64_decode(str(payload["ciphertext"]))
            if len(nonce) != 12 or len(ciphertext) < 16:
                raise ValueError("invalid encrypted lengths")
        except (ValueError, TypeError) as exc:
            raise DataCorruptionError("加密数据编码或长度无效") from exc
        try:
            aes = AESGCM(self.config.key)
            raw = aes.decrypt(nonce, ciphertext, None)
        except InvalidTag as exc:
            # Authentication alone cannot distinguish a wrong key from tampering.
            raise DataDecryptError("数据认证失败：密钥不匹配或密文被修改") from exc
        try:
            return json.loads(raw.decode("utf-8"))
        except (UnicodeError, ValueError) as exc:
            raise DataCorruptionError("解密后的数据内容无效") from exc

    def _write_atomic(self, payload: dict[str, Any]) -> None:
        dir_path = os.path.dirname(self.path)
        if dir_path:
            os.makedirs(dir_path, exist_ok=True)
        fd, tmp_path = tempfile.mkstemp(
            prefix=os.path.basename(self.path) + ".", suffix=".tmp",
            dir=os.path.dirname(os.path.abspath(self.path)),
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(payload, f, ensure_ascii=False, indent=2)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp_path, self.path)
        finally:
            if os.path.exists(tmp_path):
                os.unlink(tmp_path)

    def _backup_encrypted(self, data: Any) -> None:
        """Verify a recoverable encrypted backup before replacing plaintext."""
        if not self.config.backup_on_migrate:
            return
        payload = self._encrypt(data)
        # mkstemp creates a unique file with owner-only permissions on POSIX.
        # Only ciphertext ever reaches disk; failed backups are removed.
        fd, backup_path = tempfile.mkstemp(
            prefix=os.path.basename(self.path) + ".bak.",
            suffix=".enc",
            dir=os.path.dirname(os.path.abspath(self.path)),
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as backup:
                json.dump(payload, backup, ensure_ascii=False, indent=2)
                backup.flush()
                os.fsync(backup.fileno())
            with open(backup_path, "r", encoding="utf-8") as backup:
                restored = self._decrypt(json.load(backup))
            # Compare serialized forms to preserve JSON normalization semantics.
            if json.dumps(restored, ensure_ascii=False) != json.dumps(scrub_locations(data), ensure_ascii=False):
                raise DataDecryptError("迁移备份校验失败，原文件未替换")
        except Exception:
            os.unlink(backup_path)
            raise

    def load(self, default: Any) -> Any:
        return self.load_strict(default)

    def save(self, data: Any) -> None:
        self.save_strict(data)

    # Both public spellings now fail closed. Only a missing file returns default.

    def _read(self):
        try:
            with open(self.path, "r", encoding="utf-8") as f:
                return json.load(f)
        except FileNotFoundError:
            raise
        except (ValueError, UnicodeError) as exc:
            raise DataCorruptionError("数据文件内容损坏") from exc
        except OSError as exc:
            raise DataReadError("数据文件读取失败") from exc

    def _decode(self, payload):
        if isinstance(payload, dict) and any(k in payload for k in ("ver", "alg", "nonce", "ciphertext")):
            return scrub_locations(self._decrypt(payload)), True
        return scrub_locations(payload), False

    def load_strict(self, default: Any) -> Any:
        try:
            payload = self._read()
        except FileNotFoundError:
            return default
        data, encrypted = self._decode(payload)
        if isinstance(default, (dict, list)) and not isinstance(data, type(default)):
            raise DataCorruptionError("数据文件根节点类型无效")
        if not encrypted and self.config.auto_migrate:
            try:
                self._backup_encrypted(data)
                self._write_atomic(self._encrypt(data))
            except OSError as exc:
                raise DataWriteError("数据迁移写入失败，原文件未替换") from exc
            self.logger.info(f"已迁移明文数据并加密: {os.path.basename(self.path)}")
        return data

    def save_strict(self, data: Any) -> None:
        # Never replace existing unreadable data, including after a failed load.
        try:
            old_data, _ = self._decode(self._read())
            if isinstance(data, (dict, list)) and not isinstance(old_data, type(data)):
                raise DataCorruptionError("拒绝覆盖根节点类型不一致的数据")
        except FileNotFoundError:
            pass
        payload = self._encrypt(data)
        try:
            self._write_atomic(payload)
        except OSError as exc:
            raise DataWriteError("数据写入失败，原文件未替换") from exc
