"""FND-10 synthetic coordinate, metadata and log exit probes; no network."""
from io import BytesIO, StringIO
import json
import logging
import pathlib
import sys
import unittest
from unittest.mock import patch
from urllib.parse import quote
import warnings

from PIL import Image, ImageOps, PngImagePlugin

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from services.privacy import (
    PrivacyBoundaryError, REMOVED_LOCATION, redact_locations,
    scrub_locations, strip_image_metadata,
)
from services.privacy_logging import PrivacyLogFilter, protect_log_handler


class LocationBoundaryTests(unittest.TestCase):
    def assert_removed(self, text, *probes):
        cleaned = redact_locations(text)
        self.assertIn(REMOVED_LOCATION, cleaned)
        for probe in probes:
            self.assertNotIn(probe, cleaned)
        self.assertEqual(redact_locations(cleaned), cleaned)

    def test_decimal_pairs_include_chinese_adjacent_text_and_scientific_notation(self):
        for text in (
            "合成位置12.345678,98.765432附近", "98.765432 / 12.345678",
            "12.345678 98.765432", "-12.345678，+98.765432",
            "1.2345678e1;9.8765432e1", "12,98.765432",
            "lat=12.345678,98.765432", r"12.345678\n98.765432",
        ):
            with self.subTest(text=text):
                self.assert_removed(text, "12.345678", "98.765432", "1.2345678e1")

    def test_structured_fields_recurse_without_removing_city(self):
        payload = {
            "city": "合成城市", "nested": [{" LAT ": 12.345678}, {"GPS_longitude": 98.765432}],
            "GPS": {"opaque": "synthetic_location"}, "coordinates": [98.765432, 12.345678],
            r"\u006c\u0061\u0074": 12.345678, "other": ("safe", 1),
        }
        cleaned = scrub_locations(payload)
        dumped = json.dumps(cleaned)
        for probe in ("12.345678", "98.765432", "synthetic_location"):
            self.assertNotIn(probe, dumped)
        self.assertEqual(cleaned["city"], "合成城市")
        self.assertEqual(cleaned["other"], ["safe", 1])
        self.assertEqual(payload["nested"][0][" LAT "], 12.345678)

    def test_structured_json_retains_valid_sanitised_shape(self):
        payload = '{"user":"synthetic", "lat":12.345678, "nested":{"lon":98.765432}}'
        cleaned = json.loads(redact_locations(payload))
        self.assertEqual(cleaned["user"], "synthetic")
        self.assertEqual(cleaned["lat"], REMOVED_LOCATION)
        self.assertEqual(cleaned["nested"]["lon"], REMOVED_LOCATION)

    def test_labels_and_embedded_json_are_removed(self):
        for text in (
            "纬度：12.345678 经度：98.765432", "latitude=12.345678 longitude=98.765432",
            'debug {"lat":12.345678,"lng":98.765432} end',
            'GPS: "opaque-synthetic-location"', "coordinates=[98.765432,12.345678]",
            "我的纬度：12.345678", "longitude 98.765432", "GPS_latitude=12.345678",
            "北纬12.345678 东经98.765432", "latitude12.345678 longitude98.765432",
        ):
            with self.subTest(text=text):
                self.assert_removed(text, "12.345678", "98.765432", "opaque-synthetic-location")

    def test_dms_and_decimal_cardinals_are_removed(self):
        for text in (
            '位置12°20\'44.44"N, 98°45\'55.55"E', "12°20′44.44″N 98°45′55.55″E",
            "12度20分44.44秒 98度45分55.55秒", "N12.345678 E98.765432",
            "12.345678N 98.765432E", "12°20.44′ N,98°45.55′ E",
        ):
            with self.subTest(text=text):
                self.assert_removed(text, "20", "45", "12.345678", "98.765432")

    def test_map_links_and_opaque_short_links_are_removed(self):
        for url in (
            "https://www.google.com/maps/@12.345678,98.765432,17z",
            "https://maps.google.com/?q=12.345678,98.765432",
            "https://maps.apple.com/?ll=12.345678,98.765432",
            "https://www.openstreetmap.org/#map=17/12.345678/98.765432",
            "https://uri.amap.com/marker?position=98.765432,12.345678",
            "https://map.baidu.com/?location=12.345678,98.765432",
            "https://maps.app.goo.gl/syntheticOpaqueId", "geo:12.345678,98.765432",
            "https://example.invalid/?lat=12.345678&lon=98.765432",
        ):
            with self.subTest(url=url):
                self.assertEqual(redact_locations(url), REMOVED_LOCATION)

    def test_encoded_json_decimal_url_and_html_values_are_removed(self):
        plain = '{"lat":12.345678,"lon":98.765432}'
        encoded = quote(quote(plain, safe=""), safe="")
        for text in (
            encoded, plain.replace('"', '\\"'),
            r"\u006c\u0061\u0074=12\u002e345678",
            r"lat=\x31\x32\x2e345678", "12.345678&#44;98.765432",
            r"https:\/\/maps.google.com\/?q=12.345678%2C98.765432",
            r'{"nested":"{\"lat\":12.345678,\"lon\":98.765432}"}',
        ):
            with self.subTest(text=text):
                self.assert_removed(text, "12.345678", "98.765432", "%253A", "345678")

    def test_safe_text_and_out_of_range_numbers_remain_unchanged(self):
        for text in (
            "今天在合成城市见面", "版本 1.2.3", "999.1234,888.5678", "20,30",
            r"C:\users\user\file.txt", "https://www.baidu.com/search?q=city",
            "https://example.invalid/file%20name", r"print(\"hello\")",
        ):
            with self.subTest(text=text):
                self.assertEqual(redact_locations(text), text)

    def test_unsupported_encoding_fails_without_echoing_payload(self):
        for text in ("synthetic-private-%FF", r"synthetic-private-\ud800", "%" + "25" * 11 + "3A"):
            with self.subTest(text=text), self.assertRaises(PrivacyBoundaryError) as caught:
                redact_locations(text)
            self.assertNotIn("synthetic-private", str(caught.exception))


