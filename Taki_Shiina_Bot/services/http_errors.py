import uuid

from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException

from services.input_budget import edit_input, error_data


def install_http_errors(app):
    @app.exception_handler(RequestValidationError)
    async def validation_error(request, error):
        # 保留 detail 列表，去掉 Pydantic 回显的原始输入和上下文。
        detail = [{key: item[key] for key in ("loc", "msg", "type") if key in item} for item in error.errors()]
        return JSONResponse(status_code=422, content={"detail": detail, "data": edit_input(), "traceId": getattr(request.state, "trace_id", uuid.uuid4().hex)})

    @app.exception_handler(HTTPException)
    async def http_error(request, error):
        detail = error.detail
        if isinstance(detail, dict):
            detail = {**detail, "data": error_data(detail.get("code", 5000), detail.get("data"))}
        return JSONResponse(status_code=error.status_code, content={"detail": detail}, headers=error.headers)

    @app.middleware("http")
    async def body_budget(request, call_next):
        if request.method == "POST" and request.url.path in ("/api/v1/chat", "/api/v1/interaction/send"):
            chunks, size = [], 0
            async for chunk in request.stream():
                size += len(chunk)
                if size > 20_000_000:
                    trace = request.headers.get("x-trace-id") or uuid.uuid4().hex
                    return JSONResponse(status_code=413, content={"code": 40001, "message": "请求体过大，请编辑后发送", "data": edit_input(), "traceId": trace})
                chunks.append(chunk)
            request._body = b"".join(chunks)
            request.state.body_size = size
        return await call_next(request)
