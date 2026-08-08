import random
import re
from datetime import datetime, timedelta, timezone
from difflib import SequenceMatcher

from app_constants import KEYWORD_TO_EMOJI


def inject_emojis(text):
    if random.random() < 0.90:
        text = re.sub(r'^[…。\.\s]+', '', text)

    emoji_pattern = re.compile(r'[\U0001F600-\U0001F64F\U0001F300-\U0001F5FF\U0001F680-\U0001F6FF\U0001F900-\U0001F9FF\u2600-\u27BF\u2300-\u23FF]+')
    clean_text = emoji_pattern.sub('', text).strip()

    final_text = clean_text

    for _, data in KEYWORD_TO_EMOJI.items():
        for word in data["words"]:
            if word in clean_text:
                if random.random() < 0.15:
                    emoji = random.choice(data["emojis"])
                    final_text += f" {emoji}"
                    return final_text
    return final_text


_TIME_STAMP_TOKEN_RE = re.compile(
    r'[【\[\(（]\s*'
    r'(?:'
    r'(?:\d{1,4}[年月/.-]\d{1,2}(?:[月/.-]\d{1,2})?日?(?:\s*\d{1,2}:\d{2})?)'  # 日期(+时间)
    r'|'
    r'\d{1,2}:\d{2}'  # 纯时间
    r')'
    r'\s*[】\]\)）]'
)


def sanitize_taki_reply(text):
    taki_fillers = ["哈？", "喂", "啧", "受不了你", "麻烦死了", "拿你没办法", "服了你了", "唉……", "笨蛋", ""]

    # 先过滤动作叙事，减少模型输出中不符合聊天体的描述。
    text = re.sub(r'\*[^*\n]+\*', '', text)
    text = re.sub(r'（[^）\n]{2,}）', '', text)
    text = re.sub(r'\([^)\n]*[\u4e00-\u9fff][^)\n]*\)', '', text)
    text = re.sub(r'\n{3,}', '\n\n', text).strip()

    # 剥离模型模仿输出的时间戳，如 [8-22 17:22]、【08-22 17:22】、[17:22]
    text = _TIME_STAMP_TOKEN_RE.sub('', text).lstrip()

    if "真是的" in text and random.random() < 0.95:
        text = text.replace("真是的", random.choice(taki_fillers), 1)

    text = re.sub(r'啧', lambda _: random.choice(taki_fillers), text)

    if text.startswith("……") and random.random() < 0.90:
        text = text[1:].lstrip("…。. ")

    def strip_line_ellipsis(line):
        if re.match(r'^[…]+', line) and random.random() < 0.80:
            return re.sub(r'^[…]+\s*', '', line)
        return line

    text = '\n'.join(strip_line_ellipsis(line) for line in text.split('\n'))

    while "………" in text:
        text = text.replace("………", "……")

    text = text.replace("……。", "……")
    text = text.replace("。。", "。")
    text = text.replace("！！", "！")
    text = text.replace("？？", "？")

    return text


def _normalize_for_similarity(text: str) -> str:
    if not text:
        return ""
    collapsed = re.sub(r"\s+", "", text)
    return collapsed.strip()


def is_repetitive_reply(candidate: str, recent_replies: list[str], threshold: float = 0.88) -> bool:
    cand = _normalize_for_similarity(candidate)
    if not cand:
        return False
    for reply in recent_replies[-10:]:
        base = _normalize_for_similarity(reply)
        if not base:
            continue
        score = SequenceMatcher(None, cand, base).ratio()
        if score >= threshold:
            return True
    return False


def detect_polluted_tails(recent_replies: list[str], min_repeat: int = 3) -> list[str]:
    counts: dict[str, int] = {}
    canonical: dict[str, str] = {}
    for reply in recent_replies:
        lines = [line.strip() for line in (reply or "").split("\n") if line.strip()]
        if not lines:
            continue
        candidates = [lines[-1]]
        if len(lines) >= 2:
            candidates.append(f"{lines[-2]}\n{lines[-1]}")
        for tail in candidates:
            norm = _normalize_for_similarity(tail)
            if len(norm) < 8:
                continue
            counts[norm] = counts.get(norm, 0) + 1
            canonical.setdefault(norm, tail)

    polluted = [canonical[norm] for norm, cnt in counts.items() if cnt >= min_repeat]
    polluted.sort(key=len, reverse=True)
    return polluted


def strip_polluted_tail(text: str, polluted_tails: list[str]) -> str:
    cleaned = (text or "").rstrip()
    if not cleaned or not polluted_tails:
        return cleaned

    changed = True
    while changed:
        changed = False
        for tail in polluted_tails:
            tail_norm = (tail or "").strip()
            if not tail_norm:
                continue
            if cleaned.endswith(tail_norm):
                cleaned = cleaned[: -len(tail_norm)].rstrip()
                changed = True
                break
    return cleaned


def clean_short_term_history(messages: list[dict], min_repeat: int = 3) -> tuple[list[dict], list[str]]:
    assistant_replies = [m.get("content", "") for m in messages if m.get("role") == "assistant"]
    polluted_tails = detect_polluted_tails(assistant_replies, min_repeat=min_repeat)
    if not polluted_tails:
        return messages, []

    cleaned_messages: list[dict] = []
    for m in messages:
        if m.get("role") != "assistant":
            cleaned_messages.append(m)
            continue
        content = strip_polluted_tail(m.get("content", ""), polluted_tails)
        if content.strip():
            cleaned = dict(m)
            cleaned["content"] = content
            cleaned_messages.append(cleaned)
    return cleaned_messages, polluted_tails


def with_history_timestamp(m: dict) -> dict:
    """history 消息带 ts 时,在 content 前加北京时间时间戳,否则原样返回"""
    ts = m.get("ts")
    if not ts or m.get("role") not in ("user", "assistant"):
        return m
    beijing = datetime.fromtimestamp(ts / 1000, timezone(timedelta(hours=8)))
    out = dict(m)
    out["content"] = f"【{beijing.strftime('%m-%d %H:%M')}】{m['content']}"
    return out