class ImageMetadataBoundaryTests(unittest.TestCase):
    def image_bytes(self, image, format="PNG", **options):
        stream = BytesIO()
        image.save(stream, format=format, **options)
        return stream.getvalue()

    def test_png_exif_gps_text_and_trailing_data_removed_without_pixel_change(self):
        image = Image.new("RGB", (3, 2), (12, 90, 122))
        image.putpixel((1, 0), (250, 5, 15))
        metadata = PngImagePlugin.PngInfo()
        metadata.add_text("GPS", "12.345678,98.765432")
        exif = Image.Exif()
        exif[34853] = {1: "N", 2: (12.0, 20.0, 44.44), 3: "E", 4: (98.0, 45.0, 55.55)}
        exif[270] = "synthetic-private-description"
        raw = self.image_bytes(image, pnginfo=metadata, exif=exif) + b"synthetic-private-trailing-data"
        cleaned = strip_image_metadata(raw)
        self.assertTrue(cleaned.startswith(b"\x89PNG"))
        self.assertNotIn(b"synthetic-private", cleaned)
        self.assertNotIn(b"12.345678", cleaned)
        with Image.open(BytesIO(cleaned)) as result:
            self.assertEqual(result.tobytes(), image.tobytes())
            self.assertEqual(dict(result.getexif()), {})
            self.assertEqual(result.info, {})

    def test_jpeg_orientation_is_preserved_in_displayed_pixels(self):
        image = Image.new("RGB", (3, 2), (30, 70, 120))
        image.putpixel((0, 1), (255, 0, 0))
        exif = Image.Exif()
        exif[274] = 6
        exif[270] = "synthetic-private-description"
        raw = self.image_bytes(image, format="JPEG", exif=exif)
        with Image.open(BytesIO(raw)) as original:
            expected = ImageOps.exif_transpose(original)
            expected_pixels, expected_size = expected.tobytes(), expected.size
        with Image.open(BytesIO(strip_image_metadata(raw))) as result:
            self.assertEqual(result.size, expected_size)
            self.assertEqual(result.tobytes(), expected_pixels)
            self.assertEqual(dict(result.getexif()), {})

    def test_palette_transparency_preserves_rendered_rgba(self):
        image = Image.new("P", (2, 1))
        image.putpalette([0, 0, 0, 200, 100, 20] + [0] * (768 - 6))
        image.putpixel((1, 0), 1)
        raw = self.image_bytes(image, transparency=0)
        with Image.open(BytesIO(raw)) as original:
            expected = original.convert("RGBA").tobytes()
        with Image.open(BytesIO(strip_image_metadata(raw))) as result:
            self.assertEqual(result.convert("RGBA").tobytes(), expected)
            self.assertNotIn("transparency", result.info)

    def test_invalid_profile_animation_and_unknown_mode_fail_without_raw_fallback(self):
        image = Image.new("RGB", (2, 2))
        animation = self.image_bytes(image, format="GIF", save_all=True, append_images=[Image.new("RGB", (2, 2), "red")])
        invalid_inputs = (
            b"synthetic-private-invalid-image", bytearray(b"not-immutable"), animation,
            self.image_bytes(image, icc_profile=b"synthetic-private-profile"),
            self.image_bytes(Image.new("CMYK", (2, 2)), format="JPEG"),
        )
        for raw in invalid_inputs:
            with self.subTest(kind=type(raw).__name__), self.assertRaises(PrivacyBoundaryError) as caught:
                strip_image_metadata(raw)
            self.assertNotIn("synthetic-private", str(caught.exception))

    def test_decoder_warning_fails_without_printing_embedded_metadata(self):
        def warning_decoder(*args):
            warnings.warn("synthetic-private-metadata", UserWarning)

        with patch("PIL.Image.open", side_effect=warning_decoder), self.assertRaises(PrivacyBoundaryError) as caught:
            strip_image_metadata(b"synthetic-image")
        self.assertNotIn("synthetic-private", str(caught.exception))


class PrivacyLoggingBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.output = StringIO()
        self.handler = protect_log_handler(logging.StreamHandler(self.output), allowed_events={"model.failed"})
        self.handler.setFormatter(logging.Formatter("%(levelname)s %(name)s %(message)s"))
        self.logger = logging.Logger("synthetic.model", logging.DEBUG)
        self.logger.addHandler(self.handler)

    def test_model_body_coordinates_exception_and_stack_are_never_formatted(self):
        try:
            raise ValueError("synthetic-private-exception 12.345678,98.765432")
        except ValueError:
            self.logger.error(
                "model body=%s", "synthetic-private-model 12.345678,98.765432",
                exc_info=True, stack_info=True, extra={"privacy_event": "model.failed", "body": "synthetic-private-extra"},
            )
        text = self.output.getvalue()
        self.assertIn("event=model.failed exception=ValueError", text)
        self.assertIn("ERROR synthetic.model", text)
        for probe in ("synthetic-private", "12.345678", "98.765432", "Traceback", "Stack"):
            self.assertNotIn(probe, text)

    def test_raw_exception_argument_retains_class_only(self):
        self.logger.error("worker failed: %s", RuntimeError("synthetic-private-worker"))
        self.assertIn("exception=RuntimeError", self.output.getvalue())
        self.assertNotIn("synthetic-private", self.output.getvalue())

    def test_arbitrary_extra_fields_and_preformatted_exception_are_removed(self):
        record = self.logger.makeRecord("synthetic.model", logging.ERROR, "secret-path", 20, "synthetic-private", (), None)
        record.body = "synthetic-private-extra"
        record.exc_text = "synthetic-private-preformatted"
        record.stack_info = "synthetic-private-stack"
        record.privacy_event = "synthetic-private-unapproved-event"
        safe = PrivacyLogFilter().filter(record)
        self.assertNotIn("synthetic-private", repr(safe.__dict__))
        self.assertNotIn("body", safe.__dict__)
        self.assertEqual(safe.getMessage(), "event=synthetic.model exception=none")

    def test_multiple_output_handlers_each_retain_exception_type(self):
        second_output = StringIO()
        self.logger.addHandler(protect_log_handler(logging.StreamHandler(second_output)))
        try:
            raise OSError("synthetic-private-disk-body")
        except OSError:
            self.logger.exception("synthetic-private-log-body")
        for text in (self.output.getvalue(), second_output.getvalue()):
            self.assertIn("exception=OSError", text)
            self.assertNotIn("synthetic-private", text)

    def test_payload_objects_are_never_stringified_and_handler_install_is_idempotent(self):
        class Unformattable:
            def __str__(self):
                raise AssertionError("Payload formatting must not run")

        protect_log_handler(self.handler)
        self.logger.error("%s", Unformattable())
        self.assertEqual(len(self.handler.filters), 1)
        self.assertIn("event=synthetic.model", self.output.getvalue())


if __name__ == "__main__":
    unittest.main()
