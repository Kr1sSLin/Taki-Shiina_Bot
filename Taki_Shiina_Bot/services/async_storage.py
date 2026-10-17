"""Finish short SQL/file operations before propagating task cancellation."""
import asyncio


async def store_call(function, *args, **kwargs):
    operation = asyncio.create_task(asyncio.to_thread(function, *args, **kwargs))
    try:
        return await asyncio.shield(operation)
    except asyncio.CancelledError:
        try:
            await operation
        except Exception:
            pass
        raise
