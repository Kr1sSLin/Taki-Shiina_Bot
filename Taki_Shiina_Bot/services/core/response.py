from typing import Any

def ok(data: Any = None, message: str = "ok", code: int = 0):
    return {"code": code, "message": message, "data": data}

def err(message: str = "error", code: int = 40000, data: Any = None):
    return {"code": code, "message": message, "data": data}