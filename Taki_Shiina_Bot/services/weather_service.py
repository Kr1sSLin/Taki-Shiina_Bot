import datetime
import os

import httpx


class WeatherService:
    def __init__(self, base_dir, api_key, my_lat, my_lon, api_host=None):
        self.base_dir = base_dir
        self.api_key = api_key
        self.my_lat = my_lat
        self.my_lon = my_lon
        self.api_host = api_host or os.getenv("QWEATHER_API_HOST", "").strip()
        self.cache = {"info": "未知", "time": 0}
        self.location_cache = {"loc": None, "id": None}

    @property
    def city_file(self):
        return os.path.join(self.base_dir, "current_city.txt")

    def get_current_city(self):
        if os.path.exists(self.city_file):
            with open(self.city_file, "r", encoding="utf-8") as f:
                return f.read().strip()
        return "auto_ip"

    async def _resolve_location_id(self, loc: str) -> str | None:
        if self.location_cache.get("loc") == loc and self.location_cache.get("id"):
            return self.location_cache["id"]

        parts = loc.split(",")
        if len(parts) == 2:
            lon, lat = parts[0].strip(), parts[1].strip()
            location_id = f"{lon},{lat}"
            self.location_cache = {"loc": loc, "id": location_id}
            return location_id

        async with httpx.AsyncClient() as client:
            resp = await client.get(
                f"https://{self.api_host}/geo/v2/city/lookup",
                params={"location": loc, "key": self.api_key},
                timeout=5.0,
            )
            data = resp.json()
            if data.get("code") == "200" and data.get("location"):
                location_id = data["location"][0]["id"]
                self.location_cache = {"loc": loc, "id": location_id}
                return location_id
        return None

    async def get_weather_str(self, force: bool = False):
        now = datetime.datetime.now().timestamp()
        if not force and now - self.cache["time"] < 1800 and self.cache["info"] != "未知":
            return self.cache["info"]

        if not self.api_host:
            return "【当前天气】：未配置 QWEATHER_API_HOST"

        try:
            loc = self.get_current_city()
            location_id = await self._resolve_location_id(loc)
            if not location_id:
                return "【当前天气】：未找到该城市"

            async with httpx.AsyncClient() as client:
                resp = await client.get(
                    f"https://{self.api_host}/v7/weather/now",
                    params={"location": location_id, "key": self.api_key},
                    timeout=5.0,
                )
                data = resp.json()
                if data.get("code") == "200" and data.get("now"):
                    now_data = data["now"]
                    d = now_data.get("text", "未知")
                    t = int(now_data.get("temp", 0))
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
        self.location_cache = {"loc": None, "id": None}
