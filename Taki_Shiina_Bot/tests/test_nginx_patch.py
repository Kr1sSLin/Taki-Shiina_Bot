"""Generated admin locations stay private, including existing snippet upgrades."""

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scripts import patch_nginx_gamification as nginx


class NginxPatchTest(unittest.TestCase):
    def test_missing_nginx_fails_validation(self):
        with patch.object(nginx.subprocess, "run", side_effect=FileNotFoundError):
            self.assertFalse(nginx.check_nginx("missing-nginx"))

    def test_http_include_does_not_skip_tls_server(self):
        with tempfile.TemporaryDirectory() as directory:
            snippet = Path(directory) / "snippet.conf"
            config = Path(directory) / "site.conf"
            config.write_text(
                "server {\n listen 80;\n server_name example.test;\n include " + str(snippet) + ";\n}\n"
                "server {\n listen 443 ssl;\n server_name example.test;\n}\n", encoding="utf-8")
            argv = ["patch", "--domain", "example.test", "--conf", str(config), "--snippet-path", str(snippet)]
            with patch("sys.argv", argv), patch.object(nginx, "check_nginx", return_value=True), patch.object(nginx, "reload_nginx"):
                self.assertEqual(nginx.main(), 0)
            lines = config.read_text(encoding="utf-8").splitlines()
            begin, end = nginx.pick_server_block(lines, nginx.server_blocks(lines), "example.test")
            self.assertIn("include " + str(snippet), "\n".join(lines[begin:end + 1]))

    def test_admin_is_denied_not_proxied(self):
        admin = next(line for line in nginx.snippet_content(8001).splitlines() if "/admin/" in line)
        self.assertIn("location ^~ /api/v1/admin/", admin)
        self.assertIn("deny all", admin)
        self.assertNotIn("proxy_pass", admin)

    def test_existing_snippet_is_updated(self):
        self._existing_snippet(check_ok=True)

    def test_failed_validation_restores_existing_snippet(self):
        self._existing_snippet(check_ok=False)

    def _existing_snippet(self, check_ok):
        with tempfile.TemporaryDirectory() as directory:
            snippet = Path(directory) / "snippet.conf"
            config = Path(directory) / "site.conf"
            original_snippet = "location /api/v1/admin/ { proxy_pass http://127.0.0.1:8001; }\n"
            original_config = "server {\n listen 443 ssl;\n server_name example.test;\n include " + str(snippet) + ";\n}\n"
            snippet.write_text(original_snippet, encoding="utf-8")
            config.write_text(original_config, encoding="utf-8")
            argv = ["patch", "--domain", "example.test", "--conf", str(config), "--snippet-path", str(snippet)]
            with patch("sys.argv", argv), patch.object(nginx, "check_nginx", return_value=check_ok), patch.object(nginx, "reload_nginx") as reload:
                self.assertEqual(nginx.main(), 0 if check_ok else 1)
                self.assertEqual(reload.call_count, 1 if check_ok else 0)
            self.assertEqual(config.read_text(encoding="utf-8"), original_config)
            self.assertEqual(snippet.read_text(encoding="utf-8"), nginx.snippet_content(8001) if check_ok else original_snippet)
