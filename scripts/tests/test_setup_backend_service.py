"""Run service argument parsing and ownership checks in Bash, without systemd writes."""
import os
from pathlib import Path
import shutil
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = (ROOT / "scripts/setup-backend.sh").read_text(encoding="utf-8")
BASH = shutil.which("bash") or (
    "C:/Program Files/Git/bin/bash.exe" if os.name == "nt" else None
)


@unittest.skipUnless(BASH and Path(BASH).exists(), "Bash is required")
class ServiceTests(unittest.TestCase):
    def run_bash(self, source, *args):
        return subprocess.run(
            [BASH, "-c", source, "test", *args],
            capture_output=True, text=True, encoding="utf-8",
        )

    def parse(self, *args):
        prefix = SCRIPT.split('[[ $(uname -s) == Linux ]]')[0]
        return self.run_bash(prefix + '\nprintf "%s" "$SERVICE_UNIT"\n', *args)

    def test_default_and_custom_names(self):
        for args, expected in [
            ((), "tks-backend.service"),
            (("--service-name", "tks-backend-dev"), "tks-backend-dev.service"),
            (("--service-name", "tks-backend-dev.service"), "tks-backend-dev.service"),
        ]:
            with self.subTest(args=args):
                result = self.parse(*args)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout, expected)

    def test_invalid_names_and_missing_argument(self):
        for name in ("../prod", "-prod", "a b", "a@b", "a%b", "a;b", "", "a" * 81):
            with self.subTest(name=name):
                self.assertNotEqual(self.parse("--service-name", name).returncode, 0)
        self.assertNotEqual(self.parse("--service-name").returncode, 0)

    def test_existing_service_ownership(self):
        # Execute the actual preflight; mock only privileged reads. No services are changed.
        block = SCRIPT.split('# Check ownership before')[1].split('as_user test -w')[0]
        block = block[block.index('UNIT_PATH='):]
        prelude = r'''
set -Eeuo pipefail
die() { echo "$*" >&2; exit 1; }
SERVICE_UNIT=tks-backend-dev.service
BACKEND=/srv/tks-dev/Taki_Shiina_Bot
as_root() {
    case "$1" in
        systemctl) printf '%s' "$FRAGMENT" ;;
        grep)
            case "$3" in
                '# Managed by TKS scripts/setup-backend.sh') [[ $MANAGED == yes ]] ;;
                "WorkingDirectory=$BACKEND") [[ $SAME_DIRECTORY == yes ]] ;;
                *) return 1 ;;
            esac ;;
        *) return 1 ;;
    esac
}
FRAGMENT=$1
MANAGED=$2
SAME_DIRECTORY=$3
'''
        for fragment, managed, same, allowed in [
            ("", "no", "no", True),
            ("/etc/systemd/system/tks-backend-dev.service", "yes", "yes", True),
            ("/etc/systemd/system/tks-backend-dev.service", "yes", "no", False),
            ("/etc/systemd/system/tks-backend-dev.service", "no", "yes", False),
            ("/usr/lib/systemd/system/tks-backend-dev.service", "yes", "yes", False),
            ("/dev/null", "yes", "yes", False),
        ]:
            with self.subTest(fragment=fragment, managed=managed, same=same):
                result = self.run_bash(prelude + block, fragment, managed, same)
                self.assertEqual(result.returncode == 0, allowed, result.stderr)


if __name__ == "__main__":
    unittest.main()
