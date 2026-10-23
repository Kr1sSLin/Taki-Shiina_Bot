from copy import deepcopy
from contextlib import redirect_stdout
import asyncio
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import AsyncMock, patch

from dotenv.main import load_dotenv as real_load_dotenv
from scripts.evaluate_memory_extraction import evaluate, main, run_model


class MemoryEvaluationTests(unittest.TestCase):
    def test_saved_raw_predictions_replay_current_extractor_contract_offline(self):
        root = Path(__file__).resolve().parents[2]
        corpus = json.loads((root / "design/memory-semantic-cases.json").read_text(encoding="utf-8"))
        saved = json.loads((root / "design/memory-fix-validation-2026-10-20.json").read_text(encoding="utf-8"))["semantic"]
        identities = {case["input"]: case["id"] for case in corpus["cases"]}
        async def respond(**kwargs):
            value = json.loads(kwargs["messages"][-1]["content"])
            payload = saved["raw_model_predictions"][identities[value["current_input"]]]
            return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps(payload)))])
        create = AsyncMock(side_effect=respond)
        client = SimpleNamespace(chat=SimpleNamespace(completions=SimpleNamespace(create=create)))
        context = AsyncMock()
        context.__aenter__.return_value = client
        with patch("openai.AsyncOpenAI", return_value=context), patch.dict(os.environ, {"DEEPSEEK_API_KEY": "synthetic-key"}):
            predictions = asyncio.run(run_model(corpus))
        self.assertEqual(create.await_count, 12)
        self.assertEqual(predictions["S13"], [])
        report = evaluate(corpus, predictions)
        self.assertEqual(report["exact_cases"], 13)
        self.assertEqual((report["false_positive"], report["false_negative"]), (0, 0))

    def test_cli_loads_configured_env_file_without_making_real_model_calls(self):
        with tempfile.TemporaryDirectory(prefix="tks-eval-config-") as directory:
            root = Path(directory)
            config, corpus, output = root / "model.env", root / "cases.json", root / "report.json"
            config.write_text("DEEPSEEK_API_KEY=synthetic-evaluation-key\nDEEPSEEK_MODEL=synthetic-model\n", encoding="utf-8")
            corpus.write_text(json.dumps({"cases": [{"id": "synthetic", "expected": []}]}), encoding="utf-8")
            async def model_double(_corpus):
                self.assertEqual(os.environ["DEEPSEEK_API_KEY"], "synthetic-evaluation-key")
                self.assertEqual(os.environ["DEEPSEEK_MODEL"], "synthetic-model")
                return {"synthetic": []}
            argv = ["evaluate_memory_extraction.py", "--run-model", "--env-file", str(config), "--corpus", str(corpus), "--output", str(output)]
            with patch.dict(os.environ), patch("sys.argv", argv), redirect_stdout(io.StringIO()):
                os.environ.pop("DEEPSEEK_API_KEY", None)
                os.environ.pop("DEEPSEEK_MODEL", None)
                with patch("dotenv.load_dotenv", side_effect=real_load_dotenv), patch("scripts.evaluate_memory_extraction.run_model", new=AsyncMock(side_effect=model_double)):
                    main()
            self.assertEqual(json.loads(output.read_text(encoding="utf-8"))["exact_cases"], 1)

    def test_invented_user_facts_and_missing_predictions_are_failures(self):
        corpus = {"annotation_status": "draft", "cases": [{"id": "friend", "expected": []}, {"id": "user", "expected": [{"text": "用户喜欢咖啡", "subject": "user"}]}]}
        report = evaluate(corpus, {"friend": [{"text": "用户喜欢咖啡", "subject": "user"}], "user": []})
        self.assertEqual(report["exact_cases"], 0)
        self.assertEqual((report["false_positive"], report["false_negative"]), (1, 1))
        with self.assertRaises(ValueError):
            evaluate(corpus, {"user": []})

    def test_wrong_subject_time_or_identity_cannot_be_counted_as_a_match(self):
        expected = {"text": "用户旅行", "subject": "user", "fact_id": "trip-1", "temporal_validity": {"from": "2026-10-17T00:00:00+08:00"}}
        corpus = {"cases": [{"id": "trip", "expected": [expected]}]}
        for changed in ({"subject": "friend"}, {"fact_id": "trip-2"}, {"temporal_validity": {"from": "2026-10-18T00:00:00+08:00"}}):
            with self.subTest(changed=changed):
                report = evaluate(corpus, {"trip": [{**deepcopy(expected), **changed}]})
                self.assertEqual(report["exact_cases"], 0)
