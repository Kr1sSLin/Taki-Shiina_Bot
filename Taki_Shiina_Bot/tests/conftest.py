"""Use synthetic credentials and temporary stores before collecting any tests."""

import base64
import ipaddress
import os
import socket
import sys
from pathlib import Path
import tempfile
from unittest.mock import patch

for directory in (Path(__file__).resolve().parents[2], Path(__file__).resolve().parents[1], Path(__file__).resolve().parent):
    if str(directory) not in sys.path:
        sys.path.insert(0, str(directory))

_runtime_data = tempfile.TemporaryDirectory(prefix="tks-pytest-runtime-")
_environment = patch.dict(os.environ, {
    "BOT_DATA_DIR": _runtime_data.name,
    "DATA_AUTO_MIGRATE": "0",
    "DATA_BACKUP_ON_MIGRATE": "0",
    "MEMORY_COLLECTION_ENABLED": "1",
    "MEMORY_REINFORCEMENT_ENABLED": "1",
    "DATA_ENC_KEY": base64.urlsafe_b64encode(b"7" * 32).decode(),
    "AUTH_USERNAME": "test_user",
    "AUTH_USER_ID": "test_user",
    "AUTH_PASSWORD": "test_password",
    "AUTH_PASSWORD_HASH": "",
    "AUTH_PASSWORD_SALT": "",
    "AUTH_JWT_SECRET": "test-jwt-secret",
    "AUTH_MAX_DEVICES": "0",
    "BOT_DEVICE_TOKENS": "",
    "BOT_HTTP_TOKEN": "test-access-token",
    "BOT_WS_TOKEN": "test-access-token",
    "ADMIN_API_TOKEN": "test-admin-token",
    "DEEPSEEK_API_KEY": "test-key",
    "GEMINI_API_KEY": "",
    "GOOGLE_API_KEY": "",
    "FEISHU_WEBHOOK_URL": "",
    "DEEPSEEK_MODEL": "deepseek-v4-pro",
    "MY_LAT": "0",
    "MY_LON": "0",
    "QWEATHER_API_KEY": "",
    "QWEATHER_API_HOST": "",
    "BIZ_TZ_OFFSET_HOURS": "8",
})
_environment.start()
# Imported application modules must not load a developer's real .env file.
_dotenv = patch("dotenv.load_dotenv", return_value=False)
_dotenv.start()
_connect = socket.socket.connect
_connect_ex = socket.socket.connect_ex


def _offline_connect(sock, address):
    if sock.family in (socket.AF_INET, socket.AF_INET6) and not _is_loopback(address):
        raise RuntimeError("Unit tests must mock external network calls")
    return _connect(sock, address)


def _offline_connect_ex(sock, address):
    if sock.family in (socket.AF_INET, socket.AF_INET6) and not _is_loopback(address):
        raise RuntimeError("Unit tests must mock external network calls")
    return _connect_ex(sock, address)


def _is_loopback(address):
    try:
        return ipaddress.ip_address(address[0]).is_loopback
    except (TypeError, ValueError, IndexError):
        return False


_network = patch.object(socket.socket, "connect", _offline_connect)
_network_ex = patch.object(socket.socket, "connect_ex", _offline_connect_ex)
_network.start()
_network_ex.start()


def pytest_unconfigure(config):
    _network_ex.stop()
    _network.stop()
    _dotenv.stop()
    _environment.stop()
    _runtime_data.cleanup()
