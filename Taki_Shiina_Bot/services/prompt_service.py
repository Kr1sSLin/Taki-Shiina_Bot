"""Versioned persona. Legacy scene endpoints cannot invent current activities."""
from services.public_policy import PERSONA, PERSONA_VERSION, RELATIONSHIP_VERSION
from services.permission_guard import PermissionGuard


class PromptService:
    def __init__(self, client, memory_service, notify_owner, scene_cache, user_memo, logger):
        self.client = client
        self.memory_service = memory_service
        self.notify_owner = notify_owner
        self.scene_cache = scene_cache
        self.user_memo = user_memo
        self.logger = logger
        self.permission_guard = getattr(memory_service, "permission_guard", None) or PermissionGuard(getattr(memory_service, "base_dir", None))

    def invalidate_scene(self, user_id):
        self.scene_cache.pop(user_id, None)
        self.scene_cache.pop(str(user_id), None)

    async def generate_scene(self, user_id):
        # Old schedulers/admin endpoints cannot invent an unrecorded activity.
        self.permission_guard.owner_context_allowed(user_id)
        self.invalidate_scene(user_id)
        return ""

    async def get_random_scene(self, user_id):
        return await self.generate_scene(user_id)

    async def refresh_scene(self, user_id):
        return await self.generate_scene(user_id)

    async def get_system_prompt(self, user_id, *, allow_private_context=True):
        self.permission_guard.owner_context_allowed(user_id)
        self.invalidate_scene(user_id)
        # Unmapped legacy profile/memo/scene prose is not qualified evidence.
        return (f"persona_version={PERSONA_VERSION}\nrelationship_version={RELATIONSHIP_VERSION}\n{PERSONA}\n"
                "用户事实、官方剧情和原创日常仅使用本轮提供的获准来源。缺少依据时自然接话或简短澄清。\n"
                "提醒指令格式为 [[TIMER:HH:MM|提醒语]]，HH:MM 为北京时间；没有实际执行依据不能声称已经设置成功。")
