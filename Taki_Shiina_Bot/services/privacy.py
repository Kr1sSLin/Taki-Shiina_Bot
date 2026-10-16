"""Conservative, local location removal at storage and model boundaries.

Supported encodings are JSON/Unicode escapes, URL percent encoding and HTML
entities, with at most eight nested layers. This does not interpret arbitrary
binary/base64 payloads, OCR, place names or coordinates drawn into image pixels.
The reliable WS image path is an explicit user-authorized exception: it keeps
validated original image bytes, including EXIF, outside text scrubbing. The
optional ``strip_image_metadata`` helper is not used by that path.
"""
from __future__ import annotations

import html
from io import BytesIO
import json
import re
from urllib.parse import unquote
import warnings

REMOVED_LOCATION = "[精确坐标已移除]"


class PrivacyBoundaryError(ValueError):
    """The boundary cannot produce a reliably sanitised representation."""


_DECIMAL = r"[-+]?\d{1,3}(?:\.\d+(?:[eE][-+]?\d+)?|[eE][-+]?\d+)"
_NUMBER = r"[-+]?\d{1,3}(?:\.\d+)?(?:[eE][-+]?\d+)?"
_PAIR = re.compile(rf"(?<![A-Za-z0-9_.])({_DECIMAL})\s*[,，、/;；\s]\s*({_NUMBER})(?![A-Za-z0-9_.])")
_REVERSE_PAIR = re.compile(rf"(?<![A-Za-z0-9_.])({_NUMBER})\s*[,，、/;；\s]\s*({_DECIMAL})(?![A-Za-z0-9_.])")
_KEY_NAME = r"(?:gps[_ -]?)?(?:latitude|longitude|lat|lon|lng)|coordinates?|gps|经度|纬度|坐标|北纬|南纬|东经|西经"
_FIELD = re.compile(
    rf"(?i)(?<![A-Za-z0-9_])(?:{_KEY_NAME})[\"']?\s*[=:：]\s*"
    rf"(?:\"(?:\\.|[^\"\\])*\"|'(?:\\.|[^'\\])*'|\[[^\]]*\]|\{{[^}}]*\}}|{_NUMBER}(?:\s*[NSEW])?|[^\s,;，；}}\]]+)"
)
_LABEL = re.compile(rf"(?i)(?<![A-Za-z0-9_])(?:{_KEY_NAME})\s*{_NUMBER}(?![A-Za-z0-9_.])")
_DMS = re.compile(
    r"(?i)(?<![A-Za-z0-9_.])(?:[NSEW]\s*)?[-+]?\d{1,3}\s*[°º度]\s*"
    r"\d{1,2}(?:\.\d+)?\s*['′分](?:\s*\d{1,2}(?:\.\d+)?\s*[\"″秒])?\s*[NSEW]?"
)
_CARDINAL = re.compile(rf"(?i)(?<![A-Za-z0-9_.])(?:[NS]\s*{_DECIMAL}|[EW]\s*{_DECIMAL}|{_DECIMAL}\s*[NSEW])(?![A-Za-z0-9_])")
_URL = re.compile(r"(?i)(?:https?://|geo:)[^\s<>\"']+")
_MAP_URL = re.compile(
    r"(?i)(?:^geo:|://(?:maps\.|maps\.app\.|j\.map\.)|"
    r"://(?:www\.)?(?:google\.[^/]+|bing\.com|apple\.com)/maps|"
    r"://(?:[^/]+\.)?(?:amap\.com|openstreetmap\.org|map\.qq\.com)/|://map\.baidu\.com/|"
    r"://goo\.gl/maps)"
)
_UNICODE_ESCAPE = re.compile(r"(?:\\u[0-9a-fA-F]{4})+")
_HEX_ESCAPE = re.compile(r"\\x([0-9a-fA-F]{2})")
_LOCATION_KEYS = {
    "latitude", "longitude", "lat", "lon", "lng", "coordinate", "coordinates",
    "gps", "gpslatitude", "gpslongitude", "gpslatituderef", "gpslongituderef",
    "经度", "纬度", "坐标", "北纬", "南纬", "东经", "西经",
}


def _decode_layer(text: str) -> str:
    try:
        text = unquote(html.unescape(text), encoding="utf-8", errors="strict")
        text = text.replace("\\\\", "\\").replace('\\"', '"').replace("\\/", "/")
        text = _UNICODE_ESCAPE.sub(lambda match: json.loads('"' + match[0] + '"'), text)
        text = _HEX_ESCAPE.sub(lambda match: chr(int(match[1], 16)), text)
        text = text.replace("\\n", "\n").replace("\\r", "\r").replace("\\t", "\t")
    except (UnicodeError, ValueError):
        raise PrivacyBoundaryError("Location encoding could not be decoded safely") from None
    if any(0xD800 <= ord(char) <= 0xDFFF for char in text):
        raise PrivacyBoundaryError("Location encoding contains invalid Unicode")
    return text


