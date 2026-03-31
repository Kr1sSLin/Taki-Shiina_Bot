import json
import logging
import os


class HistoryStore:
    def __init__(self, history_file):
        self.history_file = history_file
        self.logger = logging.getLogger(__name__)

    def load(self):
        if os.path.exists(self.history_file):
            try:
                with open(self.history_file, "r", encoding="utf-8") as f:
                    return json.load(f)
            except Exception as e:
                self.logger.error(f"⚠️ 读取历史记录失败: {e}")
        return {}

    def save(self, history_data):
        try:
            with open(self.history_file, "w", encoding="utf-8") as f:
                json.dump(history_data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            self.logger.error(f"❌ 保存历史失败: {e}")
