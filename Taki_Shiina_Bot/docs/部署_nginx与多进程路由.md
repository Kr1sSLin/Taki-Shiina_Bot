# 部署：多进程拓扑与 Nginx 反向代理

> 背景：后端有 3 个可独立启动的 FastAPI 进程 + 2 个端口约定，新增的「互动/积分/等级/后台配置」
> 由 `ws_api.py` 提供（原因见 `互动积分等级体系_开发自检.md` 第 5 节），因此反向代理必须按**路径**分流。

## 1. 进程与路由归属

| 进程            | 启动方式                           | 默认端口                                        | 负责路径                                                                                            |
| ------------- | ------------------------------ | ------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| 认证服务          | `uvicorn main:app --port 8002` | 代码未指定端口（uvicorn 默认 8000，**会与 http_api 冲突**） | `/api/v1/auth/*`、`/api/v1/health*`                                                              |
| HTTP API      | `python http_api.py`           | `BOT_HTTP_PORT`（默认 8000）                    | `/api/v1/chat/history`、`/api/v1/memory/*`、`/api/v1/settings/*`（以及不计分的 `POST /api/v1/chat`）      |
| WebSocket API | `python ws_api.py`             | `BOT_WS_PORT`（默认 8001）                      | `/ws/chat` + **`/api/v1/interaction/*`、`/api/v1/points/*`、`/api/v1/level/*`、`/api/v1/admin/*`** |

> 积分/等级数据（`points_account.json` / `progress_data.json`）由 `ws_api` 进程**独占写入**，
> 定时任务（断签扫描、月度补签卡、纪念日）也在该进程启动时拉起。不要把新接口挂到别的进程，
> 否则会出现多进程双写账本。

## 2. 三个必须注意的坑

1. **`proxy_pass` 后面不要带路径。**
   `proxy_pass http://127.0.0.1:8001;` ✅ 原样透传 URI；
   `proxy_pass http://127.0.0.1:8001/;` ❌ 会剥掉 `/api/v1/` 前缀，后端直接 404。
2. **`proxy_set_header` 是覆盖而不是叠加。**
   只要某个 `location` 里写了任意一条 `proxy_set_header`，它就不再继承 server 级的其它几条。
   本配置把所有头统一放在 server 级，location 里一条都不写——尤其别丢掉 `Authorization`：
   Android 端 WebSocket 是用**请求头**传 JWT 的（用 URL query token 会被后端拒绝）。
3. **WebSocket 的读超时默认只有 60s。**
   长连接必须显式放大（本配置 3600s），否则会出现"聊几句就断线重连"。

## 3. 最小改动版（塞进你现有配置即可）

```nginx
# 新增：互动 / 积分 / 等级 / 后台配置（由 ws_api 进程提供）
# nginx 按“最长前缀”匹配，这几条一定优先于 location /api/v1/
location /api/v1/interaction/ { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
location /api/v1/points/      { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
location /api/v1/level/       { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
location /api/v1/admin/       { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
```

WebSocket 的 `location /ws/` 如果已经能正常连接则无需改动：积分/等级/断签事件复用同一条 `/ws/chat` 通道。

## 4. 完整版（含 TLS 与全部路由）

