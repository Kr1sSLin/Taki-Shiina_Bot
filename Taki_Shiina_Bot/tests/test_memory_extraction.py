"""Live-evaluation regressions: trusted versions, day windows and fast extraction."""
from copy import deepcopy
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock

from services.contracts import ChatCommand, Domain
from services.domain_cipher import DomainCipher
from services.foundation_store import VersionConflict
from services.memory_extraction import bind_extractions, relative_day_windows
from services.memory_policy import timestamp
from services.reliable_chat_store import ReliableChatStore
from services.text_generation import TextGenerator

NOW = timestamp("2026-10-16T12:00:00+08:00")


def temporary(expression="今天", **changes):
    return {"text": "用户今天发烧", "evidence": f"我{expression}发烧了", "subject": "user", "assertion": "explicit",
            "temporal_type": "current_state", "temporal_validity": {"from": None, "until": None,
            "expression": expression, "timezone": "Asia/Shanghai", "precision": "day"}, **changes}


class MemoryExtractionContractTests(unittest.TestCase):
    def test_day_windows_use_source_time_in_beijing_and_roll_over_the_year(self):
        windows = relative_day_windows("今天休息，明天出差", timestamp("2026-12-31T23:50:00+08:00"))
        self.assertEqual(windows["明天"]["from"], "2027-01-01T00:00:00+08:00")
        self.assertEqual(windows["明天"]["until"], "2027-01-02T00:00:00+08:00")
        self.assertEqual(relative_day_windows("今天发烧", None), {})

    def test_explicit_days_get_reference_windows_without_inventing_completion(self):
        for day, start, end in (("今天", 16, 17), ("明天", 17, 18)):
            with self.subTest(day=day):
                text = f"我{day}发烧了"
                fact = temporary(day)
                before = deepcopy(fact)
                result = bind_extractions({"facts": [fact]}, [], text, relative_day_windows(text, NOW))[0]
                self.assertEqual(result["temporal_validity"]["from"], f"2026-10-{start}T00:00:00+08:00")
                self.assertEqual(result["temporal_validity"]["until"], f"2026-10-{end}T00:00:00+08:00")
                self.assertEqual(result["temporal_type"], "current_state")
                self.assertEqual(fact, before)

    def test_existing_range_ongoing_unknown_and_mismatched_dates_are_preserved(self):
        text = "我今天发烧了"
        original = temporary()
        variants = [
            {**original, "temporal_validity": {**original["temporal_validity"], "until": "2026-10-20T00:00:00+08:00"}},
            {**original, "temporal_validity": {**original["temporal_validity"], "precision": "ongoing"}},
            {**original, "temporal_validity": {**original["temporal_validity"], "expression": "最近"}},
            {**original, "temporal_validity": {**original["temporal_validity"], "from": "2026-10-15T00:00:00+08:00"}},
            {**original, "evidence": "我今天发烧了，明天休息"},
        ]
        for fact in variants:
            with self.subTest(fact=fact):
                source = fact["evidence"]
                self.assertEqual(bind_extractions([fact], [], source, relative_day_windows(source, NOW)), [fact])
        self.assertEqual(bind_extractions([original], [], text, {}), [original])

    def test_only_verifiable_explicit_user_evidence_can_get_a_day_window(self):
        for changes in ({"evidence": "不在原文"}, {"assertion": "inferred"}, {"subject": "friend"}):
            with self.subTest(changes=changes):
                fact = temporary(**changes)
                self.assertEqual(bind_extractions([fact], [], "我今天发烧了", relative_day_windows("今天", NOW)), [fact])

    def test_candidate_version_is_bound_and_existing_time_range_is_not_replaced(self):
        fact = temporary(fact_id="known", expected_revision=99)
        result = bind_extractions([fact], [{"fact_id": "known", "revision": 4}], fact["evidence"], relative_day_windows("今天", NOW))[0]
        self.assertEqual(result["expected_revision"], 4)
        self.assertEqual(result["temporal_validity"], fact["temporal_validity"])

    def test_unknown_candidate_is_rejected_and_user_supplied_target_text_survives(self):
        with self.assertRaises(ValueError):
            bind_extractions([{"fact_id": "not-allowed"}], [], "原文", {})
        fact = {"target_text": "我住苏州", "operation": "correct", "expected_revision": 99}
        self.assertEqual(bind_extractions([fact], [], "我住苏州", {}), [{"target_text": "我住苏州", "operation": "correct"}])

    def test_json_object_and_legacy_array_have_the_same_list_contract(self):
        self.assertEqual(bind_extractions({"facts": []}, [], "", {}), [])
        self.assertEqual(bind_extractions(["unverified legacy text"], [], "", {}), ["unverified legacy text"])
        for payload in ({}, {"facts": None}, {"facts": "invalid"}, [42]):
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                bind_extractions(payload, [], "", {})


