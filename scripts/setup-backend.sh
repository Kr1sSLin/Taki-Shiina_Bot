#!/usr/bin/env bash
# Run from any directory: bash /path/to/repo/scripts/setup-backend.sh
set -Eeuo pipefail
umask 077

usage() {
    cat <<'EOF'
Linux 后端一键部署（推荐 Ubuntu 24.04 / Debian 12+，需要 systemd）
  bash scripts/setup-backend.sh [选项]

  --non-interactive       从已有 .env 补齐配置，不提问；缺账号/API Key 时失败
  --skip-system-packages  跳过 apt，使用已安装的 Python 3.11+、venv、pip
  --python PATH          指定 Python 解释器（默认 python3）
  --run-user USER        systemd 运行用户（默认当前普通用户 / SUDO_USER）
  --service-name NAME    独立服务名（默认 tks-backend；测试服用 tks-backend-dev）
  -h, --help             显示帮助

首次运行输入账号、密码和 DeepSeek API Key；已有配置保持不变。
自动创建 .venv、生成密钥、安装依赖、配置并重启 tks-backend.service。
统一入口只监听本机，公网接入请按 README_backend_linux.md 配置 TLS 反代。
EOF
}

die() { printf '错误：%s\n' "$*" >&2; exit 1; }
trap 'printf "部署失败（第 %s 行）。服务日志：sudo journalctl -u %s -n 80 --no-pager\n" "$LINENO" "$SERVICE_NAME" >&2' ERR

NON_INTERACTIVE=0
SKIP_PACKAGES=0
PYTHON=python3
SERVICE_NAME=tks-backend
RUN_USER=${SUDO_USER:-$(id -un)}
while (($#)); do
    case "$1" in
        --non-interactive) NON_INTERACTIVE=1; shift ;;
        --skip-system-packages) SKIP_PACKAGES=1; shift ;;
        --python|--run-user|--service-name)
            (($# >= 2)) && [[ -n $2 ]] || die "$1 缺少参数"
            case "$1" in --python) PYTHON=$2 ;; --run-user) RUN_USER=$2 ;; --service-name) SERVICE_NAME=$2 ;; esac
            shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) die "未知参数：$1（使用 --help 查看用法）" ;;
    esac
done

SERVICE_NAME=${SERVICE_NAME%.service}
[[ $SERVICE_NAME =~ ^[a-zA-Z0-9][a-zA-Z0-9_-]{0,79}$ ]] || die '服务名只能包含字母、数字、下划线、连字符，长度 1～80；可带 .service 后缀。'
SERVICE_UNIT="$SERVICE_NAME.service"
[[ $(uname -s) == Linux ]] || die '请在 Linux 服务器上运行此脚本。'
[[ -d /run/systemd/system ]] || die '需要正在运行的 systemd；不适用于普通 Docker 容器。'
ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)
BACKEND="$ROOT_DIR/Taki_Shiina_Bot"
# These paths are written into a systemd unit. Reject specifiers/control characters.
[[ $ROOT_DIR =~ ^/[a-zA-Z0-9_./-]+$ ]] || die '项目路径只能包含英文字母、数字、下划线、点、斜杠、连字符。'
[[ $RUN_USER =~ ^[a-zA-Z_][a-zA-Z0-9_-]*$ ]] || die '运行用户名格式不支持。'
id "$RUN_USER" >/dev/null 2>&1 || die "用户 $RUN_USER 不存在；请先创建普通部署用户并让其拥有项目目录。"
[[ $(id -u "$RUN_USER") != 0 ]] || die '请用有 sudo 权限的普通用户运行；root 请用 --run-user 指定拥有项目的普通用户。'
[[ -f $BACKEND/unified_api.py ]] || die '未找到后端 unified_api.py。'
if ((EUID != 0)); then
    [[ $RUN_USER == "$(id -un)" ]] || die '指定其他用户时请通过 sudo 运行。'
    command -v sudo >/dev/null || die '请安装 sudo 或通过 root 加 --run-user 运行。'
    sudo -v
fi
as_root() { if ((EUID == 0)); then "$@"; else sudo -- "$@"; fi; }
as_user() {
    if [[ $(id -un) == "$RUN_USER" ]]; then "$@"; else runuser -u "$RUN_USER" -- "$@"; fi
}
# Check ownership before installing dependencies or changing configuration.
UNIT_PATH="/etc/systemd/system/$SERVICE_UNIT"
EXISTING_UNIT=$(as_root systemctl show "$SERVICE_UNIT" --property=FragmentPath --value)
if [[ -n $EXISTING_UNIT || -e $UNIT_PATH || -L $UNIT_PATH ]]; then
    [[ $EXISTING_UNIT == "$UNIT_PATH" && ! -L $UNIT_PATH ]] || die "服务 $SERVICE_UNIT 已存在于其他位置或被屏蔽，请使用其他 --service-name。"
    as_root grep -qxF '# Managed by TKS scripts/setup-backend.sh' "$UNIT_PATH" || die "已有 $UNIT_PATH 并非本脚本生成，请使用其他 --service-name。"
    as_root grep -qxF "WorkingDirectory=$BACKEND" "$UNIT_PATH" || die "服务 $SERVICE_UNIT 属于另一代码目录，请使用其他 --service-name。"
