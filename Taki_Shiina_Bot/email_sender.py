import smtplib
from email.mime.text import MIMEText
from email.header import Header

# ================= 📧 邮件配置 =================
# 如果是路由器挂载的邮件服务，填路由器的 IP；如果是 Gmail/QQ，填对应的 smtp
SMTP_SERVER =  # 例如: smtp.gmail.com
SMTP_PORT =              # SSL 端口通常是 465
SENDER_EMAIL =
PASSWORD =  # 邮箱授权码 (不是登录密码)
RECEIVER_EMAIL = # 接收报警的邮箱

def send_alert_email(error_message):
    try:
        subject = "🚨 [立希Bot] 严重错误报警"
        content = f"立希机器人刚刚捕获到一个异常，请检查服务器！\n\n错误详情:\n{error_message}"

        message = MIMEText(content, 'plain', 'utf-8')
        message['From'] = Header("Taki_Bot_Monitor", 'utf-8')
        message['To'] = Header("Admin", 'utf-8')
        message['Subject'] = Header(subject, 'utf-8')

        # 连接 SMTP 服务器
        server = smtplib.SMTP_SSL(SMTP_SERVER, SMTP_PORT)
        server.login(SENDER_EMAIL, PASSWORD)
        server.sendmail(SENDER_EMAIL, RECEIVER_EMAIL, message.as_string())
        server.quit()
        print("✅ 报警邮件已发送")
    except Exception as e:
        print(f"❌ 邮件发送失败: {e}")
