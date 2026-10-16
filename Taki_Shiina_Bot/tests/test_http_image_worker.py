"""A standalone HTTP worker must be able to finish a WebSocket image turn."""
import asyncio
import base64
import hashlib
from io import BytesIO
import os
import pathlib
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from PIL import Image

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from ws_api_import_fixture import ensure_ws_api_env

ensure_ws_api_env()
import http_api
from services.chat_runtime import ChatRuntime
from services.contracts import ChatCommand
from services.domain_cipher import DomainCipher
from services.reliable_chat_store import ReliableChatStore
from services import vision_generation


class PublicService:
    def __init__(self, *_):
        self.legacy_allowed = True

    async def system_context(self, *_args, **_kwargs):
        return None, "public prompt"

    def history_context(self, history, **_kwargs):
        return history, []

    def messages(self, system, history, anchor, current):
        return [{"role": "system", "content": system + anchor}, *history, current]


class StandaloneHttpImageWorkerTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tks-http-image-worker-")
        self.store = ReliableChatStore(str(pathlib.Path(self.temp.name) / "foundation.sqlite3"), DomainCipher(b"v" * 32))
        self.runtime = None
        self.vision_parts = []
        self.text_calls = []

    async def asyncTearDown(self):
        if self.runtime:
            await self.runtime.stop()
        self.temp.cleanup()

    async def test_standalone_http_worker_completes_ws_image_turn_with_original_exif(self):
        image = Image.new("RGB", (2, 2), "blue")
        exif = Image.Exif()
        exif[270] = "GPS:31.2304,121.4737"
        output = BytesIO()
        image.save(output, format="JPEG", exif=exif)
        original = output.getvalue()

        class FakeVisionModel:
            def __init__(inner, model_name):
                self.assertEqual(model_name, "gemini-2.5-flash")

            def generate_content(inner, parts):
                self.vision_parts.append(parts)
                return SimpleNamespace(text="画面显示 GPS 31.2304,121.4737 和蓝色方块")

        async def fake_text_model(**kwargs):
            self.text_calls.append(kwargs["messages"])
            content = "[]" if kwargs.get("temperature") == 0.3 else "看到了蓝色方块。"
            return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=content))], usage=None)

        def independent_runtime(_data_dir, generator, extractor, deliver):
            self.runtime = ChatRuntime(self.store, generator, extractor, deliver, poll_interval=0.005)
            return self.runtime

        fake_client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=fake_text_model)))
        with patch.dict(os.environ, {"GEMINI_API_KEY": "synthetic-vision-key", "GEMINI_MODEL": "gemini-2.5-flash"}), \
             patch.object(vision_generation.genai, "configure") as configure, \
             patch.object(vision_generation.genai, "GenerativeModel", FakeVisionModel), \
             patch.object(http_api, "_reliable_runtime_provider", None), \
             patch.object(http_api, "get_chat_runtime", side_effect=independent_runtime), \
             patch.object(http_api, "client", fake_client), \
             patch("services.text_generation.ChatApplicationService", PublicService):
            runtime = http_api.get_reliable_runtime()
            image_source = {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(original).decode("ascii"),
                            "sha256": hashlib.sha256(original).hexdigest()}
            command = ChatCommand("synthetic-owner", "ws-image-1", "ws-image-1", "ws-device", "[图片]", channel="ws", metadata={"images": [image_source]})
            result = (await asyncio.wait_for(runtime.submit([command], timeout=3), timeout=4))[0]

        configure.assert_called_once_with(api_key="synthetic-vision-key", transport="rest")
        self.assertEqual(result["reply"], "看到了蓝色方块。")
        self.assertEqual(self.store.turn_result("synthetic-owner", self.store.request("synthetic-owner", "ws-image-1")["turn_id"])["status"], "completed")
        self.assertEqual(self.vision_parts[0][1], {"mime_type": "image/jpeg", "data": original})
        with Image.open(BytesIO(self.vision_parts[0][1]["data"])) as saved_image:
            self.assertEqual(saved_image.getexif()[270], "GPS:31.2304,121.4737")
        self.assertIn("蓝色方块", str(self.text_calls[0]))
        self.assertNotIn("31.2304", str(self.text_calls[0]))


if __name__ == "__main__":
    unittest.main()