fi
as_user test -w "$BACKEND" || die "$RUN_USER 无法写入后端目录，请检查项目所有权及父目录访问权限。"
cd "$BACKEND"

# Prevent two installers from updating the same virtual environment/configuration.
command -v flock >/dev/null || die '缺少 flock，请安装 util-linux。'
exec 9>"$BACKEND/.setup-backend.lock"
flock -n 9 || die '另一个部署正在运行。'
if ((EUID == 0)); then chown "$RUN_USER" "$BACKEND/.setup-backend.lock"; fi

if ((SKIP_PACKAGES == 0)); then
    command -v apt-get >/dev/null || die '自动装包支持 apt 系统；其他发行版请先装 Python 3.11+、venv、pip 后加 --skip-system-packages。'
    printf '\n[1/5] 安装系统依赖\n'
    as_root apt-get update
    as_root env DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-venv python3-pip build-essential libffi-dev libssl-dev ca-certificates
fi
PYTHON=$(command -v "$PYTHON") || die '找不到指定 Python。'
"$PYTHON" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else "需要 Python 3.11+，请安装后使用 --python 指定解释器。")'

printf '\n[2/5] 安装 Python 依赖\n'
if [[ -e .venv && ! -x .venv/bin/python ]]; then
    die '.venv 不是可用的 Linux 虚拟环境；请先自行备份/移走该目录后重试。'
fi
if [[ ! -d .venv ]]; then as_user "$PYTHON" -m venv .venv; fi
VENV="$BACKEND/.venv/bin/python"
as_user "$VENV" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else "已有虚拟环境需要 Python 3.11+")'
as_user "$VENV" -m pip install -r requirements.txt
as_user "$VENV" -m pip check

printf '\n[3/5] 配置 .env（已有密钥不轮换）\n'
CONFIG_ARGS=()
if ((NON_INTERACTIVE)); then CONFIG_ARGS+=(--non-interactive); fi
as_user "$VENV" "$ROOT_DIR/scripts/setup_backend_config.py" --backend "$BACKEND" "${CONFIG_ARGS[@]}"
PORT=$(as_user "$VENV" "$ROOT_DIR/scripts/setup_backend_config.py" --backend "$BACKEND" --print-port)
if ! as_root systemctl is-active --quiet "$SERVICE_UNIT"; then
    as_user "$VENV" - "$PORT" <<'PY'
import socket
import sys
with socket.socket() as sock:
    try:
        sock.bind(("127.0.0.1", int(sys.argv[1])))
    except OSError:
        sys.exit("统一入口端口已被占用，请停止旧后端进程或修改 BOT_UNIFIED_PORT 后重试。")
PY
fi

printf '\n[4/5] 配置 systemd\n'
UNIT=$(mktemp)
trap 'rm -f -- "$UNIT"' EXIT
cat >"$UNIT" <<EOF
# Managed by TKS scripts/setup-backend.sh
[Unit]
Description=TKS unified backend $SERVICE_NAME (HTTP, WebSocket, auth)
Wants=network-online.target
After=network-online.target
StartLimitIntervalSec=60
StartLimitBurst=5

[Service]
Type=simple
User=$RUN_USER
WorkingDirectory=$BACKEND
Environment=PYTHONUNBUFFERED=1
Environment=PYTHONUTF8=1
Environment=PYTHONPATH=$BACKEND:$ROOT_DIR
ExecStart=$VENV $BACKEND/unified_api.py
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
UMask=0077
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF
if as_root test -e "$UNIT_PATH"; then
    as_root grep -q '^# Managed by TKS scripts/setup-backend.sh$' "$UNIT_PATH" || die "已有 $UNIT_PATH 并非本脚本生成，请先备份并处理服务命名冲突。"
    as_root cp -p -- "$UNIT_PATH" "$UNIT_PATH.bak.$(date +%s)"
fi
as_root install -m 644 "$UNIT" "$UNIT_PATH"
as_root systemctl daemon-reload
as_root systemctl enable "$SERVICE_UNIT"
as_root systemctl restart "$SERVICE_UNIT"

printf '\n[5/5] 等待统一入口健康检查\n'
READY=0
for ((attempt=0; attempt<30; attempt++)); do
    if as_root systemctl is-active --quiet "$SERVICE_UNIT" &&
        "$VENV" - "$PORT" <<'PY'
import json
import sys
import urllib.request
try:
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(f"http://127.0.0.1:{sys.argv[1]}/healthz", timeout=2) as response:
        body = json.load(response)
    sys.exit(0 if body.get("status") == "ok" and body.get("service") == "unified_api" else 1)
except Exception:
    sys.exit(1)
PY
    then
        READY=1
        break
    fi
    sleep 2
done
((READY)) || die '健康检查失败；请检查日志、旧进程端口占用和 .env 配置。'
printf '\n部署成功：http://127.0.0.1:%s/healthz\n' "$PORT"
printf '服务：sudo systemctl status %s\n日志：sudo journalctl -u %s -f\n' "$SERVICE_UNIT" "$SERVICE_UNIT"
printf '后续更新：bash scripts/setup-backend.sh --service-name %s --non-interactive\n' "$SERVICE_NAME"
printf '公网接入参见 scripts/README_backend_linux.md（HTTPS/WSS 反代）。\n'
