"""Separate semantic evaluation; never confuse fixtures with live model results.

Use --predictions for offline evaluation, or --run-model with an explicitly
configured DEEPSEEK_API_KEY. Only the synthetic corpus is sent to the provider.
"""
import argparse
import asyncio
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Taki_Shiina_Bot"))
from services.memory_policy import memory_control, normalized, timestamp


def matches(actual, expected):
    if not isinstance(actual, dict):
        return False
    for field, value in expected.items():
        if field == "accepted_texts":
            continue
        if field == "text":
            variants = [value, *expected.get("accepted_texts", [])]
            if normalized(actual.get("text", "")) not in {normalized(text) for text in variants}:
                return False
        elif field == "temporal_validity":
            for boundary, date in value.items():
                try:
                    if timestamp((actual.get(field) or {}).get(boundary)) != timestamp(date):
                        return False
                except (ValueError, TypeError):
                    return False
        elif actual.get(field, "evidence" if field == "operation" else None) != value:
            return False
    return True


def evaluate(corpus, predictions):
    expected_ids = {case["id"] for case in corpus["cases"]}
    if set(predictions) != expected_ids:
        raise ValueError("predictions must cover each corpus case exactly")
    true_positive = false_positive = false_negative = exact = 0
    disagreements = []
    for case in corpus["cases"]:
        actual = predictions[case["id"]]
        if not isinstance(actual, list):
            raise ValueError("each prediction must be an extraction array")
        remaining = list(actual)
        hits = 0
        for expected in case["expected"]:
            match = next((i for i, item in enumerate(remaining) if matches(item, expected)), None)
            if match is not None:
                remaining.pop(match)
                hits += 1
        missed = len(case["expected"])-hits
        true_positive += hits
        false_positive += len(remaining)
        false_negative += missed
        if not remaining and not missed:
            exact += 1
        else:
            disagreements.append({"case": case["id"], "extra": len(remaining), "missing": missed})
    return {"annotation_status": corpus.get("annotation_status", "unknown"), "cases": len(corpus["cases"]), "exact_cases": exact,
            "precision": true_positive/(true_positive+false_positive) if true_positive+false_positive else None,
            "recall": true_positive/(true_positive+false_negative) if true_positive+false_negative else None,
            "false_positive": false_positive, "false_negative": false_negative, "disagreements": disagreements}


async def run_model(corpus):
    if not os.getenv("DEEPSEEK_API_KEY"):
        raise ValueError("DEEPSEEK_API_KEY is not configured; no model call was made")
    from openai import AsyncOpenAI
    from services.text_generation import TextGenerator
    async with AsyncOpenAI(api_key=os.environ["DEEPSEEK_API_KEY"], base_url="https://api.deepseek.com", max_retries=0, timeout=30) as client:
        extractor = TextGenerator(None, None, None, None, client, None)
        results = {}
        for case in corpus["cases"]:
            candidates = [corpus["candidates"][key] for key in case.get("candidates", [])]
            results[case["id"]] = [] if memory_control(case["input"]) else await asyncio.wait_for(extractor.extract_with_context(case["input"], candidates, timestamp(corpus["now"])), timeout=30)
            print(f"{case['id']} completed", file=sys.stderr)
        return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, default=ROOT / "design" / "memory-semantic-cases.json")
    parser.add_argument("--env-file", type=Path, default=ROOT / "Taki_Shiina_Bot" / ".env",
                        help="Model config for --run-model; existing environment variables take precedence")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--predictions", type=Path)
    source.add_argument("--run-model", action="store_true")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.run_model:
        from dotenv import load_dotenv
        load_dotenv(args.env_file, override=False)
    corpus = json.loads(args.corpus.read_text(encoding="utf-8"))
    predictions = asyncio.run(run_model(corpus)) if args.run_model else json.loads(args.predictions.read_text(encoding="utf-8"))
    report = evaluate(corpus, predictions)
    report["mode"] = "live-model" if args.run_model else "supplied-predictions"
    report["predictions"] = predictions
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "predictions"}, ensure_ascii=True))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"Evaluation did not complete ({type(error).__name__}); verify credentials, input and provider availability.", file=sys.stderr)
        raise SystemExit(2)
