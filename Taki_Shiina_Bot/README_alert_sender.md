# 飞书告警模块使用说明

## 概述
`alert_sender.py` 是立希Bot的飞书告警模块，用于在Bot出现错误时向飞书群发送告警消息。

## 功能特性
- ✅ **智能告警**：包含时间戳、服务器信息、Python版本
- ✅ **限流保护**：防止频繁发送（60秒间隔）
- ✅ **多域名支持**：自动适配国内外飞书版本
- ✅ **错误处理**：完善的异常捕获和错误提示
- ✅ **简单易用**：一行代码即可发送告警

## 部署配置

### 1. 安装依赖
```bash
pip install requests
```

### 2. 创建飞书机器人
1. 打开飞书客户端 → 进入告警接收群
2. 群设置 → 群机器人 → 添加机器人 → 自定义机器人
3. 设置名称（如"立希Bot监控"）
4. 复制生成的 Webhook URL

### 3. 配置环境变量
```bash
# Linux/macOS
export FEISHU_WEBHOOK_URL="https://open.larksuite.com/open-apis/bot/v2/hook/your-token"

# 或者只设置token（系统自动补全域名）
export FEISHU_WEBHOOK_URL="your-webhook-token"
```

### 4. 测试验证
```bash
python3 alert_sender.py
```

## 使用方法

### 基础用法
```python
from alert_sender import send_alert

# 发送告警
success = send_alert("数据库连接失败")

if success:
    print("告警发送成功")
else:
    print("告警发送失败")
```

### 在异常处理中使用
```python
from alert_sender import send_alert
import traceback

try:
    # 你的代码逻辑
    risky_operation()
except Exception as e:
    # 发送详细的错误信息
    error_message = f"{str(e)}\n\n堆栈追踪:\n{traceback.format_exc()}"
    send_alert(error_message)
    raise  # 继续抛出异常
```

## 消息格式示例
```
🚨 [立希Bot] 严重错误报警

⏰ 错误时间: 2026-04-01 14:55:00
🖥️ 服务器: your-hostname (Linux 5.15.0)
🐍 Python版本: 3.11.2

📋 错误详情:
ConnectionError: Failed to connect to database
```

## 配置选项

### 修改发送间隔
编辑 `alert_sender.py` 中的 `MIN_SEND_INTERVAL`：
```python
MIN_SEND_INTERVAL = 120  # 改为120秒间隔
```

### 支持的域名
系统自动选择最佳域名：
- `https://open.larksuite.com` - 海外版Lark（优先）
- `https://open.feishu.cn` - 国内版飞书
- `https://open.feishu.net` - 备用域名
- `https://open-sg.feishu.cn` - 新加坡节点

## 故障排查

### 常见问题
1. **环境变量未设置**
   - 错误：`ValueError: 未配置飞书 Webhook URL`
   - 解决：设置 `FEISHU_WEBHOOK_URL` 环境变量

2. **requests库缺失**
   - 错误：`ImportError: requests 库未安装`
   - 解决：`pip install requests`

3. **网络连接问题**
   - 错误：`网络请求异常`
   - 解决：检查网络连接，确认防火墙允许HTTPS出站

4. **Webhook URL无效**
   - 错误：`HTTP请求失败: 404`
   - 解决：重新获取Webhook URL，确认机器人未被删除

## API参考

### `send_alert(error_message)`
发送告警信息到飞书群

**参数：**
- `error_message` (str): 错误信息内容

**返回值：**
- `bool`: 发送成功返回 `True`，失败返回 `False`

**示例：**
```python
success = send_alert("这是一个测试告警")
```

### `test_alert()`
发送测试告警消息

**返回值：**
- `bool`: 发送成功返回 `True`，失败返回 `False`

## 版本历史
- v1.0: 完成从SMTP邮件告警到飞书Webhook告警的迁移
- 支持自动域名选择和智能消息格式化