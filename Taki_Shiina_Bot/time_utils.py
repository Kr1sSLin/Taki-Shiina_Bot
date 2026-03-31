import datetime
from datetime import timedelta, timezone


def get_current_time_str():
    utc_now = datetime.datetime.now(timezone.utc)
    beijing_time = utc_now + timedelta(hours=8)
    return f"【时间】：{beijing_time.strftime('%H:%M')}"