```nginx
# http 块顶层：WebSocket 握手用的 Connection 变量（必须有，否则 WS 连不上）
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    listen [::]:80;
    server_name takishiinabot.top;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 on;                      # nginx < 1.25 请改用：listen 443 ssl http2;
    server_name takishiinabot.top;

    ssl_certificate     /etc/letsencrypt/live/takishiinabot.top/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/takishiinabot.top/privkey.pem;
    ssl_protocols       TLSv1.2 TLSv1.3;

    # ===== 全站代理公共设置（location 里不要再写 proxy_set_header）=====
    proxy_http_version 1.1;
    proxy_connect_timeout 10s;
    proxy_set_header Host              $host;
    proxy_set_header X-Real-IP         $remote_addr;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Authorization     $http_authorization;   # JWT 透传
    proxy_set_header Upgrade           $http_upgrade;         # WebSocket 握手
    proxy_set_header Connection        $connection_upgrade;   # WebSocket 握手
    client_max_body_size 32m;

    # ---- 认证：登录 / 刷新 ----
    location /api/v1/auth/ {
        proxy_pass http://127.0.0.1:8002;
        proxy_read_timeout 30s;
    }

    # ---- 互动 / 积分 / 等级 / 后台配置（ws_api 进程）----
    location /api/v1/interaction/ {
        proxy_pass http://127.0.0.1:8001;
        proxy_read_timeout 60s;      # AI 重试总兜底 15s，留足余量
        proxy_buffering off;
    }
    location /api/v1/points/  { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
    location /api/v1/level/   { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }
    location /api/v1/admin/   { proxy_pass http://127.0.0.1:8001; proxy_read_timeout 60s; }

    # ---- WebSocket：聊天 + 积分/等级/断签事件推送 ----
    location /ws/ {
        proxy_pass http://127.0.0.1:8001;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_buffering off;
    }

    # ---- 其余 API：聊天历史 / 记忆 / 城市（http_api 进程）----
    location /api/v1/ {
        proxy_pass http://127.0.0.1:8000;
        proxy_read_timeout 120s;
    }
}
```

> 端口请以你实际 `.env` 里的 `BOT_HTTP_PORT` / `BOT_WS_PORT` 为准；
> 若用 certbot 申请证书：`certbot --nginx -d takishiinabot.top`。

## 5. 生效与自检

```bash
nginx -t && systemctl reload nginx

# 登录取 token（注意：curl 也算一台设备，受 AUTH_MAX_DEVICES 限制）
TOKEN=$(curl -s https://takishiinabot.top/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"你的用户名","password":"你的密码","deviceId":"curl-debug"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessToken"])')

curl -s https://takishiinabot.top/api/v1/level/config      -H "Authorization: Bearer $TOKEN" | head -c 200; echo
curl -s https://takishiinabot.top/api/v1/interaction/items -H "Authorization: Bearer $TOKEN" | head -c 200; echo
curl -s -o /dev/null -w '%{http_code}\n' https://takishiinabot.top/api/v1/points/balance -H "Authorization: Bearer $TOKEN"
```

预期：前两条返回 `{"code":0,...}`（`level/config` 里应能看到 7 个等级与 `makeupCardMax:12`）；
未带 token 时应返回 `401`。若返回 nginx 的 404 HTML，基本都是第 2 节第 1 条（`proxy_pass` 带了路径）
或漏配 location；若返回 `{"detail":{"code":50301,...}}`，说明路由到了 ws_api 但服务未初始化完成。

---

## 6. 服务器上执行的操作（忘记原配置时的定位与修改）

### 6.1 先摸清现状（全部只读，不改任何东西）

```bash
# ① 80/443 与三个后端端口分别被谁占着（root 才能看到进程名）
sudo ss -lntp | grep -E ':(80|443|8000|8001|8002)\b'
#    拿到 pid 后：ps -p <pid> -o args=   → 确认是 ws_api.py / http_api.py / uvicorn main:app

# ② 后端进程是怎么起的（决定后面怎么重启）
ps -ef | grep -E "uvicorn|ws_api|http_api" | grep -v grep
systemctl list-units --type=service --state=running 2>/dev/null | grep -iE "taki|tks|shiina|bot"
supervisorctl status 2>/dev/null
docker ps --format '{{.Names}}\t{{.Image}}\t{{.Ports}}' 2>/dev/null

# ③ 配置文件在哪：所有提到该域名的文件
grep -rln "takishiinabot" /etc/nginx /usr/local/nginx/conf \
     /www/server/panel/vhost/nginx /www/server/nginx/conf 2>/dev/null

# ④ 当前实际生效的分流规则（nginx -T 会带 "# configuration file xxx" 来源标记）
nginx -T 2>/dev/null | grep -nE "^# configuration file|server_name|listen |location |proxy_pass"
```

