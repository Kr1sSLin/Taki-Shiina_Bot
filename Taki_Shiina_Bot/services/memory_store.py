"""Memory transactions layered on the existing encrypted foundation journal.

Legacy rows remain pending. Extraction matching sees only allowed facts; unknown
identity after a disable is quarantined instead of creating a resurrection ID.
"""
import os

from services.contracts import Domain, new_id
from services.foundation_store import FoundationStore, PermissionDenied, VersionConflict
from services.memory_policy import eligible, initial_weight, memory_control, normalized, relevance, timestamp
from services.privacy import scrub_locations


class MemoryStore(FoundationStore):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        with self._transaction() as conn:
            conn.execute("CREATE TABLE IF NOT EXISTS memory_revisions (fact_id TEXT NOT NULL, revision INTEGER NOT NULL, owner TEXT NOT NULL, domain TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(fact_id,revision))")
            conn.execute("CREATE TABLE IF NOT EXISTS memory_ledger (owner TEXT NOT NULL, turn_id TEXT NOT NULL, fact_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(owner,turn_id,fact_id))")
            conn.execute("CREATE TABLE IF NOT EXISTS memory_evidence (owner TEXT NOT NULL, source_id TEXT NOT NULL, fact_id TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(owner,source_id,fact_id))")
            conn.execute("CREATE TABLE IF NOT EXISTS memory_aliases (owner TEXT NOT NULL, identity TEXT NOT NULL, fact_id TEXT NOT NULL, PRIMARY KEY(owner,identity,fact_id))")
            conn.execute("CREATE TABLE IF NOT EXISTS memory_pending (owner TEXT NOT NULL, source_id TEXT NOT NULL, identity TEXT NOT NULL, body BLOB NOT NULL, PRIMARY KEY(owner,source_id,identity))")
            conn.execute("CREATE TABLE IF NOT EXISTS lore_versions (owner TEXT NOT NULL,id TEXT NOT NULL,body BLOB NOT NULL, PRIMARY KEY(owner,id))")
            conn.execute("CREATE TABLE IF NOT EXISTS lore_heads (owner TEXT PRIMARY KEY,id TEXT NOT NULL)")

    def _write_fact(self, conn, owner, fact_id, domain, revision, data):
        data = dict(data)
        old = conn.execute("SELECT * FROM facts WHERE id=? AND owner=?", (fact_id, owner)).fetchone()
        if old:
            _, previous = self._fact(conn, owner, fact_id)
            conn.execute("INSERT OR IGNORE INTO memory_revisions VALUES (?,?,?,?,?)", (fact_id, old["revision"], owner, old["domain"], old["body"]))
            data.setdefault("first_recorded_at", previous.get("first_recorded_at"))
            if previous.get("retention_until") is not None:
                data.setdefault("retention_until", previous["retention_until"])
            # Permission has its own version; content/score changes cannot lift it.
            if previous.get("model_permission") == "blocked" and data.get("enabled"):
                if data.get("permission_version", 0) <= previous.get("permission_version", 1):
                    raise PermissionDenied("content writes cannot restore permission")
            if any(previous.get(field) != data.get(field) for field in ("text", "subject", "temporal_type", "temporal_validity")):
                data["revision_id"] = new_id()
        elif domain in (Domain.USER_FACT, Domain.OFFICIAL_LORE) and data.get("enabled"):
            # Without an adjudicated identity link, a new ID cannot prove it is
            # unrelated to a blocked fact. Preserve source, fail closed here.
            for existing in conn.execute("SELECT id FROM facts WHERE owner=? AND domain=?", (owner, domain.value)):
                _, policy = self._fact(conn, owner, existing["id"])
                if not policy.get("enabled"):
                    raise PermissionDenied("new identity requires permission linkage review")
        data.setdefault("revision_id", new_id())
        data.setdefault("first_recorded_at", self.clock())
        data.setdefault("weight", 0)
        data.setdefault("stage", "candidate")
        data.setdefault("truth_status", "pending")
        data.setdefault("revision_status", "active")
        data.setdefault("model_permission", "allowed" if data.get("enabled") else "blocked")
        data.setdefault("permission_version", 1)
        data.setdefault("temporal_type", "preference")
        data.setdefault("temporal_validity", {"from": None, "until": None, "expression": "", "timezone": None, "precision": "unknown"})
        if type(data["weight"]) is not int or not 0 <= data["weight"] <= 10:
            raise ValueError("weight must be an integer in [0,10]")
        if data["stage"] not in ("candidate", "formal") or data["truth_status"] not in ("pending", "accepted", "disputed", "rejected") or data["revision_status"] not in ("active", "superseded", "retracted"):
            raise ValueError("invalid memory state")
        if data["model_permission"] != ("allowed" if data.get("enabled") else "blocked"):
            raise ValueError("permission fields disagree")
        if data["temporal_type"] not in ("past_event", "current_state", "preference", "plan"):
            raise ValueError("invalid temporal type")
        validity = data["temporal_validity"]
        start, end = timestamp(validity.get("from")), timestamp(validity.get("until"))
        if start is not None and end is not None and end <= start:
            raise ValueError("invalid time range")
        timestamp(data.get("retention_until"))
        data["score"] = data["weight"] / 10
        data = scrub_locations(data)
        super()._write_fact(conn, owner, fact_id, domain, revision, data)
        conn.execute("INSERT INTO memory_revisions VALUES (?,?,?,?,?)", (fact_id, revision, owner, domain.value, self.cipher.encrypt(domain, owner, f"{fact_id}:{revision}", data)))
        aliases = {normalized(data.get("text", "")), *data.get("identity_aliases", [])}
        for alias in aliases - {""}:
            token = self.cipher.index("memory-identity", owner, normalized(alias))
            conn.execute("INSERT OR IGNORE INTO memory_aliases VALUES (?,?,?)", (owner, token, fact_id))

    def set_permission(self, owner, fact_id, *, enabled, expected_revision, targets=("sync",)):
        if type(enabled) is not bool:
            raise ValueError("permission must be boolean")
        key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            row, data = self._fact(conn, key, fact_id)
            if row["revision"] != expected_revision:
                raise VersionConflict("fact revision changed")
            if data["enabled"] == enabled:
                return expected_revision
            data.update(enabled=enabled, model_permission="allowed" if enabled else "blocked", permission_version=data.get("permission_version", 1)+1)
            self._write_fact(conn, key, fact_id, Domain(row["domain"]), expected_revision+1, data)
            self._change(conn, key, fact_id, expected_revision+1, "restored" if enabled else "disabled", targets)
            return expected_revision+1

    def handle_memory_control(self, job):
        """Narrow natural commands; ambiguous references ask, never batch restore.

        Matching uses keyed aliases, not blocked prose in a model prompt. Other
        phrasings remain ordinary input and are covered by the explicit API.
        """
        with self._transaction() as conn:
            self._require_ready(conn)
            lease = self._lease(conn, job)
            if job["payload"].get("kind") != "chat":
                raise PermissionDenied("memory controls require a user chat turn")
            turn, body = self._turn(conn, job["payload"]["turn_id"])
            if turn["owner"] != lease["owner"]:
                raise PermissionDenied("memory control owner mismatch")
            if body.get("memory_control_result"):
                return body["memory_control_result"]
            sources = []
            for source_id in body["sources"]:
                row = conn.execute("SELECT * FROM requests WHERE id=? AND owner=?", (source_id, lease["owner"])).fetchone()
                sources.append(self.cipher.decrypt(Domain.SOURCE_MESSAGE, lease["owner"], source_id, row["body"])["text"])
            # Multiple source commands might contradict one another; ask.
            if len(sources) != 1:
                if any(memory_control(text) for text in sources):
                    body["memory_control_result"] = {"reply": "这几条消息里，你想停用或恢复哪件事？请单独说清楚。"}
                    self._save_turn(conn, turn, body)
                    return body["memory_control_result"]
                return None
            match = memory_control(sources[0])
            if not match:
                return None
            action, target = match
            restoring = action in ("恢复使用", "重新允许使用")
            token = self.cipher.index("memory-identity", lease["owner"], normalized(target.strip("‘’“”\"")))
            ids = {r["fact_id"] for r in conn.execute("SELECT fact_id FROM memory_aliases WHERE owner=? AND identity=?", (lease["owner"], token))}
            if target in ("这个", "这件事", "这条记忆"):
                previous = conn.execute("SELECT id FROM chat_turns WHERE owner=? AND status='completed' AND id<>? ORDER BY created DESC,rowid DESC LIMIT 1", (lease["owner"], turn["id"])).fetchone()
                if previous:
                    _, prior = self._turn(conn, previous["id"])
                    ids = {ref["fact_id"] for ref in prior.get("memory_refs", [])}
                    if prior.get("memory_control_fact_id"):
                        ids.add(prior["memory_control_fact_id"])
                    ids.update(prior.get("blocked_identity_refs", []))
                    for source_id in prior.get("sources", []):
                        ids.update(r["fact_id"] for r in conn.execute("SELECT fact_id FROM memory_evidence WHERE owner=? AND source_id=?", (lease["owner"], source_id)))
            if len(ids) != 1:
                reply = "你想恢复哪条记忆？请说出具体内容，或在记忆管理中选择。" if restoring else "你指的是哪件事？请说出具体内容，或在记忆管理中选择。"
            else:
                fact_id = ids.pop()
                body["memory_control_fact_id"] = fact_id
                fact, data = self._fact(conn, lease["owner"], fact_id)
                enabled = restoring
                if data["enabled"] != enabled:
                    data.update(enabled=enabled, model_permission="allowed" if enabled else "blocked", permission_version=data.get("permission_version", 1)+1)
                    self._write_fact(conn, lease["owner"], fact_id, Domain(fact["domain"]), fact["revision"]+1, data)
                    self._change(conn, lease["owner"], fact_id, fact["revision"]+1, "restored" if enabled else "disabled", ("sync",))
                reply = "已恢复这条记忆的使用权限；原有分数、争议和时间限制仍然保留。" if restoring else "好，以后不再使用或提起这件事。"
                if action in ("删除", "删掉", "彻底删除"):
                    reply = "已停止使用这条记忆。目前还不支持彻底删除原聊天和备份，不能承诺已经删除。"
            body["memory_control_result"] = {"reply": reply}
            self._save_turn(conn, turn, body)
            return body["memory_control_result"]

    def _lore_head(self, conn, owner):
        row = conn.execute("SELECT id FROM lore_heads WHERE owner=?", (owner,)).fetchone()
        return row["id"] if row else None

    def _model_eligible(self, conn, owner, row, data):
        return eligible(data, self.clock(), domain=row["domain"], lore_version=self._lore_head(conn, owner))

    def read_fact(self, owner, fact_id, *, for_model=False):
        value = super().read_fact(owner, fact_id, for_model=False)
        if for_model:
            key = self.cipher.index("owner", owner)
            with self._transaction(write=False) as conn:
                self._require_ready(conn)
                row, data = self._fact(conn, key, fact_id)
                if not self._model_eligible(conn, key, row, data):
                    raise PermissionDenied("memory does not meet model eligibility")
                value = {"fact_id": fact_id, "revision": row["revision"], "domain": row["domain"], "data": data}
        return value

    def select_memories(self, owner, query, *, limit=12, domain=None):
        if not 1 <= limit <= 1000:
            raise ValueError("invalid context memory limit")
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            result = []
            for row in conn.execute("SELECT * FROM facts WHERE owner=?", (key,)):
                if domain is not None and row["domain"] != domain:
                    continue
                _, data = self._fact(conn, key, row["id"])
                if not self._model_eligible(conn, key, row, data):
                    continue
                rank = relevance(query, data.get("text", ""))
                if rank:
                    result.append({"fact_id": row["id"], "revision": row["revision"], "revision_id": data["revision_id"], "permission_version": data["permission_version"], "domain": row["domain"], "subject": data.get("subject"), "text": data["text"], "temporal_type": data["temporal_type"], "temporal_validity": data["temporal_validity"], "rank": rank, "weight": data["weight"]})
            return sorted(result, key=lambda x: (-x["rank"], -x["weight"], x["fact_id"]))[:limit]

    def search(self, owner, domain, query, *, limit=100):
        return [self.read_fact(owner, ref["fact_id"], for_model=True) for ref in self.select_memories(owner, query, limit=limit, domain=domain)]

    def fact_detail(self, owner, fact_id):
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            row, data = self._fact(conn, key, fact_id)
            evidence = [self.cipher.decrypt(Domain.EVIDENCE, key, f"{item['source_id']}:{fact_id}", item["body"]) for item in conn.execute("SELECT * FROM memory_evidence WHERE owner=? AND fact_id=?", (key, fact_id))]
            return {"factId": fact_id, "userId": owner, "revision": row["revision"], "revisionId": data.get("revision_id"), "domain": row["domain"],
                    "subject": data.get("subject"), "content": data.get("text", ""), "enabled": data.get("enabled", False),
                    "score": data.get("weight", 0)/10, "stage": data.get("stage", "candidate"), "truthStatus": data.get("truth_status", "pending"),
                    "revisionStatus": data.get("revision_status", "active"), "modelPermission": data.get("model_permission", "blocked"),
                    "permissionVersion": data.get("permission_version", 1), "firstRecordedAt": data.get("first_recorded_at"),
                    "temporalType": data.get("temporal_type"), "temporalValidity": data.get("temporal_validity"), "retentionUntil": data.get("retention_until"),
                    "sources": [{"sourceMessageId": item["source_id"], "logicalTurnId": item["turn_id"], "fragment": item.get("fragment", ""),
                                 "operation": item.get("operation", "evidence"), "revisionId": item.get("revision_id")} for item in evidence]}

    def _validate_memories(self, conn, owner, refs, *, strict_revision=True):
        for ref in refs:
            row, data = self._fact(conn, owner, ref["fact_id"])
            if (strict_revision and row["revision"] != ref["revision"]) or data.get("revision_id") != ref["revision_id"] or data.get("permission_version") != ref["permission_version"] or not self._model_eligible(conn, owner, row, data):
                raise VersionConflict("selected memory changed or expired")

    def _reinforce(self, conn, owner, turn_id, fact_id, reason, data, revision):
        effect = {"reason": reason, "revision": revision, "revision_id": data["revision_id"], "at": self.clock()}
        return conn.execute("INSERT OR IGNORE INTO memory_ledger VALUES (?,?,?,?)", (owner, turn_id, fact_id, self.cipher.encrypt(Domain.EVIDENCE, owner, f"{turn_id}:{fact_id}", effect))).rowcount == 1

    def _commit_memory_usage(self, conn, owner, turn_id, refs):
        self._validate_memories(conn, owner, refs)
        if os.getenv("MEMORY_REINFORCEMENT_ENABLED", "0") != "1":
            return
        for ref in refs:
            row, data = self._fact(conn, owner, ref["fact_id"])
            if row["domain"] != Domain.USER_FACT or not self._reinforce(conn, owner, turn_id, row["id"], "usage", data, row["revision"]):
                continue
            data["weight"] = min(10, data["weight"]+1)
            self._write_fact(conn, owner, row["id"], Domain.USER_FACT, row["revision"]+1, data)
            self._change(conn, owner, row["id"], row["revision"]+1, "reinforced", ("sync",))

    def extraction_candidates(self, job):
        """Controlled extractor input, never part of a reply context."""
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            lease = self._lease(conn, job)
            result = []
            for row in conn.execute("SELECT * FROM facts WHERE owner=? AND domain=? ORDER BY id", (lease["owner"], Domain.USER_FACT.value)):
                _, data = self._fact(conn, lease["owner"], row["id"])
                retention = timestamp(data.get("retention_until"))
                if data.get("enabled") and data.get("revision_status") == "active" and (retention is None or self.clock() < retention):
                    result.append({"fact_id": row["id"], "revision": row["revision"], "text": data["text"], "subject": data.get("subject"), "truth_status": data.get("truth_status", "pending"), "temporal_type": data["temporal_type"], "temporal_validity": data["temporal_validity"]})
            return result

    def _apply_revision(self, conn, owner, source_id, turn_id, source_text, item):
        operation = item.get("operation")
        fragment = item.get("evidence", "")
        if not isinstance(fragment, str) or not fragment or fragment not in source_text or item.get("subject") != "user":
            return None
        target = item.get("fact_id")
        if not target:
            alias = item.get("target_text")
            if not isinstance(alias, str) or not alias or normalized(alias) not in normalized(source_text):
                self._request_memory_clarification(conn, turn_id)
                return None
            matches = conn.execute("SELECT fact_id FROM memory_aliases WHERE owner=? AND identity=?", (owner, self.cipher.index("memory-identity", owner, normalized(alias)))).fetchall()
            if len(matches) != 1:
                self._request_memory_clarification(conn, turn_id)
                return None
            target = matches[0]["fact_id"]
        row, previous = self._fact(conn, owner, target)
        if row["domain"] != Domain.USER_FACT:
            raise PermissionDenied("user corrections cannot rewrite another domain")
        if item.get("fact_id") and item.get("expected_revision") != row["revision"]:
            raise VersionConflict("correction target changed")
        if conn.execute("SELECT 1 FROM memory_evidence WHERE owner=? AND source_id=? AND fact_id=?", (owner, source_id, target)).fetchone():
            return target
        data = dict(previous)
        if operation == "dispute":
            # Even a model-suspected conflict can restrict use, never certify it.
            data["truth_status"] = "disputed"
            turn, body = self._turn(conn, turn_id)
            body["memory_clarification"] = True
            self._save_turn(conn, turn, body)
        elif item.get("assertion") != "explicit":
            return None
        elif operation == "correct":
            if not isinstance(item.get("text"), str) or not item["text"].strip():
                return None
            data.update(text=item["text"].strip(), revision_id=new_id(), weight=6 if previous.get("stage") == "formal" else 3,
                        stage=previous.get("stage", "candidate"), truth_status="accepted", revision_status="active", source=source_id)
            data["temporal_type"] = item.get("temporal_type", previous["temporal_type"])
            data["temporal_validity"] = item.get("temporal_validity", previous["temporal_validity"])
        elif operation == "confirm":
            if previous.get("truth_status") != "disputed":
                return None
            data["truth_status"] = "accepted"
        elif operation == "change":
            # A real change ends the old state, without rewriting past evidence.
            if previous.get("temporal_type") not in ("current_state", "preference"):
                return None
            validity = dict(previous["temporal_validity"])
            effective = timestamp((item.get("temporal_validity") or {}).get("from")) or self.clock()
            if validity.get("from") is not None and effective <= timestamp(validity["from"]):
                return None
            validity["until"] = effective
            data["temporal_validity"] = validity
        else:
            return None
        evidence = {"source_id": source_id, "turn_id": turn_id, "fragment": fragment, "operation": operation, "revision_id": data["revision_id"]}
        conn.execute("INSERT INTO memory_evidence VALUES (?,?,?,?)", (owner, source_id, target, self.cipher.encrypt(Domain.EVIDENCE, owner, f"{source_id}:{target}", evidence)))
        self._reinforce(conn, owner, turn_id, target, operation, data, row["revision"]+1)
        self._write_fact(conn, owner, target, Domain.USER_FACT, row["revision"]+1, data)
        self._change(conn, owner, target, row["revision"]+1, "updated", ("sync",))
        if operation == "change":
            new_item = {k: v for k, v in item.items() if k not in ("operation", "fact_id", "expected_revision", "target_text")}
            return self._apply_evidence(conn, owner, source_id, turn_id, source_text, new_item)
        return target

    def _request_memory_clarification(self, conn, turn_id):
        turn, body = self._turn(conn, turn_id)
        body.update(memory_clarification=True, memory_identity_unresolved=True)
        self._save_turn(conn, turn, body)

    def _apply_evidence(self, conn, owner, source_id, turn_id, source_text, item):
        # String-only legacy extraction has no verifiable evidence span: pending.
        if isinstance(item, str):
            item = {"text": item, "evidence": "", "assertion": "inferred"}
        if not isinstance(item, dict) or not isinstance(item.get("text"), str) or not item["text"].strip():
            return None
        if item.get("operation") in ("correct", "dispute", "confirm", "change"):
            return self._apply_revision(conn, owner, source_id, turn_id, source_text, item)
        if item.get("operation", "evidence") != "evidence":
            return None
        text = scrub_locations(item["text"].strip())
        evidence = item.get("evidence", "")
        explicit = (item.get("assertion") == "explicit" and item.get("subject") == "user"
                    and isinstance(evidence, str) and bool(evidence.strip()) and evidence in source_text)
        temporal = item.get("temporal_type", "preference")
        validity = item.get("temporal_validity") or {"from": None, "until": None, "expression": "", "timezone": None, "precision": "unknown"}
        # Identity contains event dates; two trips to one place remain distinct.
        time_key = (timestamp(validity.get("from")), timestamp(validity.get("until")))
        alias = normalized(normalized(text) + "|" + str(item.get("subject", "unknown")) + "|" + temporal + "|" + str(time_key))
        matches = conn.execute("SELECT fact_id FROM memory_aliases WHERE owner=? AND identity=?", (owner, self.cipher.index("memory-identity", owner, alias))).fetchall()
        target = item.get("fact_id")
        if target:
            row, previous = self._fact(conn, owner, target)
            if row["domain"] != Domain.USER_FACT:
                raise PermissionDenied("evidence cannot cross domains")
            if item.get("expected_revision") != row["revision"]:
                raise VersionConflict("extraction match changed")
            if previous.get("subject") != item.get("subject"):
                return None
            previous_time = previous.get("temporal_validity") or {}
            if previous.get("temporal_type") != temporal or (timestamp(previous_time.get("from")), timestamp(previous_time.get("until"))) != time_key:
                return None  # Time ambiguity must not merge events or state changes.
        elif len(matches) == 1:
            target = matches[0]["fact_id"]
            row, previous = self._fact(conn, owner, target)
        elif len(matches) > 1:
            return None
        else:
            target, row, previous = new_id(), None, None
            for existing in conn.execute("SELECT id FROM facts WHERE owner=? AND domain=?", (owner, Domain.USER_FACT.value)):
                _, policy = self._fact(conn, owner, existing["id"])
                if not policy.get("enabled"):
                    # Unknown relation to a disabled identity: keep only a
                    # controlled source reference for review, never its body.
                    token = self.cipher.index("pending-memory", owner, alias)
                    pending = {"source_id": source_id, "reason": "permission_identity_unresolved"}
                    conn.execute("INSERT OR IGNORE INTO memory_pending VALUES (?,?,?,?)", (owner, source_id, token, self.cipher.encrypt(Domain.EVIDENCE, owner, f"{source_id}:{token}", pending)))
                    return None
        if previous and (not previous["enabled"] or previous.get("revision_status") != "active" or previous.get("truth_status") in ("disputed", "rejected")):
            if not previous["enabled"]:
                turn, body = self._turn(conn, turn_id)
                body["blocked_identity_refs"] = sorted(set(body.get("blocked_identity_refs", [])) | {target})
                self._save_turn(conn, turn, body)
            return None
        if not explicit and previous:
            return target  # Repeated inference has no new evidence or score.
        if previous is None:
            weight = initial_weight(temporal, validity) if explicit else 0
            data = {"text": text, "enabled": True, "weight": weight, "stage": "formal" if weight >= 6 else "candidate", "truth_status": "accepted" if explicit else "pending", "subject": "user" if explicit else item.get("subject", "unknown"), "source": source_id, "temporal_type": temporal, "temporal_validity": validity, "identity_aliases": [alias, normalized(evidence)] if explicit else [alias], "revision_id": new_id()}
        else:
            data = dict(previous)
        if explicit:
            data["identity_aliases"] = sorted(set(data.get("identity_aliases", [])) | {alias, normalized(evidence), normalized(text)})
        evidence_body = {"source_id": source_id, "turn_id": turn_id, "fragment": evidence, "explicit": explicit, "revision_id": data["revision_id"]}
        if not conn.execute("INSERT OR IGNORE INTO memory_evidence VALUES (?,?,?,?)", (owner, source_id, target, self.cipher.encrypt(Domain.EVIDENCE, owner, f"{source_id}:{target}", evidence_body))).rowcount:
            return target
        revision = row["revision"] if row else 0
        # Formal evidence is recorded but never scores without committed usage.
        fresh = False
        if previous is None or previous.get("stage") != "formal":
            fresh = self._reinforce(conn, owner, turn_id, target, "evidence" if previous else "initial", data, revision+1)
        if previous and previous.get("stage") != "formal" and explicit and fresh and os.getenv("MEMORY_REINFORCEMENT_ENABLED", "0") == "1":
            data["weight"] = min(10, data["weight"]+1)
            data["truth_status"] = "accepted"
            data["stage"] = "formal" if data["weight"] >= 6 else "candidate"
        # A fresh explicit statement may confirm a pending identity. This is new
        # evidence, never an automatic score backfill of legacy rows.
        if previous and explicit and previous.get("truth_status") == "pending" and fresh and os.getenv("MEMORY_REINFORCEMENT_ENABLED", "0") == "1":
            weight = initial_weight(temporal, validity)
            data.update(weight=weight, stage="formal" if weight >= 6 else "candidate", truth_status="accepted")
        self._write_fact(conn, owner, target, Domain.USER_FACT, revision+1, data)
        self._change(conn, owner, target, revision+1, "created" if not previous else "evidence", ("sync",))
        return target

    def revision_history(self, owner, fact_id):
        key = self.cipher.index("owner", owner)
        with self._transaction(write=False) as conn:
            self._require_ready(conn)
            _, current = self._fact(conn, key, fact_id)
            history = []
            for row in conn.execute("SELECT * FROM memory_revisions WHERE owner=? AND fact_id=? ORDER BY revision", (key, fact_id)):
                data = self.cipher.decrypt(Domain(row["domain"]), key, f"{fact_id}:{row['revision']}", row["body"])
                history.append({"revision": row["revision"], "revisionStatus": "superseded" if data.get("revision_id") != current.get("revision_id") else data.get("revision_status", "active"), "data": data})
            return history

    def stage_lore(self, owner, version_id, entries):
        """Local editorial API. Never accepts a user chat as verification."""
        if not isinstance(version_id, str) or not version_id or not entries or len(entries) > 1000:
            raise ValueError("bounded version and entries required")
        ids = set()
        for entry in entries:
            if not all(isinstance(entry.get(field), str) and entry[field].strip() for field in ("entry_id", "text", "subject")):
                raise ValueError("lore identity, content and actual actor required")
            if entry["entry_id"] in ids:
                raise ValueError("duplicate lore entry")
            ids.add(entry["entry_id"])
            source = entry.get("source", {})
            if not all(source.get(field) for field in ("work", "locator", "language", "url", "acquired_at", "acquisition")) or not str(source["url"]).startswith("https://"):
                raise ValueError("traceable official source required")
            timestamp(source["acquired_at"])
        key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            if conn.execute("SELECT 1 FROM lore_versions WHERE owner=? AND id=?", (key, version_id)).fetchone():
                raise VersionConflict("lore version already exists")
            body = {"status": "staged", "entries": entries, "review": None, "replay": None}
            conn.execute("INSERT INTO lore_versions VALUES (?,?,?)", (key, version_id, self.cipher.encrypt(Domain.OFFICIAL_LORE, key, version_id, body)))

    def _lore_version(self, conn, key, version_id):
        row = conn.execute("SELECT body FROM lore_versions WHERE owner=? AND id=?", (key, version_id)).fetchone()
        if row is None:
            raise PermissionDenied("lore version unavailable")
        return self.cipher.decrypt(Domain.OFFICIAL_LORE, key, version_id, row["body"])

    def review_lore(self, owner, version_id, decisions, *, reviewer, verification_record, replay):
        if not reviewer or not verification_record or not replay or any(case.get("passed") is not True for case in replay):
            raise ValueError("verification record and passing replay required")
        key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            value = self._lore_version(conn, key, version_id)
            if value["status"] != "staged":
                raise VersionConflict("only staged lore can be reviewed")
            if set(decisions) != {e["entry_id"] for e in value["entries"]} or any(state not in ("verified", "pending", "conflict") for state in decisions.values()):
                raise ValueError("each lore entry needs a verification decision")
            value.update(status="reviewed", review={"reviewer": reviewer, "record": verification_record, "decisions": decisions, "at": self.clock()}, replay=replay)
            conn.execute("UPDATE lore_versions SET body=? WHERE owner=? AND id=?", (self.cipher.encrypt(Domain.OFFICIAL_LORE, key, version_id, value), key, version_id))

    def publish_lore(self, owner, version_id, *, expected_head=None):
        key = self.cipher.index("owner", owner)
        with self._transaction() as conn:
            self._require_ready(conn)
            if self._lore_head(conn, key) != expected_head:
                raise VersionConflict("active lore version changed")
            value = self._lore_version(conn, key, version_id)
            if value["status"] != "reviewed":
                raise PermissionDenied("lore must be verified and replayed before publication")
            selected = [entry for entry in value["entries"] if value["review"]["decisions"][entry["entry_id"]] == "verified"]
            if not selected:
                raise ValueError("no verified lore entries")
            for entry in selected:
                fact_id = self.cipher.index("lore-fact", key, entry["entry_id"])
                existing = conn.execute("SELECT * FROM facts WHERE id=? AND owner=?", (fact_id, key)).fetchone()
                previous = self._fact(conn, key, fact_id)[1] if existing else {}
                data = {"text": entry["text"], "subject": entry["subject"], "enabled": previous.get("enabled", True), "model_permission": previous.get("model_permission", "allowed"), "permission_version": previous.get("permission_version", 1), "weight": 10, "stage": "formal", "truth_status": "accepted", "revision_status": "active", "temporal_type": "past_event", "source_verified": True, "source": entry["source"], "lore_version": version_id, "verification": value["review"]}
                revision = existing["revision"]+1 if existing else 1
                self._write_fact(conn, key, fact_id, Domain.OFFICIAL_LORE, revision, data)
                self._change(conn, key, fact_id, revision, "updated" if existing else "created", ("sync",))
            conn.execute("INSERT OR REPLACE INTO lore_heads VALUES (?,?)", (key, version_id))
            value.update(status="published", published_at=self.clock())
            conn.execute("UPDATE lore_versions SET body=? WHERE owner=? AND id=?", (self.cipher.encrypt(Domain.OFFICIAL_LORE, key, version_id, value), key, version_id))
            # Publication also invalidates running generations if all IDs are new.
            first = self.cipher.index("lore-fact", key, selected[0]["entry_id"])
            first_row, _ = self._fact(conn, key, first)
            self._change(conn, key, first, first_row["revision"], "updated", ("sync",))
            return {"version": version_id, "published": len(selected), "isolated": len(value["entries"])-len(selected)}
