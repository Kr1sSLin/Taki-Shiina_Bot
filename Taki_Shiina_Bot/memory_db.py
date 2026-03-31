import sqlite3

class MemoryManager:
    def __init__(self, db_name="bot_memory.db"):
        self.conn = sqlite3.connect(db_name, check_same_thread=False)
        self.create_tables()

    def create_tables(self):
        cursor = self.conn.cursor()
        # 表1：短期对话流水（用于保持上下文连贯）
        cursor.execute('''
            CREATE TABLE IF NOT EXISTS chat_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER,
                role TEXT,
                content TEXT,
                timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
            )
        ''')
        # 表2：长期用户画像（用于存储事实和性格偏好）
        cursor.execute('''
            CREATE TABLE IF NOT EXISTS user_profiles (
                user_id INTEGER PRIMARY KEY,
                summary TEXT
            )
        ''')
        self.conn.commit()

    def add_message(self, user_id, role, content):
        cursor = self.conn.cursor()
        cursor.execute("INSERT INTO chat_history (user_id, role, content) VALUES (?, ?, ?)",
                       (user_id, role, content))
        self.conn.commit()

    def get_recent_history(self, user_id, limit=10):
        cursor = self.conn.cursor()
        cursor.execute("""
            SELECT role, content FROM chat_history 
            WHERE user_id = ? 
            ORDER BY id DESC LIMIT ?
        """, (user_id, limit))
        rows = cursor.fetchall()
        return [{"role": row[0], "content": row[1]} for row in rows][::-1]

    def get_profile(self, user_id):
        cursor = self.conn.cursor()
        cursor.execute("SELECT summary FROM user_profiles WHERE user_id = ?", (user_id,))
        result = cursor.fetchone()
        return result[0] if result else "新朋友"

def update_profile(self, user_id, new_info):
        """
        这个方法现在只负责把新信息存入，不直接覆盖。
        为了保持逻辑解耦，真正的‘AI总结’我们放在 bot.py 里执行，
        这里只负责最稳妥的存储。
        """
        cursor = self.conn.cursor()
        cursor.execute("SELECT summary FROM user_profiles WHERE user_id = ?", (user_id,))
        result = cursor.fetchone()
        old_summary = result[0] if result else ""

        # 将新情报追加到旧情报后面，用分号隔开
        # 限制长度，防止数据库和 Prompt 爆炸
        combined_summary = f"{old_summary}; {new_info}".strip("; ")
        if len(combined_summary) > 800:
            combined_summary = combined_summary[-800:]

        cursor.execute("""
            INSERT INTO user_profiles (user_id, summary) VALUES (?, ?)
            ON CONFLICT(user_id) DO UPDATE SET summary = ?
        """, (user_id, combined_summary, combined_summary))
        self.conn.commit()