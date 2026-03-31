import os

class Settings:
    APP_NAME = "TakiShiina-Bot API"
    API_PREFIX = "/api/v1"
    CORS_ORIGINS = os.getenv(
        "CORS_ORIGINS",
        "http://localhost:5173,http://127.0.0.1:5173"
    ).split(",")

settings = Settings()