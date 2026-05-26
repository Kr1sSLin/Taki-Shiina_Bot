import logging

from secure_storage import SecureJsonStore


class HistoryStore:
    def __init__(self, history_file):
        self.history_file = history_file
        self.logger = logging.getLogger(__name__)
        self.store = SecureJsonStore(self.history_file, self.logger)

    def load(self):
        try:
            return self.store.load({})
        except Exception as e:
            self.logger.error(f"⚠️ 读取历史记录失败: {e}")
            return {}

    def save(self, history_data):
        try:
            self.store.save(history_data)
        except Exception as e:
            self.logger.error(f"❌ 保存历史失败: {e}")
