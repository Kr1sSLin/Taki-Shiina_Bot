import datetime
import os
from datetime import date, timedelta, timezone


def get_current_time_str():
    utc_now = datetime.datetime.now(timezone.utc)
    beijing_time = utc_now + timedelta(hours=8)
    return f"【时间】：{beijing_time.strftime('%H:%M')}"


# ================= 业务自然日（积分/等级体系专用） =================
# PRD FR-10 / EDGE-3：断签判定、每日首次对话等按“自然日”计算的规则统一以业务时区为基准，
# 不做用户设备时区适配。项目现有代码（早晚安推送、历史分隔）统一使用 UTC+8 北京时间，
# 因此业务时区默认同样取 UTC+8，可通过 BIZ_TZ_OFFSET_HOURS 覆盖。

def get_business_tz_offset_hours() -> float:
    raw = (os.getenv("BIZ_TZ_OFFSET_HOURS", "8") or "8").strip()
    try:
        return float(raw)
    except ValueError:
        return 8.0


def get_business_now() -> datetime.datetime:
    return datetime.datetime.now(timezone.utc) + timedelta(hours=get_business_tz_offset_hours())


def get_business_today() -> date:
    return get_business_now().date()


def get_business_today_str() -> str:
    return get_business_today().isoformat()


def get_business_month_str() -> str:
    return get_business_today().strftime("%Y-%m")


def business_date_str(value: date) -> str:
    return value.isoformat()


def parse_business_date(raw: str) -> date:
    """解析 YYYY-MM-DD（非法输入抛 ValueError）。"""
    text = (raw or "").strip()
    if len(text) != 10:
        raise ValueError("日期格式应为 YYYY-MM-DD")
    return datetime.date.fromisoformat(text)


def business_date_from_ms(timestamp_ms: int) -> date:
    utc_dt = datetime.datetime.fromtimestamp(int(timestamp_ms) / 1000, tz=timezone.utc)
    return (utc_dt + timedelta(hours=get_business_tz_offset_hours())).date()


def iter_dates(start: date, end: date):
    """含头含尾遍历日期。"""
    cursor = start
    while cursor <= end:
        yield cursor
        cursor += timedelta(days=1)


# ================= 时段判定（服务端算好、直接注入 prompt） =================
# 背景：模型没有时钟，只会读我们拼进 messages 的文本。与其让模型自己按 HH:MM 猜时段，
# 不如在服务端判好并明确告诉它「系统已判定」，措辞交给模型即可（确定性更强、边界不会飘）。

PERIOD_DEEP_NIGHT = "深夜"
PERIOD_EARLY_MORNING = "清晨"
PERIOD_MORNING = "上午"
PERIOD_AFTERNOON = "下午"
PERIOD_EVENING = "晚上"


def describe_business_period(moment: datetime.datetime | None = None) -> str:
    """按业务时区（默认 UTC+8）返回时段标签。

    区间：深夜 23:00–04:59 / 清晨 05:00–08:59 / 上午 09:00–11:59 /
    下午 12:00–17:59 / 晚上 18:00–22:59
    """
    now = moment or get_business_now()
    hour = now.hour
    if hour >= 23 or hour < 5:
        return PERIOD_DEEP_NIGHT
    if hour < 9:
        return PERIOD_EARLY_MORNING
    if hour < 12:
        return PERIOD_MORNING
    if hour < 18:
        return PERIOD_AFTERNOON
    return PERIOD_EVENING


def build_time_block(moment: datetime.datetime | None = None) -> str:
    """注入 prompt 的两行时间块（普通聊天 / 互动 / 早晚安问候共用同一口径）。"""
    now = moment or get_business_now()
    return (
        f"【当前北京时间】：{now.strftime('%H:%M')}\n"
        f"【当前时段】：{describe_business_period(now)}（系统已判定，直接采用，不要再自行推断时段）"
    )


def next_hour_boundary(moment: datetime.datetime | None = None) -> datetime.datetime:
    """下一个整点（用于心情缓存的有效期）。"""
    now = moment or datetime.datetime.now(timezone.utc)
    return (now + timedelta(hours=1)).replace(minute=0, second=0, microsecond=0)
