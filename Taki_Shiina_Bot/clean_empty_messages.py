#!/usr/bin/env python3
"""
一次性数据清洗：删除 chat_timeline.json 中 content 为空/纯空白的历史消息。

这些空消息是早期后端未做空回复保护时写入的脏数据，会导致 App 显示"空白气泡+时间戳"。

用法（务必先停服，避免与运行中的 ws_api.py 并发写同一文件）：
    sudo systemctl stop taki-ws
    sudo systemctl stop <http-api 服务名>          # 若有独立 HTTP 服务
    cd /root/Taki-Shiina_Bot/Taki_Shiina_Bot
    ./.venv/bin/python clean_empty_messages.py --dry-run
    ./.venv/bin/python clean_empty_messages.py      # 确认 dry-run 输出后执行
    sudo systemctl start taki-ws
    sudo systemctl start <http-api 服务名>
"""

import argparse
import logging
import os
import sys

from dotenv import load_dotenv

from secure_storage import SecureJsonStore

_base_dir = os.path.dirname(os.path.abspath(__file__))
load_dotenv(os.path.join(_base_dir, ".env"))

logging.basicConfig(format="%(asctime)s - %(levelname)s - %(message)s", level=logging.INFO)
logger = logging.getLogger("clean_empty_messages")

TIMELINE_FILE = os.path.join(_base_dir, "chat_timeline.json")


def is_blank_content(item: dict) -> bool:
    content = item.get("content")
    if content is None:
        return True
    return not str(content).strip()


def main() -> int:
    parser = argparse.ArgumentParser(description="清洗时间线中的空消息")
    parser.add_argument("--dry-run", action="store_true", help="只统计不写入")
    args = parser.parse_args()

    store = SecureJsonStore(TIMELINE_FILE, logger)
    items = store.load([])

    if not isinstance(items, list):
        logger.error(f"时间线数据格式异常（期望 list，实际 {type(items).__name__}），终止")
        return 1

    kept = []
    removed = []
    for item in items:
        if isinstance(item, dict) and is_blank_content(item):
            removed.append(item)
        else:
            kept.append(item)

    logger.info(f"总条数: {len(items)}")
    logger.info(f"将删除空消息: {len(removed)}")
    for item in removed[:50]:
        logger.info(
            "  - messageId=%s role=%s timestamp=%s content=%r",
            item.get("messageId"),
            item.get("role"),
            item.get("timestamp"),
            item.get("content"),
        )
    if len(removed) > 50:
        logger.info(f"  ... 其余 {len(removed) - 50} 条从略")

    if args.dry_run:
        logger.info("dry-run 模式，未写入。确认无误后去掉 --dry-run 执行。")
        return 0

    if not removed:
        logger.info("没有需要删除的空消息。")
        return 0

    store.save(kept)
    logger.info(f"已写入，保留 {len(kept)} 条（删除 {len(removed)} 条）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
