import random
import logging
import os
import sys
from datetime import timedelta, timezone, time

# 第三方库
from google import genai as genai_client
from telegram import Update
from telegram.ext import ApplicationBuilder, ContextTypes, MessageHandler, filters, CommandHandler
from openai import AsyncOpenAI
import httpx
from dotenv import load_dotenv
from app_state import AppState
from app_constants import EMOTIONAL_TRIGGERS, LORE_TRIGGERS, USER_MEMO
from handlers.chat_handler import create_chat_handler
from handlers.basic_handlers import (
    create_check_memory_command,
    create_handle_photo_handler,
    create_set_city_command,
)
from jobs.scheduler_jobs import (
    create_active_greeting_job,
    create_hourly_scene_update,
    create_notify_owner,
    create_reminder_job,
    create_schedule_random_greeting,
    create_startup_notification,
)
from services.memory_service import MemoryService
from services.history_store import HistoryStore
from services.prompt_service import PromptService
from services.weather_service import WeatherService
from text_utils import inject_emojis, sanitize_taki_reply

# ================= 1. 配置区域 =================
_base_dir = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_base_dir, '.env'))

TELEGRAM_TOKEN = os.getenv("TELEGRAM_TOKEN")
OWNER_ID = int(os.getenv("OWNER_ID", "0"))
DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY")
GOOGLE_API_KEY = os.getenv("GOOGLE_API_KEY")
OPENWEATHER_API_KEY = os.getenv("OPENWEATHER_API_KEY")
MY_LAT = float(os.getenv("MY_LAT", "0"))  # 纬度
MY_LON = float(os.getenv("MY_LON", "0"))  # 经度
MONITOR_BOT_TOKEN = os.getenv("MONITOR_BOT_TOKEN")

# ================= 2. 初始化日志 =================
logging.basicConfig(
    format='%(asctime)s - %(levelname)s - %(message)s',
    level=logging.INFO,
    stream=sys.stdout
)
logger = logging.getLogger(__name__)

# ================= 3. 应用状态与服务 =================
state = AppState(history_file=os.path.join(_base_dir, "chat_history.json"))
db = MemoryService(_base_dir)
weather_service = WeatherService(_base_dir, OPENWEATHER_API_KEY, MY_LAT, MY_LON)
history_store = HistoryStore(state.history_file)

# 兼容旧调用点：后续阶段可继续将这些别名完全替换为 state.xxx
user_chat_history = state.user_chat_history
message_buffer = state.message_buffer
debounce_jobs = state.debounce_jobs
last_activity = state.last_activity
last_bot_response_time = state.last_bot_response_time

DEBOUNCE_BASE = 8.0       # 默认防抖窗口（秒）
DEBOUNCE_EXTENDED = 20.0  # 立希刚回复后的扩展防抖窗口（秒）
DEBOUNCE_EXTEND_WINDOW = 40.0  # 立希回复后多少秒内算"刚回复"

# 全局场景缓存：{user_id: {"scene": str, "expires_at": datetime}}
_scene_cache = state.scene_cache

# ================= 5. 持久化包装 =================
load_chat_history = history_store.load

def save_chat_history():
    history_store.save(user_chat_history)

# ================= 6. 模型客户端初始化 =================
client = AsyncOpenAI(
    api_key=DEEPSEEK_API_KEY, 
    base_url="https://api.deepseek.com",
    timeout=httpx.Timeout(120.0, connect=60.0)
)
if GOOGLE_API_KEY and GOOGLE_API_KEY != "你的Key":
    _genai_client = genai_client.Client(api_key=GOOGLE_API_KEY.strip())
else:
    _genai_client = None

# ================= 8. 辅助功能 (天气、指令) =================
async def get_weather_str():
    return await weather_service.get_weather_str()

SCENE_CHAT_IDLE_SECONDS = 300  # 超过5分钟没发消息视为聊天已结束

