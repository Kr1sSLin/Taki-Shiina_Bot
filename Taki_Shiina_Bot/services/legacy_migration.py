"""Read-only legacy memory inventory and fail-closed, repeatable import."""
import hashlib
import json
import logging
import os
from collections import defaultdict

from secure_storage import EncryptionConfig, SecureJsonStore
from services.contracts import Domain
from services.domain_cipher import DomainCipher
from services.reliable_chat_store import ReliableChatStore


def read_legacy_memories(path: str, key: bytes) -> tuple[dict, str]:
    if not os.path.isfile(path):
        raise FileNotFoundError(path)
    with open(path, "rb") as source:
        source_digest = hashlib.sha256(source.read()).hexdigest()
    config = EncryptionConfig(key=key, auto_migrate=False, backup_on_migrate=False)
    data = SecureJsonStore(path, logging.getLogger(__name__), config).load_strict({})
    if not isinstance(data, dict):
        raise ValueError("legacy memory root must be an object")
    return data, source_digest


def _records(data: dict):
    for owner, value in sorted(data.items()):
        if not isinstance(owner, str) or not owner:
            raise ValueError("legacy owner is invalid")
        if isinstance(value, str):
            values = [line for line in value.splitlines() if line.strip()]
        elif isinstance(value, list) and all(isinstance(item, str) for item in value):
            values = [item for item in value if item.strip()]
        else:
            raise ValueError("unknown legacy memory shape")
        occurrences = defaultdict(int)
        for text in values:
            occurrences[text] += 1
            occurrence = occurrences[text]
            digest = hashlib.sha256(json.dumps([owner, text, occurrence], ensure_ascii=False).encode()).hexdigest()
            yield owner, text, f"legacy-{digest}", f"legacy/pending/{digest[:16]}"


def import_legacy_pending(data: dict, store: ReliableChatStore) -> dict:
    """Unknown provenance stays blocked; repeated inputs keep the same fact IDs."""
    created = 0
    checked = 0
    audit = hashlib.sha256()
    for owner, text, fact_id, source in _records(data):
        try:
            existing = store.fact_detail(owner, fact_id)
        except Exception as exc:
            # An absent ID is the only expected lookup failure. Other storage/key
            # failures must remain visible and abort the import.
            from services.foundation_store import PermissionDenied
            if not isinstance(exc, PermissionDenied) or "unavailable" not in str(exc):
                raise
            existing = None
        if existing is None:
            store.put_fact(owner, fact_id, Domain.USER_FACT, {
                "text": text,
                "source": source,
                "enabled": False,
                "model_permission": "blocked",
                "stage": "candidate",
                "truth_status": "pending",
                "weight": 0,
                "legacy_source": "user_memories.json",
                "legacy_recorded_date": text[1:11] if text.startswith("[") and len(text) >= 12 else None,
            }, expected_revision=0)
            created += 1
            existing = store.fact_detail(owner, fact_id)
        if (existing["content"] != text or existing["enabled"] is not False or
                existing["modelPermission"] != "blocked" or existing["score"] != 0 or
                existing["stage"] != "candidate" or existing["truthStatus"] != "pending"):
            raise ValueError(f"legacy record mapping mismatch: {fact_id}")
        checked += 1
        audit.update(json.dumps([owner, fact_id, existing["content"], existing["enabled"],
                                 existing["stage"], existing["truthStatus"]],
                                ensure_ascii=False, separators=(",", ":")).encode())
    return {"created": created, "checked": checked, "audit_sha256": audit.hexdigest(),
            "event_head": store.change_head()}


def migrate_file(source: str, destination: str, key: bytes) -> dict:
    source = os.path.abspath(source)
    destination = os.path.abspath(destination)
    if source == destination:
        raise ValueError("source and destination must differ")
    data, source_digest = read_legacy_memories(source, key)
    os.makedirs(os.path.dirname(destination), exist_ok=True)
    store = ReliableChatStore(destination, DomainCipher(key))
    report = import_legacy_pending(data, store)
    return {"source_sha256": source_digest, **report}
