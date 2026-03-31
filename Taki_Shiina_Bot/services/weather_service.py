import datetime
import os

import httpx


class WeatherService:
    def __init__(self, base_dir, api_key, my_lat, my_lon):
        self.base_dir = base_dir
        self.api_key = api_key
        self.my_lat = my_lat
        self.my_lon = my_lon
        self.cache = {"info": "未知", "time": 0}

    @property
    def city_file(self):
        return os.path.join(self.base_dir, "current_city.txt")

    def get_current_city(self):
        if os.path.exists(self.city_file):
            with open(self.city_file, "r", encoding="utf-8") as f:
                return f.read().strip()
        return f"{self.my_lat},{self.my_lon}"

    async def get_weather_str(self):
        now = datetime.datetime.now().timestamp()
        if now - self.cache["time"] < 1800 and self.cache["info"] != "未知":
            return self.cache["info"]

        try:
            loc = self.get_current_city()
            url = (
                "http://api.openweathermap.org/data/2.5/weather"
                f"?q={loc}&appid={self.api_key}&units=metric&lang=zh_cn"
            )
            if "," in loc:
                parts = loc.split(",")
                url = (
                    "http://api.openweathermap.org/data/2.5/weather"
                    f"?lat={parts[0].strip()}&lon={parts[1].strip()}"
                    f"&appid={self.api_key}&units=metric&lang=zh_cn"
                )

            async with httpx.AsyncClient() as client:
                resp = await client.get(url, timeout=5.0)
                data = resp.json()
                if data.get("cod") == 200:
                    d = data["weather"][0]["description"]
                    t = int(data["main"]["temp"])
                    info = f"【当前天气】：{d}，气温{t}°C。"
                    self.cache = {"info": info, "time": now}
                    return info
                return "【当前天气】：获取失败"
        except Exception:
            return "【当前天气】：暂时无法获取"

    def set_city(self, city):
        with open(self.city_file, "w", encoding="utf-8") as f:
            f.write(city)
        self.cache = {"info": "未知", "time": 0}
