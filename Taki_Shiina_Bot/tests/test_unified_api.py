"""Unified API composition tests; no lifespan means schedulers never run."""

from __future__ import annotations

import os
import sys
import unittest
import tempfile
from unittest.mock import patch
from collections import Counter

from ws_api_import_fixture import ensure_ws_api_env

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
REPO_ROOT = os.path.dirname(PACKAGE_ROOT)
for path in (PACKAGE_ROOT, REPO_ROOT):
    if path not in sys.path:
        sys.path.insert(0, path)

ensure_ws_api_env()
os.environ["AUTH_JWT_SECRET"] = "test-jwt-secret"
os.environ["AUTH_USERNAME"] = "test_user"
os.environ["AUTH_PASSWORD"] = "test_password"
os.environ["BOT_WS_TOKEN"] = "test-access-token"
os.environ["ADMIN_API_TOKEN"] = "test-admin-token"
import unified_api  # noqa: E402
from fastapi.routing import APIWebSocketRoute  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402


class UnifiedApiTestCase(unittest.TestCase):
    def test_key_routes_appear_exactly_once(self):
        paths = Counter()
        route_keys = Counter()
        for route in unified_api.app.routes:
            if hasattr(route, "path"):
                paths[route.path] += 1
                route_keys[(route.path, tuple(sorted(getattr(route, "methods", ()) or ())))] += 1
                continue
            context = getattr(route, "include_context", None)
            original = getattr(route, "original_router", None)
            if context is not None and original is not None:
                for nested in original.routes:
                    if hasattr(nested, "path"):
                        full_path = f"{context.prefix}{nested.path}"
                        paths[full_path] += 1
                        methods = tuple(sorted(getattr(nested, "methods", ()) or ()))
                        route_keys[(full_path, methods)] += 1
        expected = (
            "/api/v1/auth/login",
            "/api/v1/chat/history",
            "/api/v1/memory/facts",
            "/api/v1/settings/city",
            "/api/v1/chat",
            "/ws/chat",
            "/api/v1/interaction/items",
            "/api/v1/points/balance",
            "/api/v1/level/status",
            "/api/v1/admin/gamification-config",
            "/internal/scene/refresh",
            "/healthz",
        )
        for path in expected:
            self.assertGreaterEqual(paths[path], 1, path)
        for key, count in route_keys.items():
            self.assertEqual(count, 1, key)
        ws_routes = [route for route in unified_api.app.routes if isinstance(route, APIWebSocketRoute)]
        self.assertEqual([route.path for route in ws_routes], ["/ws/chat"])

    def test_http_routes_share_ws_runtime_objects(self):
        shared = (
            "state",
            "db",
            "weather_service",
            "history_store",
            "client",
            "prompt_service",
            "timeline_store",
            "memory_timeline_store",
        )
        for name in shared:
            with self.subTest(name=name):
                self.assertIs(getattr(unified_api.http_api, name), getattr(unified_api.ws_api, name))

    def test_interleaved_http_ws_history_saves_do_not_overwrite(self):
        from services.history_store import HistoryStore

        history = unified_api.ws_api.state.user_chat_history
        original = dict(history)
        try:
            with tempfile.TemporaryDirectory() as directory:
                store = HistoryStore(os.path.join(directory, "history.json"))
                with patch.object(unified_api.ws_api, "history_store", store), patch.object(unified_api.http_api, "history_store", store):
                    history.clear()
                    history["ws-user"] = [{"role": "user", "content": "ws"}]
                    unified_api.ws_api.history_store.save(history)
                    history["http-user"] = [{"role": "user", "content": "http"}]
                    unified_api.http_api.history_store.save(unified_api.http_api.state.user_chat_history)
                    loaded = unified_api.ws_api.history_store.load()
                    self.assertEqual(set(loaded), {"ws-user", "http-user"})
        finally:
            history.clear()
            history.update(original)

    def test_startup_handlers_registered_once(self):
        handlers = unified_api.app.router.on_startup
        self.assertEqual(handlers.count(unified_api.ws_api.validate_security_config), 1)
        self.assertEqual(handlers.count(unified_api.ws_api.on_startup), 1)
        self.assertEqual(len(handlers), 2)

    def test_health_and_http_trace_contract(self):
        # Instantiating without a context manager does not run lifespan/start schedulers.
        client = TestClient(unified_api.app)
        health = client.get("/healthz", headers={"x-trace-id": "trace-test"})
        self.assertEqual(health.status_code, 200)
        self.assertEqual(health.headers["x-trace-id"], "trace-test")
        self.assertEqual(health.json()["mode"], "unified")
        self.assertEqual(health.json()["subservices"]["ws_api"], "up")

        unauthorized = client.get("/api/v1/chat/history")
        self.assertEqual(unauthorized.status_code, 401)
        self.assertIn("x-trace-id", unauthorized.headers)


if __name__ == "__main__":
    unittest.main()
