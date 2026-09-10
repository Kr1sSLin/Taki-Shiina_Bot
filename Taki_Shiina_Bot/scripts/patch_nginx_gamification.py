#!/usr/bin/env python3
"""
为「互动积分 · 等级体系」补上 Nginx 反向代理路由（在**服务器上**执行）。

做四件事：
1. 定位：在常见 nginx 配置根目录里找出所有包含目标域名的配置文件；
2. 选择：在该文件里挑出正确的 server 块（优先 listen 443 ssl 的那个）；
3. 修改：写入一段 location 片段文件，并在 server 块里插入一行 include（幂等，可重复执行）；
4. 校验：自动跑 ``nginx -t``，通过后 reload（可用 --no-reload 关闭）。

用法（先在服务器上 dry-run 看一遍再真正执行）：
    sudo python3 Taki_Shiina_Bot/scripts/patch_nginx_gamification.py --domain takishiinabot.top --dry-run
    sudo python3 Taki_Shiina_Bot/scripts/patch_nginx_gamification.py --domain takishiinabot.top

为什么用 include 片段而不是直接往原文件里插 4 段 location：
- 只改 1 行、可逆、未来重复执行不会重复插入；
- 原配置（含宝塔面板生成的注释与格式）保持原样，减少改坏风险。
"""

from __future__ import annotations

import argparse
import datetime
import os
import re
import shutil
import subprocess
import sys
from typing import Iterable

DEFAULT_ROOTS = (
    "/etc/nginx",
    "/usr/local/nginx/conf",
    "/www/server/nginx/conf",
    "/www/server/panel/vhost/nginx",
    "/opt/nginx/conf",
)

# 需要转发到 ws_api 进程的前缀（互动 / 积分 / 等级 / 后台配置）
LOCATION_PREFIXES = ("interaction", "points", "level", "admin")

SKIP_DIR_NAMES = {".git", "__pycache__", "snippets", "modules"}


def log(message: str) -> None:
    print(message, flush=True)


def iter_config_files(roots: Iterable[str]) -> list[str]:
    files: list[str] = []
    for root in roots:
        if not os.path.isdir(root):
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_NAMES]
            for name in filenames:
                if name.endswith((".conf", ".nginx", ".vhost")) or name == "nginx.conf":
                    files.append(os.path.join(dirpath, name))
    return files


def find_domain_files(files: Iterable[str], domain: str) -> list[str]:
    pattern = re.compile(r"server_name\s+[^;]*" + re.escape(domain), re.IGNORECASE)
    hits: list[str] = []
    for path in files:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as handle:
                content = handle.read()
        except OSError:
            continue
        if pattern.search(content):
            hits.append(path)
    return hits


def server_blocks(lines: list[str]) -> list[tuple[int, int]]:
    """返回每个 ``server { ... }`` 块的 (起始行, 结束行)，下标从 0 开始。"""
    blocks: list[tuple[int, int]] = []
    depth = 0
    start: int | None = None
    start_depth = 0
    for index, line in enumerate(lines):
        code = line.split("#", 1)[0]
        if start is None and re.search(r"(^|\s)server\s*\{", code):
            start = index
            start_depth = depth
        depth += code.count("{") - code.count("}")
        if start is not None and depth <= start_depth:
            blocks.append((start, index))
            start = None
    return blocks


def pick_server_block(lines: list[str], blocks: list[tuple[int, int]], domain: str) -> tuple[int, int] | None:
    name_pattern = re.compile(r"server_name\s+[^;]*" + re.escape(domain), re.IGNORECASE)
    candidates: list[tuple[int, int]] = []
    for begin, end in blocks:
        text = "\n".join(lines[begin : end + 1])
        if name_pattern.search(text):
            candidates.append((begin, end))
    if not candidates:
        return None
    for begin, end in candidates:
        text = "\n".join(lines[begin : end + 1])
        if re.search(r"listen\s+[^;]*443", text):
            return (begin, end)
    return candidates[0]


