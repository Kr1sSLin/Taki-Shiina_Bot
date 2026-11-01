import importlib.util
import pathlib
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "privacy_check.py"
spec = importlib.util.spec_from_file_location("privacy_check", SCRIPT)
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class PrivacyCheckTests(unittest.TestCase):
    def test_private_paths_and_examples(self):
        for path in [".env", "app/.env.production", "app/x.json.bak.123.enc",
                     ".claude/settings.local.json", "app/keystore.properties",
                     "TKS_Bot_Linux/.e2e/data/tks.db", "app/credentials.enc"]:
            with self.subTest(path=path):
                self.assertTrue(checker.private_path(path))
        for path in ["app/.env.example", "app/.env.production.example", "src/auth.py"]:
            self.assertFalse(checker.private_path(path))

    def test_sensitive_signatures_without_exposing_values(self):
        marker = b"-----BEGIN " + b"PRIVATE KEY-----"
        self.assertIn(("private-key", 2), checker.content_findings(b"header\n" + marker))
        token = b"ghp_" + b"a1" * 20
        self.assertIn(("provider-key", 1), checker.content_findings(token))

    def test_checks_index_even_when_working_copy_has_been_cleaned(self):
        with tempfile.TemporaryDirectory() as folder:
            def git(*args):
                subprocess.run(["git", *args], cwd=folder, check=True, capture_output=True)
            git("init", "-q")
            path = pathlib.Path(folder) / "config.txt"
            secret = "ghp_" + "a1" * 20
            path.write_text(secret)
            git("add", "config.txt")
            path.write_text("clean unstaged text")
            result = subprocess.run([sys.executable, str(SCRIPT), "--staged"], cwd=folder,
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 1)
            self.assertIn("provider-key", result.stdout)
            self.assertNotIn(secret, result.stdout)
            git("add", "config.txt")
            result = subprocess.run([sys.executable, str(SCRIPT), "--staged"], cwd=folder,
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
