import asyncio
import datetime
import os
import random
from datetime import timedelta, timezone


def create_notify_owner(monitor_bot_token, owner_id, logger):
    async def notify_owner(text):
        if not monitor_bot_token or not owner_id:
            return
        try:
            from telegram import Bot

            async with Bot(token=monitor_bot_token) as bot:
                await bot.send_message(chat_id=owner_id, text=text)
        except Exception as e:
            logger.error(f"❌ 监控通知失败: {e}")

    return notify_owner


def create_startup_notification(notify_owner):
    async def startup_notification(context):
        utc_now = datetime.datetime.now(timezone.utc)
        beijing_now = utc_now + timedelta(hours=8)
        await notify_owner(
            f"✅ 立希已上线\n"
            f"🕐 启动时间：{beijing_now.strftime('%Y-%m-%d %H:%M')}\n"
            f"📋 早安窗口：09:00–12:00\n"
            f"🌙 深夜窗口：23:00–01:00"
        )

    return startup_notification


def create_reminder_job(logger):
    async def reminder_job(context):
        job_data = context.job.data
        chat_id = job_data["chat_id"]
        text = job_data["text"]

        try:
            await context.bot.send_message(chat_id=chat_id, text=f"⏰ {text}")
            logger.info(f"✅ [闹钟触发] 给 {chat_id} 发送了: {text}")
        except Exception as e:
            logger.error(f"❌ 闹钟发送失败: {e}")

    return reminder_job


def create_active_greeting_job(
    last_activity,
    notify_owner,
    get_system_prompt,
    client,
    sanitize_taki_reply,
    inject_emojis,
    user_chat_history,
    save_chat_history,
    logger,
):
    async def active_greeting_job(context):
        chat_id = context.job.chat_id
        job_data = context.job.data

        if isinstance(job_data, dict):
            scenario_type = job_data["scenario"]
        else:
            scenario_type = job_data

        user_last = last_activity.get(str(chat_id))
        if user_last:
            hours_since_active = (
                datetime.datetime.now(timezone.utc) - user_last
            ).total_seconds() / 3600
            if hours_since_active < 2:
                logger.info(
                    f"💬 [{scenario_type}] 用户 {hours_since_active:.1f}小时前有活动，跳过本次问候"
                )
                asyncio.create_task(
                    notify_owner(
                        f"💬 [{scenario_type}] 你 {hours_since_active:.1f}小时前发过消息，本次问候已取消"
                    )
                )
                return

        if random.random() < 0.2:
            logger.info(
                f"🎲 [随机跳过] 立希今天决定不发 {scenario_type} 问候了 (偷懒中...)"
            )
            asyncio.create_task(notify_owner(f"🎲 [{scenario_type}] 立希今天偷懒了，跳过问候"))
            return

        utc_now = datetime.datetime.now(timezone.utc)
        beijing_now = utc_now + timedelta(hours=8)
        current_time_str = beijing_now.strftime("%H:%M")

        morning_scripts = [
            "【情境】：昨晚没睡好，有严重的起床气，说话很冲，但其实是想让用户哄。",
            "【情境】：起得很早，正在喝咖啡/抹茶，心情意外地不错，稍微有点温柔。",
            "【情境】：睡过头了！！非常慌张，发消息的时候嘴里好像还叼着面包。",
            "【情境】：不想起床，想赖床，发消息撒娇说'能不能再睡五分钟'。",
            "【情境】：外面下雨/天气不好，心情低落，嘟囔着不想出门。",
            "【情境】：只是单纯地想念用户了，醒来第一件事就是想确认他在不在。",
        ]

        night_scripts = [
            "【情境】：正戴着耳机专注于写代码/写歌词，发现用户发消息，摘下一只耳机随口回应，完全没有要睡的意思。",
            "【情境】：刚刚开了一罐新的能量饮料，眼神死死盯着屏幕，漫不经心地问用户'你那边进度怎么样'。",
            "【情境】：因为卡在某个Bug/乐段上很烦躁，看到用户还在，稍微得到了一点安慰，嘟囔着'既然醒着就陪我再耗一会儿'。",
            "【情境】：看了一眼现在的确切时间，冷笑一声'呵，这个点了还没倒下吗？体力不错嘛'。",
            "【情境】：突然感到饿了，问用户'喂，便利店还开着吗'，企图拉用户一起吃夜宵。",
            "【情境】：只有在深夜才展露出的坦率，安静地打字说'只有这个时候世界才安静点...你不睡挺好的'。",
        ]

        if scenario_type == "morning":
            selected_script = random.choice(morning_scripts)
            base_instruction = f"现在是北京时间 {current_time_str}。作为立希给用户发早安。"
        else:
            selected_script = random.choice(night_scripts)
            base_instruction = f"""
            现在是北京时间 {current_time_str} (深夜)。
            用户还没睡。作为立希，不要发\"晚安\"（因为发了晚安话题就结束了）。
            你要发一条消息确认他在干什么，或者吐槽他怎么还醒着，并表示你也还醒着，可以继续陪他。
            """

        final_instruction = f"""
        {base_instruction}

        本次随机到的灵感剧本：
        {selected_script}

        【⚠️ 强制逻辑修正】：
        1. **时间一致性**：上述\"灵感剧本\"仅供参考！如果剧本里暗示了时间（比如\"看了一眼时间\"），你必须结合【当前北京时间 {current_time_str}】来生成台词。
           - 如果现在是 00:00，就说\"才12点\"。
           - 如果现在是 03:00，就说\"都3点了\"。
           - **严禁**出现\"明明是12点却说3点\"的情况！
        2. 不要暴露你在扮演，直接进入角色说话。
        3. 语气要符合剧本的情境，且如果情境是匆忙或困倦，句子要短、碎！
        """

        try:
            response = await client.chat.completions.create(
                model=os.getenv("DEEPSEEK_MODEL", "deepseek-v4-pro"),
                messages=[
                    {
                        "role": "system",
                        "content": await get_system_prompt(chat_id)
                        + "\n\n🎯 当前任务: "
                        + final_instruction,
                    }
                ],
                temperature=0.85,
            )
            raw_reply = response.choices[0].message.content
            filtered_reply = sanitize_taki_reply(raw_reply)
            final_text = inject_emojis(filtered_reply)

            messages_to_send = [msg.strip() for msg in final_text.split("\n") if msg.strip()]

            for msg in messages_to_send:
                await context.bot.send_chat_action(chat_id=chat_id, action="typing")
                delay = 1.0 + len(msg) * 0.1 + random.uniform(0.5, 1.5)
                if delay > 4.0:
                    delay = 4.0
                await asyncio.sleep(delay)
                await context.bot.send_message(chat_id=chat_id, text=msg)

            if str(chat_id) not in user_chat_history:
                user_chat_history[str(chat_id)] = []
            history = user_chat_history[str(chat_id)]
            history.append({"role": "assistant", "content": raw_reply})
            if len(history) > 300:
                user_chat_history[str(chat_id)] = history[-300:]
            save_chat_history()

            logger.info(f"✅ [主动问候] 发送成功，剧本: {selected_script}")
            asyncio.create_task(
                notify_owner(f"✅ [{scenario_type}] 问候发送成功\n📋 剧本：{selected_script[:30]}…")
            )

        except Exception as e:
            logger.error(f"❌ 主动问候失败: {e}")
            asyncio.create_task(notify_owner(f"❌ [{scenario_type}] 主动问候异常：{e}"))

    return active_greeting_job


