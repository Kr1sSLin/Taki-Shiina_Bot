class AppState:
    def __init__(self, history_file="chat_history.json"):
        self.history_file = history_file

        self.user_chat_history = {}
        self.message_buffer = {}
        self.debounce_jobs = {}
        self.last_activity = {}
        self.last_bot_response_time = {}

        # 场景缓存: {user_id: {"scene": str, "expires_at": datetime}}
        self.scene_cache = {}
