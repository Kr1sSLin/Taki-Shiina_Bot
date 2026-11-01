"""HTTP delivery fence stays held through the ASGI transport boundary."""
from services.async_storage import store_call
import asyncio
import logging

from starlette.responses import JSONResponse

from services.foundation_store import PermissionDenied, StorageBusy, VersionConflict
from services.owner_fence import async_owner_fence


class ControlledJSONResponse(JSONResponse):
    def __init__(self, content, *, store, owner, results):
        super().__init__(content=content)
        self.store, self.owner, self.results = store, owner, results
        self.trace_id = content.get("traceId")

    async def __call__(self, scope, receive, send):
        try:
            async with async_owner_fence(self.store, self.owner):
                for result in self.results:
                    await store_call(self.store.mark_dispatched, self.owner, result)
                await asyncio.wait_for(super().__call__(scope, receive, send), timeout=5)
                # Dispatch intent is already durable; a diagnostic write failure
                # after transport must never send a second HTTP response.
                for result in self.results:
                    try:
                        await store_call(self.store.mark_delivered, self.owner, result)
                    except Exception as error:
                        logging.getLogger(__name__).warning("delivery observation unavailable (%s)", type(error).__name__)
        except (PermissionDenied, StorageBusy, VersionConflict):
            failure = JSONResponse(status_code=503, content={"code": 50302, "message": "暂时未能完成，请使用原请求重试", "data": None, "traceId": self.trace_id})
            await failure(scope, receive, send)
