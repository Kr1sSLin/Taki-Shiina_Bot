"""
互动积分 · 等级体系 —— 持久化层

对应 PRD 第五节数据模型：

- ``PointsStore``   → 5.2 ``points_account``（积分余额） + 5.3 ``points_ledger``（积分流水）
- ``ProgressStore`` → 5.1 ``user_progress``（等级与连续天数） + 5.1a ``daily_activity_log``
                     + 5.4 ``makeup_cards``（补签卡库存）

设计要点：

1. **物理分表**（PRD 5.2 要求“与 user_progress 同键但物理分表，杜绝断签逻辑误写积分字段”）：
   余额/流水与进度/连续天数落在两个独立的加密文件里，断签逻辑拿不到积分文件句柄。
2. **加密落盘**：复用 ``secure_storage.SecureJsonStore``（AES-GCM），与聊天记录/记忆同等保护级别（PRD FR-12）。
3. **并发安全**（PRD EDGE-1）：单进程内用可重入线程锁，跨进程用**阻塞式文件锁**
   （POSIX 走 ``fcntl.flock``，Windows 走 ``msvcrt.locking``，见 ``acquire_file_lock``），
   所有读-改-写都在 ``transaction()`` 内完成，因此扣分是原子的
   （等价于 ``UPDATE ... SET balance = balance - :cost WHERE balance >= :cost`` 的原子语义）。
   两端锁语义一致：获取时阻塞直到成功、退出/异常时必定释放；但**不要嵌套加锁**
   （例如在同一 ``transaction()`` 内再开 ``transaction()`` 或调用 ``snapshot()``），
   同一进程用不同 fd 重复加锁在 Linux flock 与 Windows LockFile 上都会自锁。
4. **严格读写**：积分一旦写失败必须让上层感知，故使用 ``load_strict`` / ``save_strict``，
   避免像聊天历史那样静默吞掉异常导致账目丢失。
"""

from __future__ import annotations

import copy
import errno
import os
import threading
import time
from contextlib import contextmanager
from typing import Any, Iterator

from secure_storage import SecureJsonStore

try:  # POSIX（Linux/macOS）：flock 原生阻塞，且区分独占锁与共享锁
    import fcntl
except ImportError:  # pragma: no cover - Windows
    fcntl = None  # type: ignore[assignment]

try:  # Windows：底层是 LockFile，只有独占区域锁
    import msvcrt
except ImportError:  # pragma: no cover - POSIX
    msvcrt = None  # type: ignore[assignment]


# ==================== 跨进程文件锁（PRD EDGE-1） ====================

# Windows 区域锁按“字节区间”互斥：所有进程必须锁同一区间才能互斥，故固定锁第 0 字节。
_WIN_LOCK_OFFSET = 0
_WIN_LOCK_BYTES = 1
# msvcrt.LK_LOCK 内部只重试 10 次（约 1 秒/次）后抛异常，无法表达“一直阻塞”，
# 故改用 LK_NBLCK（非阻塞）+ 短睡眠循环，实现与 flock(LOCK_EX) 等价的真阻塞语义。
_WIN_LOCK_RETRY_INTERVAL = 0.02
# 只有“已被他人锁定”类错误才继续重试；其它错误（权限、句柄非法等）立即抛，
# 以免把真实故障伪装成“一直在等锁”。
_WIN_LOCK_RETRY_ERRNOS = frozenset(
    code
    for code in (
        getattr(errno, "EACCES", None),
        getattr(errno, "EAGAIN", None),
        getattr(errno, "EDEADLOCK", None),
    )
    if code is not None
)


