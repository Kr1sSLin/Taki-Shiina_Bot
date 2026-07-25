import asyncio
import datetime
import os
import random
import re
import uuid
import traceback
from datetime import timedelta, timezone

# 飞书告警模块（可选）
try:
    from alert_sender import send_alert
    ALERT_ENABLED = True
except (ImportError, ValueError):
    ALERT_ENABLED = False


def compute_debounce_window(bot_last, now_utc, debounce_base, debounce_extended, debounce_extend_window):
    if not bot_last:
        return debounce_base

    seconds_since_response = (now_utc - bot_last).total_seconds()
    if seconds_since_response < debounce_extend_window:
        return debounce_extended
    return debounce_base


def extract_timer_instruction(raw_reply):
    timer_pattern = r"\[\[TIMER:(\d{1,2}:\d{2})\|(.*?)\]\]"
    match = re.search(timer_pattern, raw_reply)
    if not match:
        return raw_reply, None, None

    target_time_str = match.group(1)
    reminder_text = match.group(2)
    cleaned_reply = re.sub(timer_pattern, "", raw_reply).strip()
    return cleaned_reply, target_time_str, reminder_text


def create_chat_handler(
    owner_id,
    notify_owner,
    message_buffer,
    debounce_jobs,
    last_activity,
    last_bot_response_time,
    debounce_base,
    debounce_extended,
    debounce_extend_window,
    user_chat_history,
    save_chat_history,
    client,
    get_system_prompt,
    get_weather_str,
    emotional_triggers,
    lore_triggers,
    reminder_job,
    memory_service,
    sanitize_taki_reply,
    inject_emojis,
    logger,
):
    async def extract_memory(user_id, user_text, history):
        try:
            recent_context_str = str(history[-4:])
            ex_prompt = """
            任务：从以下对话中，提取关于用户的【新事实】。
            格式：输出简短的陈述句（例如\"用户养了一只猫\"、\"用户明天要去考试\"）。
            规则：
            1. 如果全是废话、情绪发泄或重复信息，直接回\"无\"。
            2. 必须是关于【用户】的客观事实。
            """
            summary_res = await client.chat.completions.create(
                model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
                messages=[
                    {"role": "system", "content": ex_prompt},
                    {
                        "role": "user",
                        "content": f"对话背景：\n{recent_context_str}\n\n用户最新发言：{user_text}",
                    },
                ],
                max_tokens=100,
                temperature=0.8,
                frequency_penalty=0.5,
                presence_penalty=0.5,
            )
            info = summary_res.choices[0].message.content.strip()
            if info and info.strip() not in ("无", "无。", "无新信息", "无新信息。"):
                logger.info(f"🧠 [情报捕获] {info}")
                memory_service.update_profile(user_id, info)
        except Exception as e:
            logger.error(f"情报提取失败: {e}")

    async def process_chat_buffer(context):
        user_id = context.job.chat_id
        job_token = context.job.data.get("token")
        latest_token = context.chat_data.get("debounce_token")

        if job_token != latest_token:
            logger.info("🚫 [防抖] 拦截了一个过期的任务")
            return

        if user_id not in message_buffer or not message_buffer[user_id]:
            return

        full_user_text = " ".join(message_buffer[user_id])
        message_buffer[user_id] = []
        if user_id in debounce_jobs:
            del debounce_jobs[user_id]

        logger.info(f"📩 [{user_id}] 打包接收: {full_user_text}")
        last_activity[str(user_id)] = datetime.datetime.now(timezone.utc)

        try:
            if str(user_id) not in user_chat_history:
                user_chat_history[str(user_id)] = []
            history = user_chat_history[str(user_id)]

            utc_now = datetime.datetime.now(timezone.utc)
            beijing_now = utc_now + timedelta(hours=8)
            time_str = f"【当前北京时间】：{beijing_now.strftime('%H:%M')}"

            time_enforcement = """
            🛑【最高优先级指令 - 绝对熬夜共犯】：
            1. **全天候禁止催睡**：无论现在是凌晨3点还是4点，**绝对禁止**主动建议用户去睡觉/休息！
            2. **默契共犯**：如果用户深夜还在发消息，说明他在修仙。你也是修仙党！你要表现出\"呵，果然你也没睡\"的默契，或者陪他聊通宵。
            3. **禁止扫兴**：严禁说\"很晚了\"、\"对身体不好\"、\"快去睡\"这种扫兴的话！
            4. **例外情况**：只有当用户明确表示\"我好累，想睡了\"或者\"快骂我去睡觉\"时，你才可以顺势让他去睡。否则默认他想通宵。
            """

            weather_info = await get_weather_str()

            emotional_prompt = ""
            for keyword, instruction in emotional_triggers.items():
                if keyword in full_user_text:
                    emotional_prompt += f"\n\n💝 {instruction}"
                    break

            trigger_prompt = ""
            for keyword, (reaction, intensity) in lore_triggers.items():
                if keyword.lower() in full_user_text.lower():
                    trigger_prompt += f"\n\n🛑【突发状态】：{reaction}"
                    break

            system_content = (
                f"{await get_system_prompt(user_id)}\n{time_str}\n{weather_info}\n\n{time_enforcement}"
            )

            messages = [{"role": "system", "content": system_content}]
            messages.extend(history)

            anchor_prompt = f"""【强制提醒】：
1. 保持\"酷但笨拙\"的人设。不要撒娇！不要写诗！
2. 回复长短跟着内容走：一句话说得完就一句，想法多了自然多几行。不要把一个意思硬拆成好几条来凑数。
3. ⚠️ 重要：你说\"真是的\"的频率太高了！这次回复中严禁使用\"真是的\"这个词！用\"哈？\"、\"受不了你\"、\"拿你没办法\"等词代替。
4. ⚠️ 重要：\"啧\"也不要滥用！\"哈？\"、\"喂\"、\"受不了你\"、\"麻烦死了\"、\"拿你没办法\"都是同等选项，随机选用，不要总选\"啧\"。{emotional_prompt}{trigger_prompt}"""
            messages.append({"role": "system", "content": anchor_prompt})

            force_instruction = ""
            if any(k in full_user_text for k in ["提醒", "叫我", "闹钟", "喊我"]):
                force_instruction = """
                 \n\n【系统强制介入】：
                 检测到用户有提醒需求。你必须在回复末尾（不要在中间）添加隐藏指令：
                 [[TIMER:HH:MM|提醒内容]]
                 - HH:MM 使用24小时制北京时间。
                 - 必须严格遵守此格式，不要使用代码块。
                 """

            messages.append({"role": "user", "content": full_user_text + force_instruction})

            response = await client.chat.completions.create(
                model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"), messages=messages, temperature=0.75
            )
            raw_reply = response.choices[0].message.content

            raw_reply, target_time_str, reminder_text = extract_timer_instruction(raw_reply)

            if target_time_str and reminder_text:

                try:
                    utc_now = datetime.datetime.now(timezone.utc)
                    beijing_now = utc_now + timedelta(hours=8)

                    h, m = map(int, target_time_str.split(":"))
                    target_dt = beijing_now.replace(hour=h, minute=m, second=0, microsecond=0)

                    time_diff = (target_dt - beijing_now).total_seconds()

                    if time_diff < 0:
                        if time_diff > -900:
                            logger.warning(
                                f"⚠️ [自动纠错] AI 生成了过去的时间 ({target_time_str})，判定为延迟，将在5秒后触发。"
                            )
                            target_dt = beijing_now + timedelta(seconds=5)
                            delay_seconds = 5.0
                        else:
                            target_dt += timedelta(days=1)
                            delay_seconds = (target_dt - beijing_now).total_seconds()
                    else:
                        delay_seconds = time_diff

                    context.job_queue.run_once(
                        reminder_job,
                        delay_seconds,
                        chat_id=user_id,
                        data={"chat_id": user_id, "text": reminder_text},
                    )

                    local_target_str = target_dt.strftime("%Y-%m-%d %H:%M:%S")
                    logger.info(f"⏰ [闹钟设置成功] 将在 {local_target_str} (北京时间) 提醒: {reminder_text}")

                except Exception as e:
                    logger.error(f"❌ 设置闹钟失败: {e}")

            history.append({"role": "user", "content": full_user_text})
            history.append({"role": "assistant", "content": raw_reply})
            if len(history) > 300:
                user_chat_history[str(user_id)] = history[-300:]
            save_chat_history()

            last_bot_response_time[str(user_id)] = datetime.datetime.now(timezone.utc)

            if len(full_user_text) > 5:
                asyncio.create_task(extract_memory(user_id, full_user_text, history))

            typing_delay = len(raw_reply) * 0.1 + random.uniform(0.5, 1.0)
            typing_delay = min(typing_delay, 8.0)
            await context.bot.send_chat_action(chat_id=user_id, action="typing")
            await asyncio.sleep(typing_delay)

            filtered_reply = sanitize_taki_reply(raw_reply)
            final_text = inject_emojis(filtered_reply)

            messages_to_send = [msg.strip() for msg in final_text.split("\n") if msg.strip()]

            for msg in messages_to_send:
                await context.bot.send_chat_action(chat_id=user_id, action="typing")
                delay = 1.0 + len(msg) * 0.1 + random.uniform(0.5, 1.5)
                if delay > 4.0:
                    delay = 4.0
                await asyncio.sleep(delay)
                await context.bot.send_message(chat_id=user_id, text=msg)

        except Exception as e:
            logger.error(f"❌ Chat Error: {e}")
            # 发送飞书告警
            if ALERT_ENABLED:
                try:
                    error_detail = f"聊天处理异常\n\n用户ID: {user_id}\n用户消息: {full_user_text[:100]}...\n\n错误类型: {type(e).__name__}\n错误信息: {str(e)}\n\n堆栈追踪:\n{traceback.format_exc()}"
                    send_alert(error_detail)
                except Exception:
                    pass

    async def chat(update, context):
        if not update.message:
            return

        user_id = update.effective_chat.id
        user_text = update.message.text
        user_name = update.effective_user.username or update.effective_user.first_name or "未知"

        if str(user_id) != str(owner_id):
            logger.warning(f"⚠️ [拦截] 陌生人 {user_name}({user_id}) 试图说话: {user_text}")

            if user_text:
                asyncio.create_task(
                    notify_owner(
                        f"⚠️ **[安全警告]**\n"
                        f"有陌生人试图和立希搭话！\n"
                        f"ID: `{user_id}`\n"
                        f"昵称: {user_name}\n"
                        f"内容: {user_text}"
                    )
                )
            return

        if not user_text:
            return

        msg_id = update.message.message_id
        if context.chat_data.get("last_user_msg_id") == msg_id:
            logger.info(f"🔁 [去重] 忽略重复消息 user={user_id}, msg_id={msg_id}")
            return
        context.chat_data["last_user_msg_id"] = msg_id

        if user_id not in message_buffer:
            message_buffer[user_id] = []
        message_buffer[user_id].append(user_text)

        current_token = str(uuid.uuid4())
        context.chat_data["debounce_token"] = current_token

        if user_id in debounce_jobs:
            try:
                debounce_jobs[user_id].schedule_removal()
            except Exception:
                pass

        bot_last = last_bot_response_time.get(str(user_id))
        debounce_window = compute_debounce_window(
            bot_last,
            datetime.datetime.now(timezone.utc),
            debounce_base,
            debounce_extended,
            debounce_extend_window,
        )

        new_job = context.job_queue.run_once(
            process_chat_buffer,
            debounce_window,
            chat_id=user_id,
            data={"token": current_token},
        )
        debounce_jobs[user_id] = new_job

    return chat
