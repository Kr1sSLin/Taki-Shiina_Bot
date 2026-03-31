from fastapi import APIRouter
from Taki_Shiina_Bot.core.response import ok

router = APIRouter()

@router.get("/health")
def health():
    return ok({"status": "up"})