def default_snippet_path(conf_path: str) -> str:
    """选一个**不会被 nginx 通配 include** 的目录放片段，避免 location 出现在 http 层导致语法报错。"""
    if os.path.isdir("/etc/nginx"):
        return "/etc/nginx/snippets/tks-gamification.conf"
    if os.path.isdir("/www/server/nginx/conf"):  # 宝塔面板
        return "/www/server/nginx/conf/snippets/tks-gamification.conf"
    if os.path.isdir("/usr/local/nginx/conf"):  # 源码编译安装
        return "/usr/local/nginx/conf/snippets/tks-gamification.conf"
    return os.path.join(os.path.dirname(conf_path), "snippets", "tks-gamification.conf")


def snippet_content(ws_port: int) -> str:
    lines = [
        "# 互动积分 · 等级体系（PRD）：由 ws_api.py 进程提供，端口见 .env 的 BOT_WS_PORT",
        "# 由 scripts/patch_nginx_gamification.py 自动生成，可重复执行覆盖",
        "# 注意：proxy_pass 后面不要带路径，否则会剥掉 /api/v1/ 前缀导致 404",
    ]
    for prefix in LOCATION_PREFIXES:
        path = f"/api/v1/{prefix}/"
        pad = " " * max(1, 26 - len(path))
        extra = " proxy_read_timeout 60s; proxy_buffering off;" if prefix == "interaction" else " proxy_read_timeout 60s;"
        lines.append(f"location {path}{pad}{{ proxy_pass http://127.0.0.1:{ws_port};{extra} }}")
    return "\n".join(lines) + "\n"


def check_nginx(binary: str) -> bool:
    try:
        result = subprocess.run([binary, "-t"], capture_output=True, text=True)
    except FileNotFoundError:
        log(f"⚠️ 找不到 {binary}，跳过语法校验（请自行执行 nginx -t）")
        return True
    output = (result.stdout + result.stderr).strip()
    if output:
        log(output)
    return result.returncode == 0


def reload_nginx(binary: str) -> None:
    for command in ([binary, "-s", "reload"], ["systemctl", "reload", "nginx"]):
        try:
            result = subprocess.run(command, capture_output=True, text=True)
        except FileNotFoundError:
            continue
        if result.returncode == 0:
            log(f"✅ 已 reload：{' '.join(command)}")
            return
        log(f"⚠️ {' '.join(command)} 失败：{(result.stderr or result.stdout).strip()}")
    log("⚠️ 自动 reload 失败，请手动执行：nginx -s reload 或 systemctl reload nginx")


