"""Deployment regressions; all fixtures live in temporary directories."""
import base64
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("setup_backend_config", ROOT / "scripts/setup_backend_config.py")
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)
sys.path.insert(0, str(ROOT / "Taki_Shiina_Bot"))
from auth_utils import verify_password


class ConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.backend = Path(self.tmp.name)
        self.env = self.backend / ".env"
        (self.backend / ".env.example").write_bytes((ROOT / "Taki_Shiina_Bot/.env.example").read_bytes())
        self.env.write_text("# custom settings\nAUTH_USERNAME=operator\nAUTH_PASSWORD=secret'with#symbols\nDEEPSEEK_API_KEY=test-key\nCUSTOM_FLAG=keep-me\n", encoding="utf-8")

    def run_config(self):
        return setup.configure(self.backend, interactive=False)

    def encrypted_fixture(self, key):
        nonce = b"x" * 12
        payload = {"ver": 1, "alg": "AESGCM", "nonce": base64.urlsafe_b64encode(nonce).decode(),
                   "ciphertext": base64.urlsafe_b64encode(AESGCM(key).encrypt(nonce, b'{}', None)).decode()}
        path = self.backend / "chat_history.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def test_config_hashes_password_and_preserves_custom_values(self):
        values = self.run_config()
        self.assertTrue(verify_password("secret'with#symbols", values["AUTH_PASSWORD_SALT"], values["AUTH_PASSWORD_HASH"]))
        self.assertEqual(values["AUTH_PASSWORD"], "")
        self.assertEqual(values["CUSTOM_FLAG"], "keep-me")
        self.assertEqual(len(base64.urlsafe_b64decode(values["DATA_ENC_KEY"])), 32)
        self.assertEqual(values["GEMINI_API_KEY"], "")
        self.assertEqual(dotenv_values(self.env)["AUTH_PASSWORD_HASH"], values["AUTH_PASSWORD_HASH"])
        self.assertEqual(len(list(self.backend.glob(".env.bak.*"))), 1)

    def test_rerun_preserves_secrets_and_file_bytes(self):
        first = self.run_config()
        before = self.env.read_bytes()
        self.assertEqual(first, self.run_config())
        self.assertEqual(before, self.env.read_bytes())
        self.assertEqual(len(list(self.backend.glob(".env.bak.*"))), 1)

    def test_missing_key_with_encrypted_data_never_rotates(self):
        self.encrypted_fixture(b"k" * 32)
        before = self.env.read_bytes()
        with self.assertRaisesRegex(ValueError, "已有加密数据"):
            self.run_config()
        self.assertEqual(before, self.env.read_bytes())

    def test_wrong_key_refused_without_changing_data_or_config(self):
        values = self.run_config()
        data = self.encrypted_fixture(b"z" * 32)
        before = self.env.read_bytes(), data.read_bytes()
        with self.assertRaisesRegex(ValueError, "无法解密"):
            self.run_config()
        self.assertEqual(before, (self.env.read_bytes(), data.read_bytes()))
        data.unlink()
        self.encrypted_fixture(base64.urlsafe_b64decode(values["DATA_ENC_KEY"]))
        self.assertEqual(values, self.run_config())

    def test_noninteractive_missing_api_key_does_not_write(self):
        self.env.write_text("AUTH_USERNAME=operator\n", encoding="utf-8")
        before = self.env.read_bytes()
        with self.assertRaisesRegex(ValueError, "DEEPSEEK_API_KEY"):
            self.run_config()
        self.assertEqual(before, self.env.read_bytes())

    def test_first_install_prompts_and_hashes_password(self):
        self.env.unlink()
        with patch.object(setup, "ask", side_effect=["new-admin", "test-key", "new-password-123", "new-password-123"]):
            values = setup.configure(self.backend, interactive=True)
        self.assertTrue(verify_password("new-password-123", values["AUTH_PASSWORD_SALT"], values["AUTH_PASSWORD_HASH"]))
        self.assertFalse(list(self.backend.glob(".env.bak.*")))

    def test_invalid_port_and_partial_hash_leave_original_untouched(self):
        for extra in ("BOT_UNIFIED_PORT=80", "AUTH_PASSWORD_HASH=existing-hash", "BOT_UNIFIED_PORT=oops"):
            with self.subTest(extra=extra):
                self.env.write_text(f"AUTH_USERNAME=operator\nDEEPSEEK_API_KEY=test-key\nAUTH_PASSWORD=test-password\n{extra}\n", encoding="utf-8")
                before = self.env.read_bytes()
                with self.assertRaises(ValueError):
                    self.run_config()
                self.assertEqual(before, self.env.read_bytes())

    def test_shell_expressions_are_preserved_without_execution(self):
        with self.env.open("a", encoding="utf-8") as stream:
            stream.write("CUSTOM_SHELL='$(touch never-execute)'\n")
        values = self.run_config()
        self.assertEqual(values["CUSTOM_SHELL"], "$(touch never-execute)")
        self.assertFalse((self.backend / "never-execute").exists())


if __name__ == "__main__":
    unittest.main()
