#!/usr/bin/env python3
"""部署前自检：确认当前 .env 里的 DATA_ENC_KEY 能解开既有加密数据文件。

读失败（密钥不匹配 / 文件损坏）时退出码非 0，且**不写任何文件**。
用法：  cd <项目根> && python3 check_data_key.py chat_timeline.json memory_timeline.json user_memories.json
"""
import base64, json, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    from dotenv import load_dotenv
    load_dotenv()
except Exception:
    pass

from cryptography.hazmat.primitives.ciphers.aead import AESGCM

def b64d(raw):
    return base64.urlsafe_b64decode(raw + "=" * (-len(raw) % 4))

raw_key = (os.getenv("DATA_ENC_KEY") or "").strip()
if not raw_key:
    print("✗ DATA_ENC_KEY 未配置（.env 缺失？）")
    sys.exit(2)
try:
    key = b64d(raw_key)
except Exception:
    try:
        key = bytes.fromhex(raw_key)
    except Exception:
        print("✗ DATA_ENC_KEY 格式错误（需 base64 或 hex）")
        sys.exit(2)
if len(key) not in (16, 24, 32):
    print(f"✗ DATA_ENC_KEY 长度无效：{len(key)} 字节（需 16/24/32）")
    sys.exit(2)

bad = 0
for path in sys.argv[1:]:
    if not os.path.exists(path):
        print(f"- {path}: 不存在（跳过）")
        continue
    try:
        payload = json.load(open(path, encoding="utf-8"))
    except Exception as exc:
        print(f"✗ {path}: JSON 解析失败 → {exc}")
        bad += 1
        continue
    if not (isinstance(payload, dict) and payload.get("ver") == 1 and payload.get("alg") == "AESGCM"):
        print(f"- {path}: 明文（未加密），当前代码可直接读")
        continue
    try:
        plain = AESGCM(key).decrypt(b64d(str(payload["nonce"])), b64d(str(payload["ciphertext"])), None)
        data = json.loads(plain.decode("utf-8"))
        n = len(data) if hasattr(data, "__len__") else "?"
        print(f"✓ {path}: 解压成功，条目数={n}")
    except Exception as exc:
        print(f"✗ {path}: **解密失败** → {type(exc).__name__}: {exc}")
        bad += 1

print(f"\n结果：{'全部可读 ✅' if bad == 0 else f'{bad} 个文件不可读 ❌（先不要重启/部署，先恢复正确的 DATA_ENC_KEY）'}")
sys.exit(1 if bad else 0)
