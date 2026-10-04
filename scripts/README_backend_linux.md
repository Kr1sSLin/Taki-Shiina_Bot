# Linux 后端一键部署

在仓库根目录运行（使用拥有项目目录、具备 sudo 权限的普通用户）：

```bash
git pull
bash scripts/setup-backend.sh
```

自动安装系统依赖、创建后端 `.venv`、安装 `requirements.txt`、配置 `.env`、安装并启动 `tks-backend.service`，最后检查 `/healthz`。首次输入 `AUTH_USERNAME`、DeepSeek API Key、登录密码（至少 12 位，输入两次）。密码和 API Key 输入不回显，密码以项目使用的 PBKDF2-SHA256 格式保存。Google/Gemini 和天气等可选服务在 `.env` 中自行填写真实凭据。

## 环境要求

- Linux + systemd；自动安装系统包使用 apt，推荐 Ubuntu 24.04 / Debian 12+。
- Python 3.11+。系统 Python 太旧时，先自行安装新版，再加 `--python /usr/bin/python3.12`；指定解释器须具备 venv 模块。
- 服务器能访问系统软件源和 Python 包索引。脚本不会更改软件源。
- 项目路径使用英文字母、数字、`_`、`-`、`.` 和 `/`，不能有空格或中文。
- 服务以普通用户运行。若用 root 登录，先准备拥有项目的普通用户，再运行 `bash scripts/setup-backend.sh --run-user 用户名`；放在 `/root` 下的仓库通常无法被普通用户访问，应先用部署用户在其可访问的位置克隆。
- 其他发行版先装 Python、venv、pip、编译依赖、CA 证书和 `flock`，再加 `--skip-system-packages`。容器中没有 systemd 时，此脚本不适用。

## 首次部署与更新

### 独立测试服务器

测试服务器使用单独检出的代码和全新 `.env`，不要复制生产配置或生产数据。在测试服务器执行：

```bash
bash scripts/setup-backend.sh --service-name tks-backend-dev
```

后续更新必须继续指定相同服务名：

```bash
git pull
bash scripts/setup-backend.sh --service-name tks-backend-dev --non-interactive
sudo systemctl status tks-backend-dev --no-pager
sudo journalctl -u tks-backend-dev -f
```

`--service-name` 可带 `.service` 后缀；不指定时保持正式服务名 `tks-backend`。
脚本会在改动依赖和配置前检查同名服务，拒绝覆盖其他目录或非本脚本管理的服务。
服务名只隔离 systemd 实例；每个实例仍需使用独立代码目录、`.env` 和数据目录。
专用测试服务器可以继续使用本机端口 8000；同机多实例必须配置不同端口。

Android debug APK 为 `Taki DEV`，包名为 `com.krisslin.androidaiassistant.dev`，
固定连接 `https://staging.takishiinabot.top/api/v1/` 和 `wss://staging.takishiinabot.top/ws/chat`。
测试服务器须为此域名配置 HTTPS 证书和下文的 WebSocket 反代；不要把 staging 域名转发到生产。
首次配置会生成独立认证和加密密钥。可在 `.env` 中将 `BOT_DATA_DIR` 设置为测试用户可写的独立绝对路径，
例如 `/home/tks-dev/data`，创建目录并确认权限后重启 `tks-backend-dev`。

### 默认正式服务

脚本根据自身路径定位项目，不要求当前目录一定是仓库根目录。统一入口为 `unified_api.py`，一个进程提供认证、HTTP、WebSocket、互动和定时任务，不要再同时启动旧的 `main.py` / `http_api.py` / `ws_api.py` 服务。

默认只绑定 `127.0.0.1:8000`。其他端口可在后端 `.env` 设置 `BOT_UNIFIED_PORT`（1024～65535）。部署前请自行停止旧部署的服务；脚本不会杀死其他进程，也不会替换已有的非本脚本生成的同名 systemd 服务。

后续更新：

```bash
git pull
bash scripts/setup-backend.sh --non-interactive
```

已有有效账号、密码哈希、API Key、JWT Secret、HTTP/WS Token、管理 Token、数据密钥和自定义配置都会保留；旧明文密码会转为哈希。缺失的本地密钥自动生成，缺失的账号/登录凭据/DeepSeek Key 在非交互模式下报错。`.env.example` 新增字段会补入。占位值 `your_*` 不会当作真实凭据使用。配置使用 dotenv 解析，不执行 shell；自动部署不支持 `${...}` 插值，请填写实际值。

`.env` 修改前备份为 `.env.bak.<时间戳>`，配置和备份权限均为 `600`，都已加入 Git 忽略。已有 `DATA_ENC_KEY` 永不自动轮换；存在加密数据却缺失密钥，或密钥无法解密数据时，配置阶段会中止。数据目录沿用既有 `BOT_DATA_DIR`；默认仍是后端源码目录，不自动迁移数据。备份必须同时保存数据和原 `.env`。业务数据的迁移仍遵循 `.env` 中现有迁移开关。

脚本直接更新现有虚拟环境并重启服务，会产生短暂中断；它不提供版本/依赖的自动回滚。部署前请备份数据，依赖安装失败或健康检查失败时按错误提示处理后重跑。

## 公网接入

成功的本机健康检查不代表已经提供公网 HTTPS。服务器已有 Nginx/Caddy 时，将所有普通 HTTP 路径和 `/ws/chat` 转发到统一端口；WebSocket 需要 Upgrade。以下内容放进**已有 HTTPS Nginx server 块**，端口与 `.env` 保持一致：

```nginx
# 运维接口不经公网代理，避免后端把代理请求误判为本机访问。
location = /internal { return 403; }
location ^~ /internal/ { return 403; }
location = /api/v1/admin { return 403; }
location ^~ /api/v1/admin/ { return 403; }

location / {
    proxy_pass http://127.0.0.1:8000;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Authorization $http_authorization;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Sec-WebSocket-Protocol $http_sec_websocket_protocol;
    proxy_read_timeout 3600s;
    proxy_buffering off;
}
```

保留现有域名、证书、上传限制等配置，检查后重载：

```bash
sudo nginx -t && sudo systemctl reload nginx
```

客户端 HTTP 地址指向 `https://你的域名`，WebSocket 指向 `wss://你的域名/ws/chat`。云安全组/防火墙按反代需要开放 HTTPS 端口，不需要开放后端 8000。脚本不申请证书、不更改防火墙或现有反代。

## 常用命令

```bash
sudo systemctl status tks-backend --no-pager
sudo journalctl -u tks-backend -n 100 --no-pager
sudo journalctl -u tks-backend -f
sudo systemctl restart tks-backend
sudo systemctl stop tks-backend
sudo systemctl disable tks-backend
curl http://127.0.0.1:8000/healthz
bash scripts/setup-backend.sh --help
```

修改 `.env` 后重启服务即可。systemd 不加载 `.env` 为 shell 环境，由应用自身读取，避免两种解析规则不一致。服务启用开机自启和异常重启；健康检查验证统一 API 启动，不验证外部 AI Key 的额度或有效性。

维护脚本时可运行隔离的配置回归测试（不导入应用、不访问业务数据或外部服务）：

```bash
Taki_Shiina_Bot/.venv/bin/python -m unittest discover -s scripts/tests -v
bash -n scripts/setup-backend.sh
```
