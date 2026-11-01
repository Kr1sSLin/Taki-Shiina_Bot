import logging

from secure_storage import SecureJsonStore


class HistoryStore:
    def __init__(self, history_file):
        self.history_file = history_file
        self.logger = logging.getLogger(__name__)
        self.store = SecureJsonStore(self.history_file, self.logger)

    def load(self):
        return self.store.load_strict({})

    def save(self, history_data):
        self.store.save_strict(history_data)