第 ④ 步会明确告诉你：域名由**哪个文件的哪个 server 块**承载、`/api/v1/` 与 `/ws/` 现在各转发到哪个端口。
若 ① 显示 80/443 不是 nginx 占的，先确认是不是 Caddy / Traefik / 宝塔面板 / Apache 在托管。

### 6.2 更新后端并重启（先做这步，再做 nginx）

新增积分接口在 `ws_api.py` 进程里，**服务器上的代码必须是 `b9fd1d1` 之后的版本且该进程已重启**，
否则 nginx 路由加好了也只会 404。

```bash
cd ~/Taki-Shiina_Bot            # 换成服务器上的实际路径
git pull                        # 目标：包含 b9fd1d1 新增积分系统

# 按 6.1 ② 的结果选一种重启方式
sudo systemctl restart <ws_api 服务名>
sudo supervisorctl restart <ws_api 进程名>
# 裸跑的：kill 旧进程后重启  →  nohup python ws_api.py > ws_api.log 2>&1 &

curl -s http://127.0.0.1:8001/healthz      # {"status":"ok","service":"ws_api"}
```

> `.env` 里原有的 `DATA_ENC_KEY` 必须保留：积分/等级数据沿用同一套 AES-GCM 加密落盘。

### 6.3 用脚本补 nginx 路由（自动定位 + 修改 + 校验）

仓库自带 `Taki_Shiina_Bot/scripts/patch_nginx_gamification.py`：自动找配置文件、挑出 `listen 443 ssl`
的 server 块、写入 location 片段并插入一行 `include`，最后跑 `nginx -t` 并 reload。

```bash
# 先干跑，看清它要改哪个文件、写什么内容
sudo python3 Taki_Shiina_Bot/scripts/patch_nginx_gamification.py --domain takishiinabot.top --dry-run

# 确认无误后执行（ws_api 端口不是 8001 时加 --ws-port）
sudo python3 Taki_Shiina_Bot/scripts/patch_nginx_gamification.py --domain takishiinabot.top --ws-port 8001
```

特性：**幂等**（重复执行不会重复插入）、改前**自动备份**（`<原文件>.bak.日期-时间`）、
`nginx -t` 失败**自动回滚**。补充参数：

```bash
--conf /www/server/panel/vhost/nginx/takishiinabot.top.conf   # 手动指定文件（自动定位不到时）
--root /自定义/nginx/conf                                      # 指定扫描根目录
--no-reload                                                   # 只改不重载
```

### 6.4 端到端自检

```bash
# 401 = 路由通、只是没带 token（正常）；404 = 路由没加成功；502 = 路由通了但 ws_api 没起来
curl -s -o /dev/null -w 'level/config  → %{http_code}\n'  https://takishiinabot.top/api/v1/level/config
curl -s -o /dev/null -w 'points/balance → %{http_code}\n' https://takishiinabot.top/api/v1/points/balance

TOKEN=$(curl -s https://takishiinabot.top/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"你的用户名","password":"你的密码","deviceId":"curl-debug"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["accessToken"])')

curl -s https://takishiinabot.top/api/v1/interaction/items -H "Authorization: Bearer $TOKEN"; echo
curl -s https://takishiinabot.top/api/v1/level/config      -H "Authorization: Bearer $TOKEN" | head -c 200; echo
```

### 6.5 回滚

```bash
sudo cp /etc/nginx/sites-enabled/takishiinabot.top.conf.bak.<时间戳> \
        /etc/nginx/sites-enabled/takishiinabot.top.conf
sudo nginx -t && sudo systemctl reload nginx
# include 片段文件可留可删：留着不影响其它路径，下次重跑脚本会覆盖它
```