def acquire_file_lock(fd: int, *, exclusive: bool) -> None:
    """在 ``fd`` 上**阻塞**获取跨进程文件锁，直到成功为止（双端同一套调用语义）。

    - POSIX（Linux/macOS）：``fcntl.flock``；``exclusive=True`` → ``LOCK_EX``，
      ``exclusive=False`` → ``LOCK_SH``，与改动前完全一致。
    - Windows：``msvcrt.locking`` 底层是 ``LockFile``，**只有独占区域锁，没有共享锁语义**，
      因此共享请求在此降级为同一字节区间（第 0 字节）的独占锁。

    共享锁降级的安全性（``exclusive=False`` 的唯一调用点是 ``snapshot()`` 读快照）：
    降级只会把锁“加强”，不会降低互斥强度——

    1. 不产生竞态：锁更强不可能让两个读-改-写重叠，最多让并发只读退化为串行；
    2. 不产生丢更新/超扣：扣分路径本来就走独占锁 ``transaction()``，语义未变；
    3. 不引入死锁环路：本项目所有加锁点都是“同一时刻只持有一把锁”
       （``snapshot()`` 与 ``transaction()`` 从不嵌套，已核实全部调用点），
       跨进程各自独立取锁、无 AB-BA 交叉，故不存在环路等待；
    4. 代价仅是并发读串行化（读路径本身是毫秒级短事务）。

    注意：同一进程内用**不同 fd** 重复加锁同样会阻塞（Linux flock 与 Windows LockFile
    行为一致），因此调用方不得嵌套加锁。
    """
    if fcntl is not None:
        fcntl.flock(fd, fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH)
        return
    if msvcrt is None:  # pragma: no cover - 既非 POSIX 也非 Windows 的平台
        raise RuntimeError("当前平台既无 fcntl 也无 msvcrt，无法实现跨进程文件锁")
    # Windows 分支：msvcrt 无共享锁，exclusive 在此被有意忽略（共享 → 独占降级）。
    while True:
        try:
            os.lseek(fd, _WIN_LOCK_OFFSET, os.SEEK_SET)
            msvcrt.locking(fd, msvcrt.LK_NBLCK, _WIN_LOCK_BYTES)
            return
        except OSError as exc:
            if exc.errno not in _WIN_LOCK_RETRY_ERRNOS:
                raise
            time.sleep(_WIN_LOCK_RETRY_INTERVAL)


def release_file_lock(fd: int) -> None:
    """释放 ``acquire_file_lock`` 取得的锁（失败向上抛，不静默吞掉）。"""
    if fcntl is not None:
        fcntl.flock(fd, fcntl.LOCK_UN)
        return
    if msvcrt is None:  # pragma: no cover - 既非 POSIX 也非 Windows 的平台
        return
    os.lseek(fd, _WIN_LOCK_OFFSET, os.SEEK_SET)
    msvcrt.locking(fd, msvcrt.LK_UNLCK, _WIN_LOCK_BYTES)


SCHEMA_VERSION = 1

# 流水原因码（PRD 5.3 / 4.3 / FR-9）
REASON_DAILY_FIRST_CHAT = "DAILY_FIRST_CHAT"
REASON_STREAK_3_DAY = "STREAK_3_DAY"
REASON_ANNIVERSARY = "ANNIVERSARY"
REASON_ITEM_SEND = "ITEM_SEND"
REASON_ITEM_REFUND = "ITEM_REFUND"
# 测试/运维手动调整（仅由 /api/v1/admin/points/adjust 产生，正常运行不会出现）
REASON_ADMIN_ADJUST = "ADMIN_ADJUST"

# 每日有效对话来源（PRD 5.1a，INTERACTION 为需求方确认后新增）
SOURCE_CHAT = "CHAT"
SOURCE_MAKEUP_CARD = "MAKEUP_CARD"
SOURCE_INTERACTION = "INTERACTION"

CARD_AVAILABLE = "AVAILABLE"
CARD_USED = "USED"

LEVEL_NONE = "NONE"


def _now_ms() -> int:
    return int(time.time() * 1000)


def activity_key(user_id: str, activity_date: str) -> str:
    """``daily_activity_log`` 的唯一键，等价于唯一约束 (user_id, activity_date)。"""
    return f"{user_id}|{activity_date}"


