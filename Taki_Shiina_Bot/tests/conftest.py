"""Isolate import-time runtime stores before pytest imports any test modules."""

import os
import tempfile

_runtime_data = tempfile.TemporaryDirectory(prefix="tks-pytest-runtime-")
_previous_data_dir = os.environ.get("BOT_DATA_DIR")
os.environ["BOT_DATA_DIR"] = _runtime_data.name
os.environ["DATA_AUTO_MIGRATE"] = "0"
os.environ["DATA_BACKUP_ON_MIGRATE"] = "0"


def pytest_unconfigure(config):
    if _previous_data_dir is None:
        os.environ.pop("BOT_DATA_DIR", None)
    else:
        os.environ["BOT_DATA_DIR"] = _previous_data_dir
    _runtime_data.cleanup()
