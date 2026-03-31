import random
import re

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


def sanitize_taki_reply(text):
    taki_fillers = ["哈？", "喂", "啧", "受不了你", "麻烦死了", "拿你没办法", "服了你了", "唉……", "笨蛋", ""]

    # 先过滤动作叙事，减少模型输出中不符合聊天体的描述。
    text = re.sub(r'\*[^*\n]+\*', '', text)
    text = re.sub(r'（[^）\n]{2,}）', '', text)
    text = re.sub(r'\([^)\n]*[\u4e00-\u9fff][^)\n]*\)', '', text)
    text = re.sub(r'\n{3,}', '\n\n', text).strip()

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
