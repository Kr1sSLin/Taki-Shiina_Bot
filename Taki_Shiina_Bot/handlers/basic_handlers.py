import asyncio
import os

import PIL.Image


def create_set_city_command(weather_service):
    async def set_city_command(update, context):
        if not context.args:
            await update.message.reply_text("指令格式：/city 城市拼音")
            return
        new_city = context.args[0]
        weather_service.set_city(new_city)
        await update.message.reply_text(f"知道了。定位切到 {new_city} 了。")

    return set_city_command


def create_check_memory_command(memory_service):
    async def check_memory_command(update, context):
        user_id = update.effective_chat.id
        memory_content = memory_service.get_profile(user_id)
        await update.message.reply_text(
            f"📓 **[立希的观察日记]**\n\n{memory_content}",
            parse_mode="Markdown",
        )

    return check_memory_command


def create_handle_photo_handler(
    owner_id,
    user_chat_history,
    genai_client,
    sanitize_taki_reply,
    inject_emojis,
    logger,
):
    async def handle_photo(update, context):
        user_id = update.effective_chat.id
        if str(user_id) != str(owner_id):
            return

        try:
            await context.bot.send_chat_action(chat_id=user_id, action="typing")
            history = user_chat_history.get(str(user_id), [])
            recent_context = ""
            if history:
                for msg in history[-3:]:
                    recent_context += f"{msg['role']}: {msg['content']}\n"

            caption = update.message.caption or ""
            photo_file = await update.message.photo[-1].get_file()
            file_path = "temp_user_photo.jpg"
            await photo_file.download_to_drive(file_path)

            img = PIL.Image.open(file_path)
            img.load()
            os.remove(file_path)

            vision_prompt = (
                f"你是椎名立希。用户发了图，说：'{caption}'。"
                f"语境：{recent_context}。请以立希口吻回复。"
            )

            if genai_client is None:
                await update.message.reply_text("看不清。")
                return

            response = await asyncio.to_thread(
                genai_client.models.generate_content,
                model="gemini-2.5-flash",
                contents=[vision_prompt, img],
            )

            filtered_reply = sanitize_taki_reply(response.text)
            await update.message.reply_text(inject_emojis(filtered_reply))
        except Exception as e:
            logger.error(f"❌ 识图详细报错: {e}")
            await update.message.reply_text("看不清。")

    return handle_photo
