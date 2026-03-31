from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from Taki_Shiina_Bot.core.config import settings
from Taki_Shiina_Bot.api.v1.health import router as health_router
from Taki_Shiina_Bot.api.v1.auth import router as auth_router

app = FastAPI(title=settings.APP_NAME)

app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.CORS_ORIGINS,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(health_router, prefix=settings.API_PREFIX, tags=["health"])
app.include_router(auth_router, prefix=settings.API_PREFIX, tags=["auth"])