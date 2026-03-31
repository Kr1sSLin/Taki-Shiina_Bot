import google.generativeai as genai

GOOGLE_API_KEY =

try:
    genai.configure(api_key=GOOGLE_API_KEY)

    print("🔍 正在查询可用模型...")
    print("--------------------------------------------------")

    # 列出所有模型
    for m in genai.list_models():
        # 只显示支持生成内容(generateContent)的模型
        if 'generateContent' in m.supported_generation_methods:
            print(f"✅ 发现模型: {m.name}")

    print("--------------------------------------------------")

    # 顺便查一下库的版本
    import importlib.metadata
    try:
        version = importlib.metadata.version("google-generativeai")
        print(f"📦 当前库版本: {version}")
    except:
        print("📦 无法获取库版本")

except Exception as e:
    print(f"❌ 查询失败: {e}")