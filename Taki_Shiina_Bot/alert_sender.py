import os
import platform
import sys
from datetime import datetime
import json

# 加载 .env 文件
try:
    from dotenv import load_dotenv
    _base_dir = os.path.dirname(os.path.abspath(__file__))
    load_dotenv(os.path.join(_base_dir, '.env'))
except ImportError:
    pass  # 如果没有 python-dotenv，继续使用系统环境变量

# 动态导入检查
try:
    import requests
except ImportError:
    print("❌ 缺少 requests 库，请执行: pip install requests")
    raise ImportError("requests 库未安装")

# ================= 🔧 飞书配置 =================
# 从环境变量读取 Webhook URL
FEISHU_WEBHOOK_URL = os.getenv("FEISHU_WEBHOOK_URL")

# 如果环境变量中只设置了 token，自动补全域名
if FEISHU_WEBHOOK_URL and not FEISHU_WEBHOOK_URL.startswith('http'):
    # 可能的飞书域名（按优先级排序）
    FEISHU_DOMAINS = [
        "https://open.larksuite.com",       # 海外版 Lark（优先尝试）
        "https://open.feishu.cn",           # 中国大陆
        "https://open.feishu.net",          # 备用域名
        "https://open-sg.feishu.cn",        # 新加坡节点
    ]
    
    # 保存原始 token
    webhook_token = FEISHU_WEBHOOK_URL
    FEISHU_WEBHOOK_URL = f"{FEISHU_DOMAINS[0]}/open-apis/bot/v2/hook/{webhook_token}"
    print(f"🔧 自动补全 Webhook URL: {FEISHU_DOMAINS[0]}")

# 配置验证 - 改为警告而非抛出异常
if not FEISHU_WEBHOOK_URL:
    print("⚠️ 未配置飞书 Webhook URL，告警功能已禁用")
    print("请在 .env 文件或环境变量中设置: FEISHU_WEBHOOK_URL=你的webhook_url")

# ================= ⏱️ 限流控制 =================
_last_send_time = 0
MIN_SEND_INTERVAL = 60  # 最小发送间隔：60秒

def _get_system_info():
    """获取系统信息"""
    try:
        hostname = platform.node()
        system = f"{platform.system()} {platform.release()}"
        python_ver = f"{sys.version.split()[0]}"
        return hostname, system, python_ver
    except Exception:
        return "Unknown", "Unknown", "Unknown"

def _should_send_alert():
    """检查是否应该发送告警（防止频繁发送）"""
    global _last_send_time
    current_time = datetime.now().timestamp()
    
    if current_time - _last_send_time < MIN_SEND_INTERVAL:
        time_remaining = int(MIN_SEND_INTERVAL - (current_time - _last_send_time))
        print(f"⚠️ 告警发送间隔过短，还需等待 {time_remaining} 秒")
        return False
    
    _last_send_time = current_time
    return True

def send_alert(error_message, bypass_rate_limit=False):
    """
    发送告警信息到飞书群
    
    Args:
        error_message (str): 错误信息内容
        bypass_rate_limit (bool): 是否跳过限流检查（用于启动通知等场景）
        
    Returns:
        bool: 发送成功返回 True，失败返回 False
    """
    # 检查是否已配置
    if not FEISHU_WEBHOOK_URL:
        print("⚠️ 飞书告警未配置，跳过发送")
        return False
    
    # 限流检查（可选跳过）
    if not bypass_rate_limit and not _should_send_alert():
        return False
    
    try:
        # 获取系统信息
        hostname, system, python_ver = _get_system_info()
        current_time = datetime.now().strftime('%Y-%m-%d %H:%M:%S')
        
        # 构造简单的纯文本消息
        alert_text = (
            f"🚨 [立希Bot] 严重错误报警\n\n"
            f"⏰ 错误时间: {current_time}\n"
            f"🖥️ 服务器: {hostname} ({system})\n"
            f"🐍 Python版本: {python_ver}\n\n"
            f"📋 错误详情:\n{str(error_message)}"
        )
        
        # 使用纯文本格式
        payload = {
            "msg_type": "text",
            "content": {
                "text": alert_text
            }
        }
        
        # 发送 HTTP 请求
        headers = {"Content-Type": "application/json"}
        response = requests.post(
            FEISHU_WEBHOOK_URL,
            json=payload,
            headers=headers,
            timeout=10
        )
        
        # 检查响应状态
        if response.status_code == 200:
            result = response.json()
            if result.get("code") == 0:
                print("✅ 飞书告警已发送")
                return True
            else:
                print(f"❌ 飞书API返回错误: {result}")
                return False
        else:
            print(f"❌ HTTP请求失败: {response.status_code}")
            return False
            
    except requests.exceptions.Timeout:
        print("❌ 飞书请求超时，请检查网络连接")
        return False
    except requests.exceptions.RequestException as e:
        print(f"❌ 网络请求异常: {e}")
        return False
    except Exception as e:
        print(f"❌ 发送告警时发生未知错误: {e}")
        return False

def test_alert():
    """测试函数 - 发送测试告警"""
    test_message = "这是一条测试告警消息，用于验证飞书通知功能是否正常工作。"
    print("📤 正在发送测试告警...")
    
    success = send_alert(test_message)
    if success:
        print("✅ 测试完成，请检查飞书群是否收到消息")
    else:
        print("❌ 测试失败，请检查配置")
    
    return success

# ================= 🧪 测试入口 =================
if __name__ == "__main__":
    print("🔧 飞书告警模块测试")
    if FEISHU_WEBHOOK_URL:
        print(f"📡 Webhook 已配置")
        test_alert()
    else:
        print("❌ 环境变量 FEISHU_WEBHOOK_URL 未设置")
        print("请设置环境变量后重试")