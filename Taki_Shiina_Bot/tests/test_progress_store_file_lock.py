"""跨进程文件锁单测（PRD EDGE-1「多设备并发超扣」防护）。

`services/progress_store.py` 的 ``transaction()`` / ``snapshot()`` 依赖一把**跨进程**的
阻塞式文件锁（POSIX: ``fcntl.flock``；Windows: ``msvcrt.locking``）。本文件覆盖：

1. 独占锁互斥：A 实例持有 ``transaction()`` 时，B 实例（同进程不同实例 / 不同 fd）进不去；
2. 跨进程互斥：子进程在父进程持锁期间拿不到锁，父进程释放后才拿到（真实多进程场景）；
3. 锁可释放 / 可重入：正常退出与**异常退出**都必须释放，释放后可以再次获取；
4. 共享锁请求不得绕过独占持有者（Windows 上共享锁降级为独占，仍是互斥）。

用例都在同一进程内用事件 + 超时断言，因此锁失效时是**断言失败**而不是把测试挂死；
子进程用 ``daemon`` 语义的显式超时等待，避免 CI 上无限阻塞。
"""

from __future__ import annotations

import base64
import logging
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest

CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.dirname(CURRENT_DIR)
if PACKAGE_ROOT not in sys.path:
    sys.path.insert(0, PACKAGE_ROOT)

from services.progress_store import (  # noqa: E402
    ProgressStore,
    acquire_file_lock,
    release_file_lock,
)

TEST_KEY = base64.urlsafe_b64encode(b"7" * 32).decode("utf-8").rstrip("=")

# 子进程脚本：用**生产代码里的同一套原语**去抢父进程持有的锁，抢到后落一个标记文件。
# 通过环境变量传参，避免 Windows 命令行引号转义问题。
CHILD_SOURCE = r"""
import os

os.environ["DATA_ENC_KEY"] = os.environ["LOCK_TEST_ENC_KEY"]
import sys

sys.path.insert(0, os.environ["LOCK_TEST_PACKAGE_ROOT"])

from services.progress_store import acquire_file_lock, release_file_lock  # noqa: E402

fd = os.open(os.environ["LOCK_TEST_LOCK_PATH"], os.O_CREAT | os.O_RDWR, 0o600)
try:
    acquire_file_lock(fd, exclusive=True)
    with open(os.environ["LOCK_TEST_MARKER"], "w", encoding="utf-8") as fh:
        fh.write("acquired")
    release_file_lock(fd)
finally:
    os.close(fd)
"""