def _decode_text(text: str) -> str:
    for _ in range(8):
        decoded = _decode_layer(text)
        if decoded == text:
            return text
        text = decoded
    if _decode_layer(text) != text:
        raise PrivacyBoundaryError("Location encoding exceeds the supported nesting limit")
    return text


def _redact_plain(text: str) -> str:
    def replace_pair(match):
        first, second = abs(float(match[1])), abs(float(match[2]))
        valid = (first <= 180 and second <= 90) or (first <= 90 and second <= 180)
        return REMOVED_LOCATION if valid else match[0]

    def replace_url(match):
        url = match[0]
        if _MAP_URL.search(url) or _PAIR.search(url) or _REVERSE_PAIR.search(url) or _FIELD.search(url):
            return REMOVED_LOCATION
        return url

    text = _URL.sub(replace_url, text)
    text = _DMS.sub(REMOVED_LOCATION, text)
    text = _CARDINAL.sub(REMOVED_LOCATION, text)
    text = _REVERSE_PAIR.sub(replace_pair, _PAIR.sub(replace_pair, text))
    text = _FIELD.sub(REMOVED_LOCATION, text)
    text = _LABEL.sub(REMOVED_LOCATION, text)
    return text


def redact_locations(text: str) -> str:
    """Remove supported precise-location forms, preserving unrelated text.

    Encoded input is decoded only in the returned representation when location
    removal was necessary. Unsupported nested/invalid encodings fail closed.
    """
    decoded = _decode_text(text)
    # Real JSON retains valid structure, including nested coordinate fields.
    if decoded.lstrip().startswith(("{", "[")):
        try:
            structured = json.loads(decoded)
        except (ValueError, RecursionError):
            structured = None
        if isinstance(structured, (dict, list)):
            scrubbed = scrub_locations(structured)
            if scrubbed != structured:
                return json.dumps(scrubbed, ensure_ascii=False)
            return text
    cleaned = _redact_plain(decoded)
    return cleaned if cleaned != decoded else text


def scrub_locations(value):
    """Recursively sanitise existing JSON-compatible storage/model payloads."""
    if isinstance(value, str):
        return redact_locations(value)
    if isinstance(value, dict):
        result = {}
        for key, item in value.items():
            key_text = _decode_text(str(key))
            normalised = re.sub(r"[\s_-]", "", key_text).casefold()
            result[redact_locations(str(key))] = REMOVED_LOCATION if normalised in _LOCATION_KEYS else scrub_locations(item)
        return result
    if isinstance(value, (list, tuple)):
        return [scrub_locations(item) for item in value]
    return value


def strip_image_metadata(image_bytes: bytes) -> bytes:
    """Return a lossless PNG with EXIF/GPS, text and trailing metadata removed.

    EXIF orientation is applied before stripping so the displayed orientation is
    retained. Single-frame RGB/RGBA/greyscale/palette images are supported. Other
    modes, animation and embedded colour profiles fail explicitly: silently
    dropping a profile could change the displayed colours. Pixel content is not
    inspected for written coordinates and must not be claimed to pass OCR.
    """
    from PIL import Image, ImageOps

    if not isinstance(image_bytes, bytes):
        raise PrivacyBoundaryError("Image boundary requires immutable encoded bytes")
    try:
        with warnings.catch_warnings():
            # Decoder warnings may contain embedded metadata too; turn them
            # into the same generic failure instead of writing raw stderr.
            warnings.simplefilter("error")
            with Image.open(BytesIO(image_bytes)) as source:
                if getattr(source, "n_frames", 1) != 1:
                    raise PrivacyBoundaryError("Animated or multi-frame images cannot be sanitised safely")
                if source.info.get("icc_profile") or any(key in source.info for key in ("gamma", "chromaticity")):
                    raise PrivacyBoundaryError("Embedded colour profiles require a verified conversion")
                source.load()
                image = ImageOps.exif_transpose(source)
                if image.mode == "P" or "transparency" in image.info:
                    image = image.convert("RGBA")
                if image.mode not in {"1", "L", "LA", "RGB", "RGBA"}:
                    raise PrivacyBoundaryError("Image colour mode is not supported by the privacy boundary")
                clean = Image.frombytes(image.mode, image.size, image.tobytes())
                output = BytesIO()
                clean.save(output, format="PNG")
                return output.getvalue()
    except PrivacyBoundaryError:
        raise
    except Exception:
        raise PrivacyBoundaryError("Image metadata could not be removed safely") from None
