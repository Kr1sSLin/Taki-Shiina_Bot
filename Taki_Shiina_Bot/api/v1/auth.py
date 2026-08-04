from __future__ import annotations

import os
import uuid

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from ...auth_utils import create_access_token, parse_device_tokens, verify_password
from ...services.auth_store import DeviceLimitError, InvalidTokenError, RefreshTokenStore

router = APIRouter()

_base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

AUTH_USERNAME = (os.getenv("AUTH_USERNAME", "") or "").strip()
AUTH_USER_ID = (os.getenv("AUTH_USER_ID", "") or "").strip()
AUTH_PASSWORD_HASH = (os.getenv("AUTH_PASSWORD_HASH", "") or "").strip()
AUTH_PASSWORD_SALT = (os.getenv("AUTH_PASSWORD_SALT", "") or "").strip()
AUTH_PASSWORD = (os.getenv("AUTH_PASSWORD", "") or "").strip()
AUTH_JWT_SECRET = (os.getenv("AUTH_JWT_SECRET", "") or "").strip()
AUTH_ACCESS_TTL_MINUTES = int(os.getenv("AUTH_ACCESS_TTL_MINUTES", "15"))
AUTH_REFRESH_TTL_DAYS = int(os.getenv("AUTH_REFRESH_TTL_DAYS", "30"))
AUTH_MAX_DEVICES = int(os.getenv("AUTH_MAX_DEVICES", "0"))

APP_USER_ID = os.getenv("APP_USER_ID", "default-user").strip() or "default-user"
DEFAULT_USER_ID = AUTH_USER_ID or AUTH_USERNAME or APP_USER_ID

BOT_DEVICE_TOKENS = (os.getenv("BOT_DEVICE_TOKENS", "") or "").strip()
DEVICE_TOKEN_ERROR: str | None = None
DEVICE_TOKEN_MAP, DEVICE_ALLOWLIST = {}, {}
try:
    DEVICE_TOKEN_MAP, DEVICE_ALLOWLIST = parse_device_tokens(
        BOT_DEVICE_TOKENS, DEFAULT_USER_ID, max_devices=AUTH_MAX_DEVICES
    )
    if BOT_DEVICE_TOKENS and not DEVICE_ALLOWLIST:
        raise ValueError("BOT_DEVICE_TOKENS 为空")
except ValueError as exc:
    DEVICE_TOKEN_ERROR = str(exc)

CONFIG_ERRORS: list[str] = []
if not AUTH_JWT_SECRET:
    CONFIG_ERRORS.append("AUTH_JWT_SECRET")
if not AUTH_USERNAME:
    CONFIG_ERRORS.append("AUTH_USERNAME")
if not ((AUTH_PASSWORD_HASH and AUTH_PASSWORD_SALT) or AUTH_PASSWORD):
    CONFIG_ERRORS.append("AUTH_PASSWORD_HASH/AUTH_PASSWORD_SALT 或 AUTH_PASSWORD")
if DEVICE_TOKEN_ERROR:
    CONFIG_ERRORS.append(f"BOT_DEVICE_TOKENS({DEVICE_TOKEN_ERROR})")
if CONFIG_ERRORS:
    joined = ", ".join(CONFIG_ERRORS)
    raise RuntimeError(f"Auth config invalid: {joined}")

refresh_store = RefreshTokenStore(
    path=os.path.join(_base_dir, "refresh_tokens.json"),
    max_devices=AUTH_MAX_DEVICES,
)


class LoginReq(BaseModel):
    username: str
    password: str
    deviceId: str | None = None


class RefreshReq(BaseModel):
    refreshToken: str


def _check_password(password: str) -> bool:
    if AUTH_PASSWORD_HASH and AUTH_PASSWORD_SALT:
        return verify_password(password, AUTH_PASSWORD_SALT, AUTH_PASSWORD_HASH)
    return password == AUTH_PASSWORD


@router.post("/auth/login")
def login(body: LoginReq):
    username = (body.username or "").strip()
    password = body.password or ""
    if not username or not password:
        raise HTTPException(status_code=400, detail={"code": 40001, "message": "username/password required"})
    if username != AUTH_USERNAME or not _check_password(password):
        raise HTTPException(status_code=401, detail={"code": 40101, "message": "invalid credentials"})

    device_id = (body.deviceId or "").strip() or f"device_{uuid.uuid4().hex}"
    if DEVICE_ALLOWLIST and device_id not in DEVICE_ALLOWLIST:
        raise HTTPException(status_code=403, detail={"code": 40301, "message": "device not allowed"})

    try:
        refresh_token = refresh_store.issue(
            user_id=DEFAULT_USER_ID,
            device_id=device_id,
            ttl_seconds=AUTH_REFRESH_TTL_DAYS * 24 * 3600,
        )
    except DeviceLimitError:
        raise HTTPException(status_code=403, detail={"code": 40302, "message": "device limit reached"})

    access_token = create_access_token(
        user_id=DEFAULT_USER_ID,
        device_id=device_id,
        secret=AUTH_JWT_SECRET,
        expires_in_seconds=AUTH_ACCESS_TTL_MINUTES * 60,
    )

    return {
        "accessToken": access_token,
        "refreshToken": refresh_token,
        "tokenType": "Bearer",
        "expiresIn": AUTH_ACCESS_TTL_MINUTES * 60,
        "userId": DEFAULT_USER_ID,
        "deviceId": device_id,
    }


@router.post("/auth/refresh")
def refresh(body: RefreshReq):
    refresh_token = (body.refreshToken or "").strip()
    if not refresh_token:
        raise HTTPException(status_code=400, detail={"code": 40002, "message": "refreshToken required"})

    try:
        user_id, device_id, new_refresh = refresh_store.rotate(
            refresh_token=refresh_token,
            ttl_seconds=AUTH_REFRESH_TTL_DAYS * 24 * 3600,
        )
    except InvalidTokenError:
        raise HTTPException(status_code=401, detail={"code": 40102, "message": "refresh token invalid"})

    if DEVICE_ALLOWLIST and device_id not in DEVICE_ALLOWLIST:
        raise HTTPException(status_code=403, detail={"code": 40301, "message": "device not allowed"})

    access_token = create_access_token(
        user_id=user_id,
        device_id=device_id,
        secret=AUTH_JWT_SECRET,
        expires_in_seconds=AUTH_ACCESS_TTL_MINUTES * 60,
    )

    return {
        "accessToken": access_token,
        "refreshToken": new_refresh,
        "tokenType": "Bearer",
        "expiresIn": AUTH_ACCESS_TTL_MINUTES * 60,
        "userId": user_id,
        "deviceId": device_id,
    }
