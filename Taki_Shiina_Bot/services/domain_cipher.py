"""Versioned AES-GCM envelopes; HKDF separates domains, AAD binds identities."""
import base64
import binascii
import hashlib
import hmac
import json
import os

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

from secure_storage import DataCorruptionError, DataDecryptError
from services.contracts import Domain
from services.privacy import scrub_locations


class DomainCipher:
    def __init__(self, key: bytes, key_id: str = "primary-v1"):
        if len(key) not in (16, 24, 32) or not key_id or len(key_id) > 80:
            raise ValueError("invalid encryption configuration")
        self.key_id = key_id
        self._keys = {domain: HKDF(algorithm=hashes.SHA256(), length=32, salt=b"tks-foundation-v1", info=domain.value.encode()).derive(key) for domain in Domain}
        self._index_key = HKDF(algorithm=hashes.SHA256(), length=32, salt=b"tks-foundation-v1", info=b"private-index").derive(key)

    def index(self, *parts: str) -> str:
        raw = json.dumps(parts, ensure_ascii=False, separators=(",", ":")).encode()
        return hmac.new(self._index_key, raw, hashlib.sha256).hexdigest()

    def _aad(self, domain: Domain, owner_key: str, identity: str) -> bytes:
        return json.dumps([1, self.key_id, domain.value, owner_key, identity], separators=(",", ":")).encode()

    @staticmethod
    def _scrub(domain: Domain, data):
        """Keep validated original image bytes opaque while scrubbing source text.

        The text scrubber intentionally decodes slash escapes and matches GPS
        labels. Those patterns can occur by chance inside legitimate base64, so
        the authenticated image field must never be treated as prose.
        """
        if domain != Domain.SOURCE_MESSAGE or not isinstance(data, dict):
            return scrub_locations(data)
        metadata = data.get("metadata")
        images = metadata.get("images") if isinstance(metadata, dict) else None
        if not isinstance(images, list) or not images:
            return scrub_locations(data)
        masked_images, originals = [], []
        for image in images:
            if not isinstance(image, dict) or set(image) != {"mimeType", "dataBase64", "sha256"}:
                return scrub_locations(data)
            encoded, digest = image["dataBase64"], image["sha256"]
            if not isinstance(encoded, str) or not isinstance(digest, str):
                return scrub_locations(data)
            try:
                raw = base64.b64decode(encoded, validate=True)
            except (ValueError, TypeError, binascii.Error):
                return scrub_locations(data)
            if not raw or base64.b64encode(raw).decode("ascii") != encoded or hashlib.sha256(raw).hexdigest() != digest:
                return scrub_locations(data)
            masked_images.append({**image, "dataBase64": ""})
            originals.append(encoded)
        masked = {**data, "metadata": {**metadata, "images": masked_images}}
        clean = scrub_locations(masked)
        for image, encoded in zip(clean["metadata"]["images"], originals, strict=True):
            image["dataBase64"] = encoded
        return clean

    def encrypt(self, domain: Domain, owner_key: str, identity: str, data) -> bytes:
        raw = json.dumps(self._scrub(domain, data), ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode()
        nonce = os.urandom(12)
        ciphertext = AESGCM(self._keys[domain]).encrypt(nonce, raw, self._aad(domain, owner_key, identity))
        return json.dumps({"ver": 1, "kid": self.key_id, "nonce": base64.b64encode(nonce).decode(), "ciphertext": base64.b64encode(ciphertext).decode()}, separators=(",", ":")).encode()

    def decrypt(self, domain: Domain, owner_key: str, identity: str, envelope: bytes):
        try:
            payload = json.loads(envelope)
            if payload["ver"] != 1 or payload["kid"] != self.key_id:
                raise DataCorruptionError("unsupported encryption format or key identifier")
            nonce = base64.b64decode(payload["nonce"], validate=True)
            ciphertext = base64.b64decode(payload["ciphertext"], validate=True)
            if len(nonce) != 12 or len(ciphertext) < 16:
                raise ValueError("invalid envelope lengths")
        except (KeyError, ValueError, TypeError) as exc:
            raise DataCorruptionError("invalid encrypted envelope") from exc
        try:
            raw = AESGCM(self._keys[domain]).decrypt(nonce, ciphertext, self._aad(domain, owner_key, identity))
        except InvalidTag as exc:
            raise DataDecryptError("data authentication failed: key mismatch or modified ciphertext") from exc
        try:
            return self._scrub(domain, json.loads(raw))
        except (ValueError, UnicodeError) as exc:
            raise DataCorruptionError("invalid decrypted content") from exc
