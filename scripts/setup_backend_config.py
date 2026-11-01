"""Configure Linux deployment without executing .env as shell code."""
from __future__ import annotations

import argparse
import base64
import getpass
import hashlib
import json
import os
from pathlib import Path
import secrets
import tempfile
import time

from dotenv import dotenv_values, set_key


def missing(value: str | None) -> bool:
    return not value or value.startswith("your_")


def ask(label: str, *, secret: bool = False, interactive: bool) -> str:
    if not interactive:
        raise ValueError(f"缺少 {label}；请先填写后端 .env 或在交互终端运行。")
    value = (getpass.getpass(f"{label}: ") if secret else input(f"{label}: ")).strip()
    if missing(value) or "\n" in value or "\r" in value or "${" in value:
        raise ValueError(f"{label} 不能为空、占位符或包含换行/变量插值。")
    return value


def configure(backend: Path, *, interactive: bool) -> dict[str, str]:
    env_path = backend / ".env"
    if env_path.is_symlink():
        raise ValueError(".env 为符号链接，请先确认配置文件位置并改用普通文件。")
    template = backend / ".env.example"
    original_bytes = env_path.read_bytes() if env_path.exists() else template.read_bytes()
    original = original_bytes.decode("utf-8")
    defaults = dict(dotenv_values(template, interpolate=False))
    existing = dict(dotenv_values(env_path, interpolate=False)) if env_path.exists() else {}
    values = {**defaults, **existing}
    # Resolve exactly the file being installed, not unrelated caller environment.
    if any("${" in (value or "") for value in values.values()):
        raise ValueError("自动部署要求 .env 使用实际值，暂不支持 ${...} 插值；请先展开这些值。")

    for key in ("AUTH_USERNAME", "DEEPSEEK_API_KEY"):
        if missing(values.get(key)):
            values[key] = ask(key, secret=key.endswith("KEY"), interactive=interactive)
    if missing(values.get("AUTH_USER_ID")):
        # Preserve the backend's AUTH_USERNAME / APP_USER_ID fallback.
        values["AUTH_USER_ID"] = ""
    password = values.get("AUTH_PASSWORD")
    valid_hash = not missing(values.get("AUTH_PASSWORD_HASH"))
    valid_salt = not missing(values.get("AUTH_PASSWORD_SALT"))
    if valid_hash != valid_salt:
        raise ValueError("AUTH_PASSWORD_HASH / AUTH_PASSWORD_SALT 必须成对提供，请恢复完整配置。")
    if not (valid_hash and valid_salt):
        if missing(password):
            password = ask("登录密码（至少 12 位）", secret=True, interactive=interactive)
            if len(password) < 12:
                raise ValueError("新登录密码至少需要 12 位。")
            if password != ask("再次输入登录密码", secret=True, interactive=interactive):
                raise ValueError("两次密码不一致。")
        salt = secrets.token_urlsafe(24)
        digest = hashlib.pbkdf2_hmac("sha256", password.encode(), salt.encode(), 200_000)
        values["AUTH_PASSWORD_SALT"] = salt
        values["AUTH_PASSWORD_HASH"] = base64.urlsafe_b64encode(digest).rstrip(b"=").decode()
    values["AUTH_PASSWORD"] = ""
    for key in ("AUTH_JWT_SECRET", "BOT_HTTP_TOKEN", "BOT_WS_TOKEN", "ADMIN_API_TOKEN"):
        if missing(values.get(key)):
            values[key] = secrets.token_urlsafe(32)

    data_dir = Path(values.get("BOT_DATA_DIR") or backend)
    if not data_dir.is_absolute():
        data_dir = backend / data_dir
    encrypted_files = []
    candidates: set[Path] = set()
    # Auth stores still live in the source directory even with BOT_DATA_DIR set.
    def scan_error(error: OSError) -> None:
        raise error

    for directory in {backend, data_dir}:
        if not directory.exists():
            continue
        for root, dirs, files in os.walk(directory, onerror=scan_error):
            dirs[:] = [name for name in dirs if not name.startswith(".") and name not in {"__pycache__", "node_modules", "venv"}]
            candidates.update(Path(root) / name for name in files if name.endswith(".json"))
    for path in sorted(candidates):
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (ValueError, UnicodeError):
            continue
        if isinstance(payload, dict) and payload.get("alg") == "AESGCM":
            encrypted_files.append((path, payload))
    if missing(values.get("DATA_ENC_KEY")):
        if encrypted_files:
            raise ValueError("检测到已有加密数据但 DATA_ENC_KEY 缺失；请恢复原密钥，不能生成新密钥。")
        values["DATA_ENC_KEY"] = base64.urlsafe_b64encode(secrets.token_bytes(32)).decode()
    # Match secure_storage.load_encryption_config (including its hex fallback).
    raw_key = values["DATA_ENC_KEY"].strip()
    try:
        key = base64.urlsafe_b64decode(raw_key + "=" * (-len(raw_key) % 4))
    except Exception:
        try:
            key = bytes.fromhex(raw_key)
        except ValueError:
            raise ValueError("DATA_ENC_KEY 格式无效。") from None
    if len(key) not in (16, 24, 32):
        raise ValueError("DATA_ENC_KEY 无法被后端读取；请恢复正确的 base64 密钥，勿更换已有数据的密钥。")
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM

    for path, payload in encrypted_files:
        try:
            AESGCM(key).decrypt(base64.urlsafe_b64decode(payload["nonce"]), base64.urlsafe_b64decode(payload["ciphertext"]), None)
        except Exception:
            raise ValueError(f"密钥无法解密 {path.name}；未修改配置，请恢复原密钥。") from None

    if values.get("BOT_UNIFIED_HOST") != "127.0.0.1":
        raise ValueError("请将 .env 中 BOT_UNIFIED_HOST 设为 127.0.0.1，再通过 TLS 反代接入。")
    try:
        port = int(values.get("BOT_UNIFIED_PORT") or "8000")
    except ValueError:
        raise ValueError("BOT_UNIFIED_PORT 必须是整数。") from None
    if not 1024 <= port <= 65535:
        raise ValueError("BOT_UNIFIED_PORT 必须在 1024～65535 之间。")
    values["BOT_UNIFIED_PORT"] = str(port)
    if values.get("ENABLE_ADMIN_TEST_API") != "0":
        raise ValueError("请在生产 .env 中设置 ENABLE_ADMIN_TEST_API=0。")
    # Optional provider placeholders must not masquerade as real credentials.
    for name, value in list(values.items()):
        if value and value.startswith("your_"):
            values[name] = ""

    fd, temp_name = tempfile.mkstemp(prefix=".env.setup-", suffix=".tmp", dir=backend)
    temp_path = Path(temp_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(original)
        original_values = dict(dotenv_values(temp_path, interpolate=False))
        for name, value in values.items():
            if value is not None and (name not in original_values or original_values[name] != value):
                set_key(str(temp_path), name, value, quote_mode="always")
        # python-dotenv uses native newlines; keep Linux-ready output on all hosts.
        temp_path.write_bytes(temp_path.read_text(encoding="utf-8").encode("utf-8"))
        os.chmod(temp_path, 0o600)
        if env_path.exists():
            if env_path.read_bytes() == temp_path.read_bytes():
                os.chmod(env_path, 0o600)
                return values
            backup = backend / f".env.bak.{time.time_ns()}"
            # mkstemp-created source mode is already private; set backup mode before copying secrets.
            with backup.open("xb") as stream:
                os.chmod(backup, 0o600)
                stream.write(original_bytes)
            print(f"已备份原配置：{backup.name}（含密钥，请妥善保管）")
        os.replace(temp_path, env_path)
    finally:
        temp_path.unlink(missing_ok=True)
    return values


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend", required=True, type=Path)
    parser.add_argument("--non-interactive", action="store_true")
    parser.add_argument("--print-port", action="store_true")
    args = parser.parse_args()
    if args.print_port:
        print(dotenv_values(args.backend / ".env")["BOT_UNIFIED_PORT"])
        return
    try:
        configure(args.backend.resolve(), interactive=not args.non_interactive)
    except (ValueError, EOFError, OSError) as exc:
        parser.exit(1, f"配置失败：{exc}\n")
    print(".env 已就绪；敏感值未输出，数据目录保持原配置。")


if __name__ == "__main__":
    main()
