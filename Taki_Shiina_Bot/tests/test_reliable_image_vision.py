"""The reliable image path preserves source bytes and fences both model calls."""
import base64
import hashlib
from io import BytesIO
import pathlib
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from PIL import Image

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from services.text_generation import TextGenerator


def jpeg_with_exif():
    image = Image.new("RGB", (2, 2), "blue")
    exif = Image.Exif()
    exif[270] = "GPS:31.2304,121.4737"  # ImageDescription in a valid EXIF segment.
    output = BytesIO()
    image.save(output, format="JPEG", exif=exif)
    return output.getvalue()


RAW_JPEG = jpeg_with_exif()


class FakeService:
    def __init__(self, *_):
        self.legacy_allowed = True

    async def system_context(self, *_args, **_kwargs):
        return None, "public-system"

    def history_context(self, history, **_kwargs):
        return history, []

    def messages(self, system, history, anchor, current):
        return [{"role": "system", "content": system + anchor}, *history, current]


class FakeStore:
    def __init__(self, *, stale=False):
        self.checked = 0
        self.stale = stale

    def history(self, *_args, **_kwargs):
        return []

    def prepare_memories(self, *_args):
        return []

    def assert_snapshot(self, _snapshot):
        self.checked += 1
        if self.stale:
            raise RuntimeError("synthetic stale generation")


class ReliableImageVisionTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.calls = []
        self.received = []
        async def create(**kwargs):
            self.calls.append(kwargs["messages"])
            return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content="看到了。"))], usage=None)
        self.client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=create)))
        self.state = SimpleNamespace(user_chat_history={})
        self.service_patch = patch("services.text_generation.ChatApplicationService", FakeService)
        self.service_patch.start()
        self.addCleanup(self.service_patch.stop)

    def generator(self, provider=None):
        return TextGenerator(self.state, None, None, None, self.client, lambda raw: (raw, None, None), vision_provider=provider)

    @staticmethod
    def snapshot(*, images=None, text="看一下这张图片"):
        metadata = {"images": images} if images is not None else None
        return {"owner": "synthetic-owner", "legacy_allowed": False,
                "sources": [{"channel": "ws", "text": text, "metadata": metadata}]}

    @staticmethod
    def encoded(raw=RAW_JPEG, **changes):
        return {"mimeType": "image/jpeg", "dataBase64": base64.b64encode(raw).decode("ascii"),
                "sha256": hashlib.sha256(raw).hexdigest(), **changes}

    async def test_original_exif_bytes_reach_vision_and_derived_coordinates_are_scrubbed(self):
        async def vision(images, caption):
            self.received.append((images, caption))
            return "图片显示 GPS 31.2304,121.4737 和一只猫"

        store = FakeStore()
        result = await self.generator(vision)(self.snapshot(images=[self.encoded()]), store)
        self.assertTrue(result["reply"])
        self.assertEqual(self.received[0][0], [{"mimeType": "image/jpeg", "raw": RAW_JPEG}])
        self.assertIn(b"Exif", self.received[0][0][0]["raw"])
        with Image.open(BytesIO(self.received[0][0][0]["raw"])) as image:
            self.assertEqual(image.getexif()[270], "GPS:31.2304,121.4737")
        self.assertEqual(self.received[0][1], "看一下这张图片")
        self.assertEqual(store.checked, 2)  # vision, then text model
        prompt = str(self.calls[0])
        self.assertIn("一只猫", prompt)
        self.assertNotIn("31.2304", prompt)
        self.assertNotIn("121.4737", prompt)

    async def test_text_only_path_never_invokes_vision(self):
        async def vision(_images, _caption):
            self.fail("text-only chat called vision")

        store = FakeStore()
        await self.generator(vision)(self.snapshot(), store)
        self.assertEqual(store.checked, 1)
        self.assertIn("看一下这张图片", str(self.calls[0]))
        self.assertNotIn("图片内容：", str(self.calls[0]))

    async def test_missing_vision_provider_fails_before_text_model(self):
        with self.assertRaisesRegex(RuntimeError, "vision model is not configured"):
            await self.generator()(self.snapshot(images=[self.encoded()]), FakeStore())
        self.assertEqual(self.calls, [])

    async def test_corrupt_image_fails_before_any_model(self):
        async def vision(_images, _caption):
            self.fail("bad source reached vision")

        for image in (self.encoded(sha256="0" * 64), self.encoded(dataBase64="not base64!")):
            with self.subTest(image=image["dataBase64"][:8]), self.assertRaises(ValueError):
                await self.generator(vision)(self.snapshot(images=[image]), FakeStore())
        self.assertEqual(self.calls, [])

    async def test_images_merged_from_sources_obey_one_turn_limit(self):
        async def vision(_images, _caption):
            self.fail("over-limit turn reached vision")

        snapshot = self.snapshot(images=[self.encoded(), self.encoded()])
        snapshot["sources"].append({"channel": "ws", "text": "第二条", "metadata": {"images": [self.encoded(), self.encoded()]}})
        with self.assertRaisesRegex(ValueError, "too many images"):
            await self.generator(vision)(snapshot, FakeStore())
        self.assertEqual(self.calls, [])

    async def test_vision_failure_cannot_call_text_model(self):
        async def vision(_images, _caption):
            raise TimeoutError("synthetic vision timeout")

        with self.assertRaises(TimeoutError):
            await self.generator(vision)(self.snapshot(images=[self.encoded()]), FakeStore())
        self.assertEqual(self.calls, [])

    async def test_stale_turn_is_fenced_before_vision(self):
        async def vision(_images, _caption):
            self.fail("stale turn reached vision")

        with self.assertRaisesRegex(RuntimeError, "stale generation"):
            await self.generator(vision)(self.snapshot(images=[self.encoded()]), FakeStore(stale=True))
        self.assertEqual(self.calls, [])


if __name__ == "__main__":
    unittest.main()
