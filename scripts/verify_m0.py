"""Run the local M0 gate with the current Python environment; never deploy."""

from __future__ import annotations

import ast
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
BACKEND = ROOT / "Taki_Shiina_Bot"


def main() -> int:
    required = (
        BACKEND / "tests/test_m0_contract.py",
        BACKEND / "tests/m0_contract_fixture.py",
        BACKEND / "tests/fixtures/m0/v1-contract.json",
        BACKEND / "tests/fixtures/m0/legacy-data.json",
    )
    missing = [str(path.relative_to(ROOT)) for path in required if not path.is_file()]
    if missing:
        print("Missing ignored M0 assets; restore them from the M0 delivery archive: " + ", ".join(missing), file=sys.stderr)
        return 1
    paths = subprocess.check_output(
        ["git", "ls-files", "-z"], cwd=ROOT
    ).decode().split("\0")
    python_paths = {ROOT / name for name in paths if name.endswith(".py")}
    python_paths.update((BACKEND / "tests").rglob("*.py"))
    python_paths.update((ROOT / "scripts").glob("*.py"))
    errors = []
    for path in sorted(python_paths):
        try:
            ast.parse(path.read_bytes(), filename=str(path.relative_to(ROOT)))
        except (SyntaxError, OSError) as exc:
            errors.append(str(exc))
    for name in paths:
        if name.endswith(".sh") and shutil.which("bash"):
            result = subprocess.run(["bash", "-n", str(ROOT / name)], capture_output=True, text=True)
            if result.returncode:
                errors.append(result.stderr)
        if name.endswith(".mjs") and shutil.which("node"):
            result = subprocess.run(["node", "--check", str(ROOT / name)], capture_output=True, text=True)
            if result.returncode:
                errors.append(result.stderr)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"Syntax PASS: {len(python_paths)} Python files; tracked shell/Node scripts checked where tools exist.", flush=True)
    print("PowerShell syntax/runtime gate requires Windows: TKS_Bot_Windows/scripts/verify.ps1", flush=True)
    for command, directory in (
        ([sys.executable, "-m", "pip", "check"], ROOT),
        ([sys.executable, "-m", "pytest", "tests/", "-q"], BACKEND),
        ([sys.executable, "-m", "pytest", "scripts/tests/", "-q"], ROOT),
    ):
        print(f"Running {' '.join(command)} (cwd={directory})", flush=True)
        result = subprocess.run(command, cwd=directory)
        if result.returncode:
            return result.returncode
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