set_city_command = create_set_city_command(weather_service)
check_memory_command = create_check_memory_command(db)
handle_photo = create_handle_photo_handler(
    OWNER_ID,
    user_chat_history,
    _genai_client,
    sanitize_taki_reply,
    inject_emojis,
    logger,
)

notify_owner = create_notify_owner(MONITOR_BOT_TOKEN, OWNER_ID, logger)
startup_notification = create_startup_notification(notify_owner)
reminder_job = create_reminder_job(logger)

prompt_service = PromptService(
    client,
    db,
    notify_owner,
    _scene_cache,
    USER_MEMO,
    logger,
)
get_system_prompt = prompt_service.get_system_prompt
generate_scene = prompt_service.generate_scene

active_greeting_job = create_active_greeting_job(
    last_activity,
    notify_owner,
    get_system_prompt,
    client,
    sanitize_taki_reply,
    inject_emojis,
    user_chat_history,
    save_chat_history,
    logger,
)
schedule_random_greeting = create_schedule_random_greeting(
    notify_owner,
    active_greeting_job,
    logger,
)
hourly_scene_update = create_hourly_scene_update(
    SCENE_CHAT_IDLE_SECONDS,
    last_activity,
    generate_scene,
    _scene_cache,
    logger,
)

chat = create_chat_handler(
    OWNER_ID,
    notify_owner,
    message_buffer,
    debounce_jobs,
    last_activity,
    last_bot_response_time,
    DEBOUNCE_BASE,
    DEBOUNCE_EXTENDED,
    DEBOUNCE_EXTEND_WINDOW,
    user_chat_history,
    save_chat_history,
    client,
    get_system_prompt,
    weather_service.get_weather_str,
    EMOTIONAL_TRIGGERS,
    LORE_TRIGGERS,
    reminder_job,
    db,
    sanitize_taki_reply,
    inject_emojis,
    logger,
)

# ================= 9. 启动区 =================
if __name__ == '__main__':
    loaded_history = load_chat_history()
    state.user_chat_history.clear()
    state.user_chat_history.update(loaded_history)
    application = ApplicationBuilder().token(TELEGRAM_TOKEN).build()
    
    application.add_handler(CommandHandler("city", set_city_command)) 
    application.add_handler(CommandHandler("memo", check_memory_command)) 
    application.add_handler(MessageHandler(filters.TEXT & (~filters.COMMAND), chat)) 
    application.add_handler(MessageHandler(filters.PHOTO, handle_photo)) 
    
    job_queue = application.job_queue
    if job_queue and OWNER_ID:
        beijing_tz = timezone(timedelta(hours=8))

        job_queue.run_daily(
            schedule_random_greeting,
            time=time(hour=9, minute=0, tzinfo=beijing_tz),
            chat_id=OWNER_ID,
            data={"scenario": "morning", "window_minutes": 180}
        )

        job_queue.run_daily(
            schedule_random_greeting,
            time=time(hour=23, minute=0, tzinfo=beijing_tz),
            chat_id=OWNER_ID,
            data={"scenario": "night", "window_minutes": 120}
        )

        job_queue.run_once(startup_notification, when=3, chat_id=OWNER_ID)

        # 🕐 每小时整点推送场景（北京时间 00:00 ~ 23:00，共24个）
        for hour in range(24):
            job_queue.run_daily(
                hourly_scene_update,
                time=time(hour=hour, minute=0, second=0, tzinfo=beijing_tz),
                chat_id=OWNER_ID,
            )

    print("👀 立希 v1.4.0 已就位...")
    application.run_polling()

#教 你 打 维 护 代 码
#sudo systemctl restart mybot              重启机器人
#sudo systemctl status mybot               查看运行状态
#sudo systemctl stop mybot                 停止机器人
#sudo journalctl -u mybot -f               实时监控日志
#sudo journalctl -u mybot -n 50 --no-pager 查看最后50行日志
#sudo journalctl -u mybot -p err           只显示红色报错信息

#教 你 常 用 快 捷 键
#Ctrl + O 保存
#Ctrl + X 退出
#Ctrl + K 剪切/删除整行
#Ctrl + W 搜索
