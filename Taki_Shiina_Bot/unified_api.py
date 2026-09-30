"""Recommended single-process entry point for all HTTP and WebSocket routes."""

from __future__ import annotations

import os
import sys
import uuid

from dotenv import load_dotenv
from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware

_base_dir = os.path.dirname(os.path.abspath(__file__))
if _base_dir not in sys.path:
    sys.path.insert(0, _base_dir)
load_dotenv(os.path.join(_base_dir, ".env"))

# Import the legacy applications once. Their route functions keep using the same
# module-global services, so unified mode does not duplicate business logic/state.
try:  # project-directory execution (python unified_api.py)
    import http_api
    import ws_api
except ImportError:  # package execution (uvicorn Taki_Shiina_Bot.unified_api:app)
    from Taki_Shiina_Bot import http_api, ws_api  # type: ignore


def configure_unified_runtime() -> None:
    """Make ws_api's runtime the sole owner of mutable chat state and stores."""
    http_api.configure_unified_runtime(ws_api)


configure_unified_runtime()

try:
    from Taki_Shiina_Bot.api.v1.auth import router as auth_router
    from Taki_Shiina_Bot.core.config import settings
except ImportError:  # pragma: no cover - project directory on sys.path only
    from api.v1.auth import router as auth_router
    from core.config import settings

app = FastAPI(title="TakiShiina Bot Unified API")
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.CORS_ORIGINS,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.middleware("http")
async def trace_middleware(request: Request, call_next):
    """Preserve http_api's trace contract for every unified HTTP route."""
    trace_id = request.headers.get("x-trace-id") or f"trace_{uuid.uuid4().hex}"
    request.state.trace_id = trace_id
    response = await call_next(request)
    response.headers["x-trace-id"] = trace_id
    return response
app.include_router(ws_api.interaction_router, prefix="/api/v1", tags=["interaction"])
app.include_router(ws_api.points_router, prefix="/api/v1", tags=["points"])
app.include_router(ws_api.level_router, prefix="/api/v1", tags=["level"])
app.include_router(ws_api.admin_router, prefix="/api/v1", tags=["admin"])


# Auth routes are not present in either legacy app.
app.include_router(auth_router, prefix=settings.API_PREFIX, tags=["auth"])


def _mount_unique(routes, *, include) -> None:
    existing = {
        (getattr(route, "path", None), tuple(sorted(getattr(route, "methods", ()) or ())))
        for route in app.routes
    }
    for route in routes:
        path = getattr(route, "path", "")
        if not include(path, route):
            continue
        key = (path, tuple(sorted(getattr(route, "methods", ()) or ())))
        if key in existing:
            raise RuntimeError(f"duplicate unified route: {key}")
        app.router.routes.append(route)
        existing.add(key)


_mount_unique(
    http_api.app.routes,
    include=lambda path, _route: path in {
        "/api/v1/chat/history",
        "/api/v1/memory/facts",
        "/api/v1/settings/city",
        "/api/v1/chat",
    },
)
_mount_unique(
    ws_api.app.routes,
    include=lambda path, _route: path in {"/ws/chat", "/internal/scene/refresh"},
)

# Mount ws_api lifecycle hooks exactly once. This retains security validation and
# starts its four scheduler families once; http_api has no background schedulers.
app.router.add_event_handler("startup", ws_api.validate_security_config)
app.router.add_event_handler("startup", ws_api.on_startup)


@app.get("/healthz")
async def healthz(request: Request):
    return {
        "status": "ok",
        "service": "unified_api",
        "mode": "unified",
        "subservices": {
            "auth": "up",
            "http_api": "up",
            "ws_api": "up",
            "gamification": "up",
        },
        "traceId": request.state.trace_id,
    }


if __name__ == "__main__":
    import uvicorn

    host = os.getenv("BOT_UNIFIED_HOST", "127.0.0.1")
    port = int(os.getenv("BOT_UNIFIED_PORT", "8000"))
    uvicorn.run("unified_api:app", host=host, port=port, reload=False)
