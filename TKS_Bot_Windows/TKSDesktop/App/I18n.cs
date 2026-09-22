using System.Globalization;

namespace TKSDesktop.App;

/// <summary>
/// 界面文案访问（PRD NFR-W-10 / W-P7 / V-W-S8）。
///
/// ⚠️ **界面文案不得硬编码中文字符串**：全部走本资源层，代码只引用 key。
/// ⚠️ 首版仅 <c>zh-CN</c>。
/// ⚠️ 未知 key **必须**回退为 key 本身（不得返回空白、不得抛错），便于机械校验发现缺失。
/// </summary>
public static class I18n
{
    private static readonly Dictionary<string, string> ZhCn = new(StringComparer.Ordinal)
    {
        /* ---- 通用 ---- */
        ["app.name"] = "TKS Desktop",
        ["common.ok"] = "确定",
        ["common.cancel"] = "取消",
        ["common.confirm"] = "确认",
        ["common.close"] = "关闭",
        ["common.retry"] = "重试",
        ["common.copy"] = "复制",
        ["common.delete"] = "删除",
        ["common.save"] = "保存",
        ["common.refresh"] = "刷新",
        ["common.loading"] = "加载中…",
        ["common.empty"] = "暂无数据",
        ["common.copied"] = "已复制",
        ["common.unknown"] = "未知",
        ["common.offlineData"] = "离线数据，最后更新于 {0}",

        /* ---- 登录 ---- */
        ["login.title"] = "登录",
        ["login.username"] = "用户名",
        ["login.password"] = "密码",
        ["login.submit"] = "登录",
        ["login.submitting"] = "登录中…",
        ["login.failed"] = "登录失败，请重试",
        ["login.sessionExpired"] = "登录已过期，请重新登录",
        ["login.credentialCorrupted"] = "登录状态已失效，请重新登录",
        ["login.credentialNotPersisted"] = "无法安全保存登录状态，本次运行不会持久化凭据",
        ["login.idleExpired"] = "长时间未使用，请重新登录",
        ["login.kickedOut"] = "该账号已在其他设备登录",

        /* ---- 连接状态（FR-W-CONN-5） ---- */
        ["conn.status.connecting"] = "连接中",
        ["conn.status.connected"] = "已连接",
        ["conn.status.reconnecting"] = "重连中（第 {0} 次）",
        ["conn.status.disconnected"] = "已断开",
        ["conn.status.refreshing"] = "正在续签登录态",
        ["conn.status.degraded"] = "已降级（重连已达上限）",
        ["conn.reconnect"] = "重新连接",
        ["conn.reconnect.ok"] = "已重新连接",
        ["conn.reconnect.manual.fullsync"] = "手动重连后将执行全量同步",

        /* ---- 聊天（FR-W-CHAT-*） ---- */
        ["chat.input.placeholder"] = "说点什么…",
        ["chat.send"] = "发送",
        ["chat.notConnected"] = "未连接到服务器",
        ["chat.queued"] = "已排队（{0} 秒内合并）",
        ["chat.typing"] = "立希正在输入…",
        ["chat.typing.vision"] = "立希正在看图…",
        ["chat.typing.generating"] = "立希正在组织语言…",
        ["chat.typing.interaction"] = "立希正在反应…",
        ["chat.typing.interaction_merge"] = "立希正在思考…",
        ["chat.timeout"] = "AI 响应超时，请重试",
        ["chat.connectionLost"] = "连接已断开，消息未能送达",
        ["chat.emptyReply"] = "立希似乎不知道说什么…",
        ["chat.search.placeholder"] = "搜索聊天记录",
        ["chat.search.noResult"] = "没有匹配的记录",
        ["chat.search.tooShort"] = "请至少输入 1 个字符",
        ["chat.clear.confirm"] = "确定清空本地聊天记录吗？服务端数据不受影响。",
        ["chat.clear.done"] = "本地聊天记录已清空",
        ["chat.delete.confirm"] = "确定删除这条本地消息吗？",
        ["chat.degraded.title"] = "降级模式（单次请求）",
        ["chat.degraded.notice"] = "此模式下无流式效果，且不产生积分",
        ["chat.degraded.send"] = "以单次请求模式发送",
        ["chat.degraded.textOnly"] = "单次请求模式仅支持文字，请先移除图片附件",
        ["chat.daySeparator"] = "──── 新的一天 ────",
        ["chat.loadMore"] = "加载更早的消息",

        /* ---- 图片（FR-W-IMG-*） ---- */
        ["image.add"] = "添加图片",
        ["image.paste.empty"] = "剪贴板中没有图片",
        ["image.tooMany"] = "最多只能添加 {0} 张图片",
        ["image.invalidType"] = "仅支持 JPG 与 PNG 格式",
        ["image.tooLarge"] = "单张图片不能超过 {0} MB",
        ["image.totalTooLarge"] = "本次发送的图片总量过大（上限 {0} MB），请减少图片",
        ["image.diskFull"] = "磁盘空间不足，请清理后再试",
        ["image.notFound"] = "图片文件不存在",
        ["image.pathRejected"] = "图片路径不合法，已拒绝加载",
        ["image.save"] = "另存为",
        ["image.zoomIn"] = "放大",
        ["image.zoomOut"] = "缩小",
        ["image.resetZoom"] = "重置缩放",

        /* ---- 通知（FR-W-NOTI-*） ---- */
        ["notification.chat.title"] = "立希",
        ["notification.greeting.morning"] = "立希的早安",
        ["notification.greeting.night"] = "立希的深夜问候",
        ["notification.greeting.title"] = "立希的问候",
        ["notification.reminder.title"] = "消息提醒",
        ["notification.reminder.body"] = "提醒时间：{0}\n{1}",
        ["notification.error.title"] = "立希遇到了问题",
        ["notification.level.title"] = "等级变化",
        ["notification.streak.title"] = "断签预警",
        ["notification.progress.title"] = "进度更新",
        ["notification.action.reply"] = "回复",
        ["notification.action.ignore"] = "忽略",
        ["notification.toast.unavailable"] = "通知中心不可用，已降级为托盘气泡",
        ["notification.portableHint"] = "便携版建议运行一次安装包以获得通知中心集成",

        /* ---- 提醒（FR-W-REM-*） ---- */
        ["reminder.title"] = "已排程提醒",
        ["reminder.empty"] = "没有已排程的提醒",
        ["reminder.cancel"] = "取消提醒",
        ["reminder.cancelled"] = "提醒已取消",
        ["reminder.column.time"] = "提醒时间",
        ["reminder.column.text"] = "内容",

        /* ---- 个人中心 / 积分 / 等级 / 补签卡 ---- */
        ["profile.title"] = "个人中心",
        ["profile.points"] = "积分余额",
        ["profile.level"] = "当前等级",
        ["profile.continuousDays"] = "连续陪伴 {0} 天",
        ["profile.progressToNext"] = "距离 {0} 还差 {1} 天",
        ["profile.maxLevel"] = "已是最高等级",
        ["profile.gapDays"] = "已 {0} 天未对话",
        ["profile.breakDeadline"] = "断签截止日：{0}",
        ["profile.pointsUnaffected"] = "积分余额未受影响",
        ["profile.makeupCards"] = "补签卡：{0} 张",
        ["profile.level.justStarted"] = "刚刚开始",
        ["profile.interaction"] = "互动",
        ["profile.pointsHistory"] = "积分流水",
        ["profile.makeupCalendar"] = "补签日历",
        ["profile.makeupHistory"] = "补签卡流水",
        ["profile.levelConfig"] = "等级说明",

        /* ---- 互动礼物（FR-W-INT-*） ---- */
        ["interaction.title"] = "送立希点什么",
        ["interaction.send"] = "送出",
        ["interaction.insufficient"] = "还差 {0} 积分",
        ["interaction.waiting"] = "正在等待立希反应…",
        ["interaction.timeout"] = "立希好像走神了",
        ["interaction.refunded"] = "积分已退回",
        ["interaction.giftBadge"] = "礼物",
        ["interaction.empty"] = "暂无可用物品",

        /* ---- 积分流水事由（FR-W-PT-3） ---- */
        ["points.reason.DAILY_FIRST_CHAT"] = "每日首次对话",
        ["points.reason.STREAK_3_DAY"] = "连续 3 天陪伴",
        ["points.reason.ANNIVERSARY"] = "纪念日奖励",
        ["points.reason.ITEM_SEND"] = "赠送 {0}",
        ["points.reason.ITEM_REFUND"] = "互动失败退回",
        ["points.reason.ADMIN_ADJUST"] = "管理员调整",
        ["points.reason.unknown"] = "其他变动",
        ["points.column.time"] = "时间",
        ["points.column.reason"] = "事由",
        ["points.column.change"] = "变动",
        ["points.column.balanceAfter"] = "变动后余额",

        /* ---- 等级反馈（FR-W-LV-4/5/5a） ---- */
        ["level.upgrade.title"] = "等级提升！",
        ["level.upgrade.body"] = "恭喜达成「{0}」",
        ["level.restore.title"] = "等级已恢复",
        ["level.restore.body"] = "等级已恢复至 {0}",
        ["level.reset.title"] = "连续陪伴中断",
        ["level.reset.body"] = "连续陪伴中断，等级已重置",
        ["level.reset.guide"] = "可前往补签日历挽回",
        ["level.highest.title"] = "新的最高等级",
        ["streak.warning.title"] = "再不理立希，等级要掉了",
        ["streak.warning.body"] = "已 {0} 天未对话，还剩 {1} 天（截止 {2}）",

        /* ---- 补签卡（FR-W-MC-*） ---- */
        ["makeup.title"] = "补签日历",
        ["makeup.available"] = "可用补签卡：{0} 张",
        ["makeup.rule"] = "每月发放 {0} 张、可跨月结转、上限 {1} 张",
        ["makeup.use"] = "使用补签卡",
        ["makeup.use.confirm"] = "将消耗 1 张补签卡补签 {0}",
        ["makeup.use.success"] = "补签成功",
        ["makeup.noCandidates"] = "没有可补签的日期",
        ["makeup.column.date"] = "日期",
        ["makeup.column.status"] = "状态",
        ["makeup.status.AVAILABLE"] = "可用",
        ["makeup.status.USED"] = "已使用",

        /* ---- 历史与记忆（FR-W-HIS-*） ---- */
        ["history.title"] = "历史与记忆",
        ["history.notifications"] = "立希的通知",
        ["history.facts"] = "记忆档案",
        ["history.unread"] = "未读 {0} 条",
        ["history.markRead"] = "标记已读",
        ["history.markAllRead"] = "全部已读",
        ["history.facts.readonly"] = "记忆由立希自动整理，暂不支持编辑",
        ["history.search.placeholder"] = "搜索记忆",
        ["history.empty"] = "暂无记录",

        /* ---- 设置（FR-W-SET-*） ---- */
        ["settings.title"] = "设置",
        ["settings.theme"] = "主题",
        ["settings.theme.system"] = "跟随系统",
        ["settings.theme.light"] = "日间",
        ["settings.theme.dark"] = "夜间",
        ["settings.city"] = "天气城市",
        ["settings.city.hint"] = "留空表示由服务端根据 IP 自动判断",
        ["settings.server"] = "服务地址",
        ["settings.apiBaseUrl"] = "REST 地址",
        ["settings.wsBaseUrl"] = "WebSocket 地址",
        ["settings.deriveWs"] = "由 REST 地址推导",
        ["settings.testConnection"] = "测试连通性",
        ["settings.testConnection.ok"] = "连接正常",
        ["settings.testConnection.healthUnavailable"] = "服务可达，但健康检查路径不可用",
        ["settings.testConnection.unreachable"] = "无法确认服务可达",
        ["settings.clearLocal"] = "清空本地会话",
        ["settings.autostart"] = "开机自动启动并最小化到托盘",
        ["settings.autostart.current"] = "当前指向：{0}",
        ["settings.closeBehavior"] = "关闭窗口时",
        ["settings.closeBehavior.tray"] = "最小化到托盘",
        ["settings.closeBehavior.quit"] = "直接退出",
        ["settings.closeBehavior.trayUnavailable"] = "托盘不可用，已临时改为关闭即退出",
        ["settings.hotkey"] = "全局快捷键",
        ["settings.hotkey.enabled"] = "启用全局快捷键",
        ["settings.hotkey.inUse"] = "该快捷键已被其他程序占用，请更换",
        ["settings.hotkey.invalid"] = "快捷键格式不正确",
        ["settings.hotkey.ok"] = "快捷键已生效",
        ["settings.sendKey"] = "发送键",
        ["settings.sendKey.enter"] = "Enter 发送",
        ["settings.sendKey.ctrlenter"] = "Ctrl+Enter 发送",
        ["settings.notifications"] = "通知",
        ["settings.notifications.chat"] = "聊天消息",
        ["settings.notifications.greeting"] = "问候",
        ["settings.notifications.reminder"] = "提醒",
        ["settings.notifications.error"] = "错误",
        ["settings.notifications.progress"] = "进度",
        ["settings.dnd"] = "勿扰时段",
        ["settings.dnd.enabled"] = "启用勿扰时段",
        ["settings.dnd.start"] = "开始",
        ["settings.dnd.end"] = "结束",
        ["settings.dnd.hint"] = "勿扰时段内不弹通知，但仍正常收消息",
        ["settings.data"] = "数据与存储",
        ["settings.storage.credentials"] = "凭据存储状态",
        ["settings.storage.encrypted"] = "已加密保存（DPAPI）",
        ["settings.storage.notSaved"] = "未保存——系统无法安全存储",
        ["settings.storage.path"] = "凭据文件路径",
        ["settings.storage.attachments"] = "附件占用空间：{0}",
        ["settings.storage.cleanup"] = "清理超过 N 天的本地附件",
        ["settings.storage.cleanup.done"] = "已清理 {0} 个文件",
        ["settings.storage.cleanup.confirm"] = "确定清理超过 {0} 天的本地附件吗？清理后历史图片将无法显示。",
        ["settings.export"] = "导出聊天记录",
        ["settings.export.json"] = "导出为 JSON",
        ["settings.export.text"] = "导出为纯文本",
        ["settings.export.done"] = "已导出到 {0}",
        ["settings.logs"] = "打开日志目录",
        ["settings.dataDir"] = "打开数据目录",
        ["settings.about"] = "关于",
        ["settings.about.version"] = "版本",
        ["settings.about.buildDate"] = "构建日期",
        ["settings.about.distribution"] = "产物形态",
        ["settings.about.deviceId"] = "设备 ID",
        ["settings.about.server"] = "后端地址",
        ["settings.about.checkUpdate"] = "检查更新",
        ["settings.about.noUpdateSource"] = "未配置更新源，请手动下载新版本",
        ["settings.about.localManifest"] = "本地清单显示可用版本 {0}",
        ["settings.logout"] = "退出登录",
        ["settings.logout.confirm"] = "确定退出登录吗？",
        ["settings.logout.clearData"] = "同时清除本地数据",

        /* ---- URL 校验 ---- */
        ["settings.url.error.empty"] = "地址不能为空",
        ["settings.url.error.invalid"] = "地址格式不正确",
        ["settings.url.error.scheme"] = "仅支持 https:// 或 wss://",
        ["settings.url.warning.plaintext"] = "凭据将以明文传输，确定继续吗？",

        /* ---- 托盘菜单（FR-W-DSK-1） ---- */
        ["tray.menu.show"] = "显示窗口",
        ["tray.menu.hide"] = "隐藏窗口",
        ["tray.menu.status"] = "连接状态",
        ["tray.menu.profile"] = "个人中心",
        ["tray.menu.reminders"] = "已排程提醒",
        ["tray.menu.reconnect"] = "重新连接",
        ["tray.menu.settings"] = "设置",
        ["tray.menu.exit"] = "退出",

        /* ---- 自检（NFR-W-15） ---- */
        ["selftest.title"] = "TKS Desktop 自检",
        ["selftest.pass"] = "通过",
        ["selftest.fail"] = "失败",
        ["selftest.summary"] = "共 {0} 项，通过 {1}，失败 {2}",

        /* ---- 错误码文案（§5.5 C-6） ---- */
        ["error.unknown"] = "出了点问题，请稍后再试",
        ["error.api.0"] = "成功",
        ["error.api.40001"] = "请求参数有误",
        ["error.api.40002"] = "内容不能为空",
        ["error.api.40101"] = "用户名或密码错误",
        ["error.api.40102"] = "登录状态已失效，请重新登录",
        ["error.api.40301"] = "该设备不在白名单",
        ["error.api.40302"] = "设备数量已达上限",
        ["error.api.40201"] = "积分不足",
        ["error.api.40202"] = "该物品不存在或已下架",
        ["error.api.40204"] = "互动失败，积分已退回",
        ["error.api.40205"] = "补签卡不足",
        ["error.api.40206"] = "该日期已有有效对话记录，无需补签",
        ["error.api.40207"] = "日期不合法",
        ["error.api.50301"] = "服务暂时不可用，请稍后再试",
        ["error.api.5000"] = "服务器开小差了，请稍后再试",
        ["error.server.INVALID_JSON"] = "消息格式有误",
        ["error.server.UNKNOWN_TYPE"] = "不支持的消息类型",
        ["error.server.EMPTY_MESSAGE"] = "消息内容不能为空",
        ["error.server.VISION_IMAGE_COUNT_EXCEEDED"] = "图片数量超出限制",
        ["error.server.VISION_INVALID_MIME"] = "图片格式不受支持",
        ["error.server.VISION_INVALID_BASE64"] = "图片数据损坏",
        ["error.server.VISION_IMAGE_TOO_LARGE"] = "图片体积过大",
        ["error.server.GEMINI_NOT_CONFIGURED"] = "服务端未配置识图能力",
        ["error.server.AI_TIMEOUT"] = "AI 响应超时",
        ["error.server.INTERNAL_ERROR"] = "服务端内部错误",
        ["error.client.SEND_FAILED"] = "发送失败",
        ["error.client.TIMEOUT"] = "请求超时",
        ["error.client.CONNECTION_LOST"] = "连接已断开",

        /* ---- 便携版 / 环境 ---- */
        ["settings.portable.notWritable"] = "程序目录不可写，请移到可写目录或以安装版运行",
        ["settings.tray.unavailable"] = "托盘不可用，已临时改为关闭即退出",
        ["startup.initializing"] = "正在启动…",
        ["startup.failed"] = "客户端启动或界面加载失败。请检查日志目录中的错误记录，并联系维护者。",
    };

    /// <summary>当前语言（首版仅 zh-CN）。</summary>
    public static string CurrentLanguage => "zh-CN";

    /// <summary>
    /// 取文案。未知 key 回退为 key 本身（便于机械校验发现缺失）。
    /// </summary>
    public static string T(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return ZhCn.TryGetValue(key, out var value) ? value : key;
    }

    /// <summary>取带格式化参数的文案。</summary>
    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // 参数与占位符不匹配时不得崩溃，返回模板原文（便于发现缺陷）。
            return template;
        }
    }

    /// <summary>全部已登记的 key（供机械校验 i18n 缺失/未使用 —— V-W-S8）。</summary>
    public static IReadOnlyCollection<string> AllKeys => ZhCn.Keys;

    /// <summary>检查某个 key 是否存在（用于静态校验）。</summary>
    public static bool Has(string key) => ZhCn.ContainsKey(key);
}
