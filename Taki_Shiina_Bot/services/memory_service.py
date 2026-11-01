import datetime
import copy
import logging
import os

from secure_storage import SecureJsonStore
from services.privacy import redact_locations
from services.permission_guard import PermissionGuard
from services.foundation_store import PermissionDenied


class MemoryService:
    def __init__(self, base_dir):
        self.logger = logging.getLogger(__name__)
        self.base_dir = os.path.abspath(base_dir)
        self.permission_guard = PermissionGuard(self.base_dir)
        self.db_file = os.path.join(base_dir, "user_memories.json")
        self.data = {}
        self.store = SecureJsonStore(self.db_file, self.logger)
        self.load()

    def load(self):
        self.data = self.store.load_strict({})

    def save(self):
        self.store.save_strict(self.data)

    def get_profile(self, user_id):
        user_id = str(user_id)
        if not self.permission_guard.owner_context_allowed(user_id):
            return ""
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
        if not self.permission_guard.owner_context_allowed(user_id):
            raise PermissionDenied("Unmapped legacy memory updates are disabled for this owner")
        raw_data = self.data.get(user_id, [])

        if isinstance(raw_data, str):
            if raw_data == "暂无特定情报。":
                memory_list = []
            else:
                memory_list = raw_data.split("\n")
        else:
            memory_list = list(raw_data)

        date_str = datetime.datetime.now().strftime("%Y-%m-%d")
        entry = f"[{date_str}] {redact_locations(new_info)}"

        memory_list.append(entry)

        updated = copy.deepcopy(self.data)
        updated[user_id] = memory_list
        self.store.save_strict(updated)
        self.data = updated
        self.logger.info("记忆更新已提交")