def create_schedule_random_greeting(notify_owner, active_greeting_job, logger):
    async def schedule_random_greeting(context):
        scenario_type = context.job.data["scenario"]
        window_minutes = context.job.data["window_minutes"]
        chat_id = context.job.chat_id

        delay_seconds = random.randint(0, window_minutes * 60)

        utc_now = datetime.datetime.now(timezone.utc)
        beijing_now = utc_now + timedelta(hours=8)
        trigger_time = beijing_now + timedelta(seconds=delay_seconds)
        logger.info(
            f"🎲 [{scenario_type}] 问候已安排在 {trigger_time.strftime('%H:%M')} 发送（{delay_seconds // 60} 分钟后）"
        )
        asyncio.create_task(
            notify_owner(
                f"🎲 [{scenario_type}] 问候抽签结果：{trigger_time.strftime('%H:%M')} 发送（{delay_seconds // 60} 分钟后）"
            )
        )

        context.job_queue.run_once(
            active_greeting_job,
            delay_seconds,
            chat_id=chat_id,
            data={"scenario": scenario_type, "scheduled_at": datetime.datetime.now(timezone.utc)},
        )

    return schedule_random_greeting


def create_hourly_scene_update(
    scene_chat_idle_seconds,
    last_activity,
    generate_scene,
    scene_cache,
    logger,
):
    async def hourly_scene_update(context):
        chat_id = context.job.chat_id
        now = datetime.datetime.now(timezone.utc)

        user_last = last_activity.get(str(chat_id))
        seconds_since_active = (now - user_last).total_seconds() if user_last else float("inf")

        if seconds_since_active < scene_chat_idle_seconds:
            beijing_now = now + timedelta(hours=8)
            logger.info(
                f"🕐 [场景等待] 用户 {seconds_since_active/60:.1f} 分钟前有活动，"
                f"等待聊天结束后再切换（{beijing_now.strftime('%H:%M')}）"
            )
            context.job_queue.run_once(hourly_scene_update, when=60, chat_id=chat_id)
            return

        scene_text = await generate_scene(chat_id)
        next_hour = (now + timedelta(hours=1)).replace(minute=0, second=0, microsecond=0)
        scene_cache[chat_id] = {"scene": scene_text, "expires_at": next_hour}

        beijing_now = now + timedelta(hours=8)
        logger.info(f"🕐 [整点场景] {beijing_now.strftime('%H:%M')} 场景已更新，下次整点过期")

    return hourly_scene_update
