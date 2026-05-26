import datetime
import logging
import os

from secure_storage import SecureJsonStore


class MemoryService:
    def __init__(self, base_dir):
        self.logger = logging.getLogger(__name__)
        self.db_file = os.path.join(base_dir, "user_memories.json")
        self.data = {}
        self.store = SecureJsonStore(self.db_file, self.logger)
        self.load()

    def load(self):
        if not os.path.exists(self.db_file):
            self.data = {}
            self.save()
            return
        try:
            self.data = self.store.load({})
        except Exception as e:
            self.logger.error(f"无法读取记忆文件: {e}")
            self.data = {}

    def save(self):
        try:
            self.store.save(self.data)
        except Exception as e:
            self.logger.error(f"无法保存记忆文件: {e}")

    def get_profile(self, user_id):
        user_id = str(user_id)
        raw_data = self.data.get(user_id, [])

        if not raw_data:
            return "暂无特定情报。"

        if isinstance(raw_data, str):
            return raw_data

        if isinstance(raw_data, list):
            return "\n".join(raw_data)

        return "暂无特定情报。"

    def update_profile(self, user_id, new_info):
        user_id = str(user_id)
        raw_data = self.data.get(user_id, [])

        if isinstance(raw_data, str):
            if raw_data == "暂无特定情报。":
                memory_list = []
            else:
                memory_list = raw_data.split("\n")
        else:
            memory_list = raw_data

        date_str = datetime.datetime.now().strftime("%Y-%m-%d")
        entry = f"[{date_str}] {new_info}"

        memory_list.append(entry)

        self.data[user_id] = memory_list
        self.save()
        self.logger.info(f"💾 记忆已更新: {entry}")