class MemoryExtractionModelTests(unittest.IsolatedAsyncioTestCase):
    def generator(self, payload):
        response = SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(payload)))])
        create = AsyncMock(return_value=response)
        client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=create)))
        return TextGenerator(None, None, None, None, client, None), create

    async def test_only_extraction_disables_thinking_and_requests_json(self):
        generator, create = self.generator({"facts": [temporary()]})
        facts = await generator.extract_with_context("我今天发烧了", [], NOW)
        call = create.call_args.kwargs
        self.assertEqual(call["extra_body"], {"thinking": {"type": "disabled"}})
        self.assertEqual(call["response_format"], {"type": "json_object"})
        self.assertEqual(call["temperature"], 0)
        message = json.loads(call["messages"][-1]["content"])
        self.assertEqual(message["day_windows"]["今天"]["until"], "2026-10-17T00:00:00+08:00")
        self.assertEqual(facts[0]["temporal_validity"]["until"], "2026-10-17T00:00:00+08:00")

    async def test_candidates_are_snapshotted_before_the_model_await(self):
        generator, create = self.generator({"facts": [{"fact_id": "known", "expected_revision": 99}]})
        candidates = [{"fact_id": "known", "revision": 1}]
        response = create.return_value
        async def mutate(**kwargs):
            candidates[0]["revision"] = 2
            return response
        create.side_effect = mutate
        self.assertEqual((await generator.extract_with_context("原文", candidates, NOW))[0]["expected_revision"], 1)

    async def test_bound_version_does_not_rebase_to_a_fact_changed_during_the_call(self):
        with tempfile.TemporaryDirectory() as directory:
            store = ReliableChatStore(str(Path(directory)/"test.sqlite3"), DomainCipher(b"e"*32))
            data = {"text": "用户喜欢豆浆", "enabled": True, "subject": "user", "weight": 6, "stage": "formal", "truth_status": "accepted"}
            store.put_fact("owner", "known", Domain.USER_FACT, data, expected_revision=0)
            store.accept([ChatCommand("owner", "r", "c", "d", "之前说错了，我喜欢牛奶")], memory_first=True)
            job = store.claim(kinds={"extract"})
            candidates = store.extraction_candidates(job)
            fact = {"text": "用户喜欢牛奶", "evidence": "我喜欢牛奶", "subject": "user", "assertion": "explicit", "operation": "correct", "fact_id": "known", "expected_revision": 2}
            generator, create = self.generator({"facts": [fact]})
            response = create.return_value
            async def change(**kwargs):
                store.put_fact("owner", "known", Domain.USER_FACT, {**data, "text": "用户喜欢水"}, expected_revision=1)
                return response
            create.side_effect = change
            facts = await generator.extract_with_context("之前说错了，我喜欢牛奶", candidates, NOW)
            self.assertEqual(facts[0]["expected_revision"], 1)
            with self.assertRaises(VersionConflict):
                store.commit_extraction(job, facts)
            self.assertEqual(store.read_fact("owner", "known")["data"]["text"], "用户喜欢水")

    async def test_legacy_extract_entry_point_still_returns_an_array(self):
        generator, _ = self.generator([])
        self.assertEqual(await generator.extract("没有明确事实"), [])
