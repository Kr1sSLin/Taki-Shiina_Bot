from fastapi import APIRouter
from pydantic import BaseModel
from Taki_Shiina_Bot.core.response import ok, err

router = APIRouter()

class LoginReq(BaseModel):
    username: str
    password: str

@router.post("/auth/login")
def login(body: LoginReq):
    if not body.username or not body.password:
        return err("username/password required", 40001)

    # TODO: 替换为你真实登录逻辑
    return ok({
        "access_token": "mock_access_token",
        "refresh_token": "mock_refresh_token",
        "token_type": "Bearer"
    })