class SecureTxStore:
    """带文件锁的加密 JSON 读-改-写存储基类。"""

    def __init__(self, path: str, logger):
        self.path = path
        self.logger = logger
        self._store = SecureJsonStore(path, logger)
        self._thread_lock = threading.RLock()
        self._lock_path = f"{path}.lock"

    # ---------- 子类实现 ----------
    def _empty(self) -> dict:
        raise NotImplementedError

    def _normalize(self, raw: Any) -> dict:
        """把磁盘上的数据补齐成完整结构，容忍旧版本缺字段。"""
        data = self._empty()
        if not isinstance(raw, dict):
            return data
        for key, value in raw.items():
            if key == "meta" and isinstance(value, dict):
                data["meta"].update(value)
            elif key in data:
                data[key] = value
            else:
                data[key] = value
        data["version"] = SCHEMA_VERSION
        return data

    # ---------- 读写 ----------
    def _load(self) -> dict:
        raw = self._store.load_strict(self._empty())
        return self._normalize(raw)

    def _save(self, data: dict) -> None:
        self._store.save_strict(data)

    @contextmanager
    def transaction(self) -> Iterator[dict]:
        """独占读-改-写事务；正常退出时原子写回，异常时丢弃改动。"""
        with self._thread_lock:
            lock_fd = None
            locked = False
            try:
                lock_fd = os.open(self._lock_path, os.O_CREAT | os.O_RDWR, 0o600)
                acquire_file_lock(lock_fd, exclusive=True)
                locked = True
                data = self._load()
                yield data
                self._save(data)
            finally:
                if lock_fd is not None:
                    try:
                        # 只在确实拿到锁时解锁；未拿到锁（自身抛错）时直接关 fd，
                        # 关句柄同样会释放该句柄上的锁，不会出现锁泄漏。
                        if locked:
                            release_file_lock(lock_fd)
                    finally:
                        os.close(lock_fd)

    def snapshot(self) -> dict:
        """只读快照（不写回），用于查询接口。

        注意：Windows 下 msvcrt 无共享锁，``exclusive=False`` 会降级为独占锁
        （见 ``acquire_file_lock`` 的说明），语义上更严格、不会削弱互斥。
        """
        with self._thread_lock:
            lock_fd = None
            locked = False
            try:
                lock_fd = os.open(self._lock_path, os.O_CREAT | os.O_RDWR, 0o600)
                acquire_file_lock(lock_fd, exclusive=False)
                locked = True
                return copy.deepcopy(self._load())
            finally:
                if lock_fd is not None:
                    try:
                        if locked:
                            release_file_lock(lock_fd)
                    finally:
                        os.close(lock_fd)


class PointsStore(SecureTxStore):
    """5.2 points_account + 5.3 points_ledger。"""

    def _empty(self) -> dict:
        return {
            "version": SCHEMA_VERSION,
            "points_account": {},
            "points_ledger": [],
            "meta": {"next_ledger_id": 1},
        }

    # ---------- 账户 ----------
    @staticmethod
    def ensure_account(data: dict, user_id: str) -> dict:
        account = data["points_account"].get(user_id)
        if not isinstance(account, dict):
            account = {"balance": 0, "updated_at": _now_ms()}
            data["points_account"][user_id] = account
        account.setdefault("balance", 0)
        return account

    @staticmethod
    def get_balance(data: dict, user_id: str) -> int:
        account = data["points_account"].get(user_id) or {}
        try:
            return int(account.get("balance", 0))
        except (TypeError, ValueError):
            return 0

    @staticmethod
    def find_by_idempotency(data: dict, key: str) -> dict | None:
        for entry in data["points_ledger"]:
            if entry.get("idempotency_key") == key:
                return entry
        return None

    @classmethod
    def append_ledger(
        cls,
        data: dict,
        user_id: str,
        change_amount: int,
        reason_code: str,
        idempotency_key: str,
        related_item_id: str | None = None,
        business_date: str | None = None,
        note: str | None = None,
    ) -> dict:
        """追加一条只增不改的流水，并同步维护余额快照。

        幂等：``idempotency_key`` 已存在时直接返回既有记录，不重复加减（PRD 5.3 / FR-13）。
        原子：余额不足时（change_amount 为负）不写流水并抛 ``ValueError``，
        调用方据此返回“积分不足”。
        """
        existing = cls.find_by_idempotency(data, idempotency_key)
        if existing is not None:
            return existing

        account = cls.ensure_account(data, user_id)
        balance = int(account.get("balance", 0))
        amount = int(change_amount)
        new_balance = balance + amount
        if new_balance < 0:
            raise ValueError("insufficient_points")

        ledger_id = int(data["meta"].get("next_ledger_id", 1))
        data["meta"]["next_ledger_id"] = ledger_id + 1
        entry = {
            "id": ledger_id,
            "user_id": user_id,
            "change_amount": amount,
            "reason_code": reason_code,
            "balance_after": new_balance,
            "related_item_id": related_item_id,
            "idempotency_key": idempotency_key,
            "created_at": _now_ms(),
            "business_date": business_date,
        }
        if note:
            entry["note"] = note
        data["points_ledger"].append(entry)
        account["balance"] = new_balance
        account["updated_at"] = _now_ms()
        return entry


