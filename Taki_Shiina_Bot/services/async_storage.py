"""短事务完成后再传播取消，避免补偿与尚未结束的写入竞争。"""
import asyncio
from weakref import WeakValueDictionary

from services.file_lock import FileLockTimeoutError

_gamification_gates = WeakValueDictionary()


async def store_call(function, *args, **kwargs):
    operation = asyncio.create_task(asyncio.to_thread(function, *args, **kwargs))
    cancelled = False
    while not operation.done():
        try:
            await asyncio.shield(operation)
        except asyncio.CancelledError:
            cancelled = True
        except Exception:
            break
    if cancelled:
        if not operation.cancelled():
            operation.exception()
        raise asyncio.CancelledError
    return operation.result()


async def gamification_call(function, *args, **kwargs):
    # 这些入口共享整份积分/进度文件，在线程池外排队，避免锁等待耗尽默认线程池。
    gate = _gamification_gates.setdefault(asyncio.get_running_loop(), asyncio.Semaphore(1))
    try:
        await asyncio.wait_for(gate.acquire(), timeout=10)
    except TimeoutError:
        raise FileLockTimeoutError("gamification storage is busy") from None
    try:
        return await store_call(function, *args, **kwargs)
    finally:
        gate.release()