class FileLockSemanticsTestCase(unittest.TestCase):
    """锁原语与 ``transaction()`` / ``snapshot()`` 的互斥语义。"""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.base_dir = tmp.name
        self.data_path = os.path.join(self.base_dir, "progress_data.json")
        self.lock_path = f"{self.data_path}.lock"

        self._prev_env = {
            key: os.environ.get(key)
            for key in ("DATA_ENC_KEY", "DATA_AUTO_MIGRATE", "DATA_BACKUP_ON_MIGRATE")
        }
        self.addCleanup(self._restore_env)
        os.environ["DATA_ENC_KEY"] = TEST_KEY
        os.environ["DATA_AUTO_MIGRATE"] = "0"
        os.environ["DATA_BACKUP_ON_MIGRATE"] = "0"

        self.logger = logging.getLogger("test-progress-store-file-lock")
        self.store_a = ProgressStore(self.data_path, self.logger)
        self.store_b = ProgressStore(self.data_path, self.logger)

        self._fds: list[int] = []
        self._threads: list[threading.Thread] = []
        self.addCleanup(self._close_fds)

    # ---------- 夹具 ----------
    def _restore_env(self) -> None:
        for key, value in self._prev_env.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    def _open_lock_fd(self) -> int:
        fd = os.open(self.lock_path, os.O_CREAT | os.O_RDWR, 0o600)
        self._fds.append(fd)
        return fd

    def _close_fds(self) -> None:
        for thread in self._threads:
            thread.join(timeout=10)
        for fd in self._fds:
            try:
                os.close(fd)
            except OSError:  # pragma: no cover - 已关闭
                pass

    def _start(self, target) -> threading.Thread:
        thread = threading.Thread(target=target, daemon=True)
        self._threads.append(thread)
        thread.start()
        return thread

    # ---------- 1. 独占锁互斥 ----------
    def test_transaction_excludes_other_instance(self):
        """A 持有 transaction 期间，B 的 transaction 必须被阻塞，A 退出后才能进入。"""
        held = threading.Event()
        let_go = threading.Event()
        second_entered = threading.Event()

        def holder():
            with self.store_a.transaction() as data:
                data["user_progress"]["u1"] = {"continuous_days": 1}
                held.set()
                let_go.wait(10)

        def waiter():
            with self.store_b.transaction() as data:
                data["user_progress"]["u2"] = {"continuous_days": 2}
                second_entered.set()

        self._start(holder)
        self.assertTrue(held.wait(10), "持有者未能进入事务")
        self._start(waiter)

        self.assertFalse(
            second_entered.wait(0.6),
            "第二个实例在首个实例持有事务期间进入了 transaction()（独占锁未生效）",
        )
        let_go.set()
        self.assertTrue(second_entered.wait(10), "首个事务释放后第二个实例仍被阻塞")

        # B 的写入必须落盘（锁释放后确实完成了读-改-写）
        snapshot = self.store_a.snapshot()
        self.assertIn("u2", snapshot["user_progress"])

    def test_lock_primitive_blocks_second_handle(self):
        """直接验证锁原语：同一进程的第二个 fd 拿到独占锁前必须等待。"""
        fd_holder = self._open_lock_fd()
        acquire_file_lock(fd_holder, exclusive=True)

        acquired = threading.Event()
        fd_other = self._open_lock_fd()

        def contender():
            acquire_file_lock(fd_other, exclusive=True)
            acquired.set()
            release_file_lock(fd_other)

        self._start(contender)
        self.assertFalse(acquired.wait(0.5), "第二个 fd 未被阻塞（文件锁未生效）")
        release_file_lock(fd_holder)
        self.assertTrue(acquired.wait(10), "释放后第二个 fd 仍未拿到锁")

    # ---------- 2. 跨进程互斥 ----------
    def test_other_process_cannot_lock_while_held(self):
        """子进程在父进程持锁期间拿不到锁；父进程释放后拿到（真实多进程 EDGE-1 场景）。"""
        marker = os.path.join(self.base_dir, "child-acquired.marker")
        env = os.environ.copy()
        env.update(
            {
                "LOCK_TEST_PACKAGE_ROOT": PACKAGE_ROOT,
                "LOCK_TEST_ENC_KEY": TEST_KEY,
                "LOCK_TEST_LOCK_PATH": self.lock_path,
                "LOCK_TEST_MARKER": marker,
            }
        )

        child = None
        try:
            with self.store_a.transaction():
                child = subprocess.Popen(
                    [sys.executable, "-c", CHILD_SOURCE],
                    cwd=PACKAGE_ROOT,
                    env=env,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                )
                time.sleep(1.0)
                self.assertFalse(
                    os.path.exists(marker),
                    "子进程在父进程持锁期间拿到了锁（跨进程互斥失效）",
                )
            # 父进程退出事务（释放锁）后，子进程的重试循环应当拿到锁
            stdout, stderr = child.communicate(timeout=30)
            self.assertEqual(child.returncode, 0, f"子进程失败: {stderr}\n{stdout}")
            self.assertTrue(os.path.exists(marker), "父进程释放后子进程仍未拿到锁")
        finally:
            if child is not None and child.poll() is None:  # pragma: no cover - 防挂死
                child.kill()
                child.communicate(timeout=10)

    # ---------- 3. 锁可释放 / 可重入 ----------
    def test_lock_released_on_exception_and_reacquirable(self):
        """事务体内抛异常时锁必须释放；随后可再次获取（不残留死锁）。"""
        with self.assertRaises(RuntimeError):
            with self.store_a.transaction() as data:
                data["meta"]["should_be_discarded"] = True
                raise RuntimeError("boom")

        acquired = threading.Event()

        def reacquire():
            with self.store_b.transaction() as data:
                data["meta"]["reacquired"] = True
            acquired.set()

        self._start(reacquire)
        self.assertTrue(acquired.wait(10), "异常退出后文件锁未释放，再次获取被阻塞")
        self.assertNotIn("should_be_discarded", self.store_a.snapshot()["meta"])
        self.assertTrue(self.store_a.snapshot()["meta"].get("reacquired"))

    def test_lock_is_reusable_after_release(self):
        """反复加锁/解锁（含 snapshot 共享请求）不应残留锁状态。"""
        for index in range(20):
            with self.store_a.transaction() as data:
                data["meta"]["round"] = index
            self.assertEqual(self.store_a.snapshot()["meta"]["round"], index)

        # 交替使用两个实例，确认锁既能释放、也能在下次获取时重新生效
        acquired = threading.Event()

        def alternate():
            with self.store_b.transaction():
                pass
            acquired.set()

        self._start(alternate)
        self.assertTrue(acquired.wait(10), "反复加解锁后锁无法再次获取")

    # ---------- 4. 共享锁请求的降级语义 ----------
    def test_shared_request_does_not_bypass_exclusive_holder(self):
        """共享锁请求不得绕过独占持有者（Windows 降级为独占锁，仍保持互斥）。"""
        fd_holder = self._open_lock_fd()
        acquire_file_lock(fd_holder, exclusive=True)

        fd_shared = self._open_lock_fd()
        shared_acquired = threading.Event()

        def shared_requester():
            acquire_file_lock(fd_shared, exclusive=False)
            shared_acquired.set()
            release_file_lock(fd_shared)

        self._start(shared_requester)
        self.assertFalse(shared_acquired.wait(0.5), "共享锁请求绕过了独占持有者")
        release_file_lock(fd_holder)
        self.assertTrue(shared_acquired.wait(10), "独占锁释放后共享锁请求仍未成功")

    def test_snapshot_lock_is_acquired_and_released(self):
        """snapshot() 的锁必须成对获取/释放：连续调用不阻塞。"""
        for _ in range(5):
            self.assertIsInstance(self.store_a.snapshot(), dict)

        done = threading.Event()

        def other_snapshot():
            self.store_b.snapshot()
            done.set()

        self._start(other_snapshot)
        self.assertTrue(done.wait(10), "snapshot() 未释放文件锁（另一个实例被阻塞）")


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