class ProgressStore(SecureTxStore):
    """5.1 user_progress + 5.1a daily_activity_log + 5.4 makeup_cards。"""

    def _empty(self) -> dict:
        return {
            "version": SCHEMA_VERSION,
            "user_progress": {},
            "daily_activity_log": {},
            "makeup_cards": [],
            "meta": {
                "next_activity_id": 1,
                "next_card_id": 1,
                # 月度发放 / 纪念日发放 / 断签提醒 / 断签事件的幂等标记
                "makeup_grant_months": {},
                "anniversary_grants": {},
                "streak_warnings": {},
                "break_notices": {},
            },
        }

    # ---------- 用户进度 ----------
    @staticmethod
    def ensure_progress(data: dict, user_id: str) -> dict:
        progress = data["user_progress"].get(user_id)
        if not isinstance(progress, dict):
            progress = {
                "continuous_days": 0,
                "level_code": LEVEL_NONE,
                "last_valid_date": None,
                "level_updated_at": None,
                "highest_level_code": LEVEL_NONE,
                "updated_at": _now_ms(),
            }
            data["user_progress"][user_id] = progress
        progress.setdefault("continuous_days", 0)
        progress.setdefault("level_code", LEVEL_NONE)
        progress.setdefault("last_valid_date", None)
        progress.setdefault("level_updated_at", None)
        progress.setdefault("highest_level_code", LEVEL_NONE)
        return progress

    def known_user_ids(self, data: dict) -> list[str]:
        users = set(data.get("user_progress", {}).keys())
        for entry in data.get("makeup_cards", []):
            if entry.get("user_id"):
                users.add(str(entry["user_id"]))
        return sorted(users)

    # ---------- 每日有效对话流水 ----------
    @staticmethod
    def activity_dates(data: dict, user_id: str) -> list[str]:
        prefix = f"{user_id}|"
        dates = [
            entry.get("activity_date")
            for key, entry in data["daily_activity_log"].items()
            if key.startswith(prefix) and entry.get("activity_date")
        ]
        return sorted(d for d in dates if d)

    @classmethod
    def record_activity(cls, data: dict, user_id: str, activity_date: str, source: str) -> bool:
        """写入一条每日有效对话记录；已存在则跳过（唯一约束 (user_id, activity_date)）。

        返回 True 表示本次是新增记录。
        """
        key = activity_key(user_id, activity_date)
        if key in data["daily_activity_log"]:
            return False
        activity_id = int(data["meta"].get("next_activity_id", 1))
        data["meta"]["next_activity_id"] = activity_id + 1
        data["daily_activity_log"][key] = {
            "id": activity_id,
            "user_id": user_id,
            "activity_date": activity_date,
            "source": source,
            "created_at": _now_ms(),
        }
        return True

    @staticmethod
    def has_activity(data: dict, user_id: str, activity_date: str) -> bool:
        return f"{user_id}|{activity_date}" in data["daily_activity_log"]

    # ---------- 补签卡 ----------
    @staticmethod
    def available_card_count(data: dict, user_id: str) -> int:
        return sum(
            1
            for card in data["makeup_cards"]
            if card.get("user_id") == user_id and card.get("status") == CARD_AVAILABLE
        )

    @classmethod
    def grant_card(cls, data: dict, user_id: str, granted_month: str) -> dict:
        card_id = int(data["meta"].get("next_card_id", 1))
        data["meta"]["next_card_id"] = card_id + 1
        card = {
            "id": card_id,
            "user_id": user_id,
            "granted_month": granted_month,
            "status": CARD_AVAILABLE,
            "used_for_date": None,
            "used_at": None,
            "created_at": _now_ms(),
        }
        data["makeup_cards"].append(card)
        return card

    @classmethod
    def consume_card(cls, data: dict, user_id: str, target_date: str) -> dict | None:
        for card in data["makeup_cards"]:
            if card.get("user_id") == user_id and card.get("status") == CARD_AVAILABLE:
                card["status"] = CARD_USED
                card["used_for_date"] = target_date
                card["used_at"] = _now_ms()
                return card
        return None

    @staticmethod
    def user_cards(data: dict, user_id: str) -> list[dict]:
        return [card for card in data["makeup_cards"] if card.get("user_id") == user_id]