def main() -> int:
    parser = argparse.ArgumentParser(description="为互动积分/等级体系补上 nginx 路由")
    parser.add_argument("--domain", default="takishiinabot.top", help="站点域名（默认 takishiinabot.top）")
    parser.add_argument("--ws-port", type=int, default=8001, help="ws_api 端口（默认 8001，即 BOT_WS_PORT）")
    parser.add_argument("--root", action="append", default=None, help="配置根目录，可多次指定（默认用内置常见路径）")
    parser.add_argument("--conf", default=None, help="直接指定要修改的配置文件，跳过自动定位")
    parser.add_argument("--snippet-path", default=None, help="include 片段文件路径，默认 /etc/nginx/snippets/tks-gamification.conf")
    parser.add_argument("--nginx-bin", default="nginx", help="nginx 可执行文件路径")
    parser.add_argument("--dry-run", action="store_true", help="只显示将要做的事情，不写任何文件")
    parser.add_argument("--no-reload", action="store_true", help="校验通过后不自动 reload")
    args = parser.parse_args()

    roots = tuple(args.root) if args.root else DEFAULT_ROOTS
    log("== 1/4 定位配置文件 ==")
    if args.conf:
        conf_path = args.conf
        log(f"使用指定文件：{conf_path}")
    else:
        files = iter_config_files(roots)
        log(f"扫描 {len(files)} 个配置文件…")
        hits = find_domain_files(files, args.domain)
        if not hits:
            log(f"❌ 没有找到包含 server_name {args.domain} 的配置。")
            log("   请检查：① 域名是否由 nginx 托管；② 是否用了 Caddy/Traefik/宝塔面板；")
            log("   ③ 用 --root 指定配置目录，或用 --conf 直接指定文件。")
            for root in roots:
                if os.path.isdir(root):
                    log(f"   （存在）{root}")
            return 2
        log("命中文件：")
        for path in hits:
            log(f"  - {path}")
        conf_path = hits[0]
        if len(hits) > 1:
            log(f"⚠️ 命中多个文件，默认修改第一个：{conf_path}")
            log("   如需改另一个，请加 --conf <路径> 重跑。")

    with open(conf_path, "r", encoding="utf-8", errors="replace") as handle:
        original = handle.read()
    lines = original.splitlines()
    blocks = server_blocks(lines)
    block = pick_server_block(lines, blocks, args.domain)
    if block is None:
        log(f"❌ 在 {conf_path} 中找不到包含 server_name {args.domain} 的 server 块。")
        return 2
    begin, end = block
    has_tls = bool(re.search(r"listen\s+[^;]*443", "\n".join(lines[begin : end + 1])))
    log(f"选中 server 块：第 {begin + 1}～{end + 1} 行（{'含 443/TLS' if has_tls else '未发现 443，可能是 HTTP 块'}）")
    for index in range(begin, min(begin + 6, end + 1)):
        log(f"    {index + 1}: {lines[index].strip()}")

    snippet_path = args.snippet_path or default_snippet_path(conf_path)
    include_line = f"    include {snippet_path};"
    log("")
    log("== 2/4 准备 include 片段 ==")
    log(f"片段路径：{snippet_path}")
    log("片段内容：")
    for line in snippet_content(args.ws_port).rstrip("\n").splitlines():
        log(f"    {line}")

    log("")
    log("== 3/4 修改主配置 ==")
    if any(snippet_path in line for line in lines):
        log("已存在该 include，跳过插入（幂等）")
        changed = False
    else:
        # 插到 server_name 行之后（没有 server_name 时插到 server 块首行之后）
        target = None
        for index in range(begin, end + 1):
            if re.match(r"\s*server_name\b", lines[index]):
                target = index
                break
        if target is None:
            target = begin
            log("未找到 server_name 行，改插到 server 块首行之后")
        lines.insert(target + 1, include_line)
        changed = True
        log(f"将在第 {target + 2} 行插入：{include_line.strip()}")

    if args.dry_run:
        log("")
        log("== --dry-run：以下内容不会被写入 ==")
        if changed:
            log(f"  写入 {snippet_path}")
            log(f"  修改 {conf_path}（插入 1 行 include）")
        else:
            log("  无需改动")
        return 0

    if changed:
        stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
        backup = f"{conf_path}.bak.{stamp}"
        shutil.copy2(conf_path, backup)
        log(f"已备份原配置：{backup}")
        os.makedirs(os.path.dirname(snippet_path), exist_ok=True)
        with open(snippet_path, "w", encoding="utf-8") as handle:
            handle.write(snippet_content(args.ws_port))
        log(f"已写入片段：{snippet_path}")
        with open(conf_path, "w", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")
        log(f"已更新主配置：{conf_path}")

    log("")
    log("== 4/4 语法校验与生效 ==")
    if not check_nginx(args.nginx_bin):
        log("❌ nginx -t 未通过，正在回滚…")
        if changed:
            shutil.copy2(backup, conf_path)
            log(f"已回滚 {conf_path}（备份仍在 {backup}）")
            check_nginx(args.nginx_bin)
        return 1
    log("✅ nginx -t 通过")
    if changed and not args.no_reload:
        reload_nginx(args.nginx_bin)
    elif not changed:
        log("配置未变化，无需 reload")

    log("")
    log("完成。自检：")
    log(f'  curl -s -o /dev/null -w "%{{http_code}}\\n" https://{args.domain}/api/v1/level/config   # 期望 401（未带 token）')
    return 0


if __name__ == "__main__":
    sys.exit(main())
