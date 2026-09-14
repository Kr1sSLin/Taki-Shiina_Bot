# 渲染进程共享层契约（供功能页开发者/子代理遵循）

> 本文件是 `src/renderer/src/` 下各 feature 页面**必须**遵循的接口约定。
> 共享层（stores / components / i18n / lib）由主开发者维护，功能页只消费、不修改。

---

## 0. 铁律

1. **不得硬编码中文字符串**（NFR-10）。所有面向用户的文案一律 `t('key')`，
   新增 key 必须同时写入 `src/shared/i18n/zh-CN.ts`。
2. **不得直接使用 `fs` / `net` / `child_process` / `ipcRenderer`**（FR-ARCH-2）。
   一切能力经 `window.tks.*`（见 `src/shared/ipc.ts` 的 `TksApi`）。
3. **不得在渲染端实现积分/等级业务规则**（FR-PROG-2/3）。
   所有「自然日」展示必须用服务端返回的日期字符串（`lastValidDate` / `business_date` / `candidates[].date`）。
4. **错误一律用 `describeError(err)` 转文案**，不要直接 `err.message`。
5. 只写函数组件 + hooks；不引入新的第三方依赖（体积约束 NFR-3）。

---

## 1. i18n

```ts
import { useTranslation, describeError, errorCodeOf } from '../i18n'

const { t } = useTranslation()
t('chat.send')                          // → '发送'
t('chat.queued', { n: 8 })              // → '已排队（8 秒内合并发送）'
describeError(err)                      // 任意异常 → 本地化文案
errorCodeOf(err)                        // → number | null（用于 40201 / 40204 分支）
```

---

## 2. Stores（zustand）

### `useAppStore` — `../store/app-store`
```ts
ready, settings, about, platform, shortcut,
connection: ConnectionState, systemDark, theme: 'light'|'dark', toasts,
celebration, reminders, draftImages, unreadNotifications,
restFallback, restFallbackEnabled, syncStatus, focused

setSettings(s), updateSettings(patch): Promise<AppSettings>,
setConnection(c), setSystemDark(b), pushToast({level,i18nKey,params?,text?}),
dismissToast(id), setCelebration(e|null), setReminders(r), setDraftImages(imgs),
setUnread(n), setRestFallback(enabled, degraded), setSyncStatus(s), setFocused(b),
refreshShortcut(): Promise<void>
```

### `useAuthStore` — `../store/auth-store`
```ts
checked, authenticated, userId, deviceId,
storageMode: 'safeStorage'|'plaintext-0600'|'none',
lastUsername, storagePromptDismissed,
expiryNotice: { reason: string; kicked: boolean } | null
bootstrap(), login(user, pass): Promise<AuthTokens>, logout(clearLocalData): Promise<void>,
setStoragePromptDismissed(b), setExpiryNotice(n|null)
```

### `useChatStore` — `../store/chat-store`
```ts
loaded, messages: ChatMessage[], streaming: Record<requestId, string>,
typing: { typing: boolean; stage?: 'vision'|'generating'|'interaction'|'interaction_merge' },
queued: { requestId: string; debounceWindowSec: number }[],
syncStatus: SyncStatusEvent|null, hasMore, loadingOlder, oldestTimestamp,
searchKeyword, searchResults: ChatMessage[]|null, scrollTarget: string|null

load(), loadOlder(), upsert(msgs), remove(ids), appendDelta(id, delta), clearStreaming(id),
setTyping(t), pushQueued(q), clearQueued(), setSyncStatus(s), handleStreamDone(e),
reset(), search(kw), clearSearch(), locate(messageId), setScrollTarget(id|null)
```

### `useProfileStore` — `../store/profile-store`
```ts
loaded, offline, overview: PointsOverviewData|null, cached: UserProgressCache|null,
levelConfig: LevelConfigData|null, levels: ResolvedLevel[],
ledger: PointsLedgerEntry[], ledgerPage, ledgerHasMore, ledgerTotal,
ledgerReasonFilter: string|null, ledgerLoading,
makeupCard: MakeupCardSummary|null, candidates: MakeupCandidatesData|null,
interaction: InteractionItemsData|null, interactionLoading,
interactionPending: { itemId: string; requestId: string; startedAt: number }|null,
balance: number

refreshAll(), refreshOverview(), refreshLevelConfig(),
loadLedger({ reset?, reasonCode? }), refreshBalance(),
refreshInteraction(), refreshMakeup(),
applyPointsChanged(balance, entries), applyBalance(n), applyMakeupAvailable(n),
setInteractionPending(p), reset()
```

---

## 3. 组件（`../components/...`）

### `primitives.tsx`
```ts
<Button variant="primary|secondary|ghost|danger" size="sm|md|lg" loading icon block>
<IconButton label="无障碍名称" onClick active>
<Spinner size>
<Input label hint error ...inputProps>
<TextArea label hint ...textareaProps>
<Switch checked onChange label hint disabled>
<Segmented value options onChange ariaLabel>          // options: {value,label}[]
<Modal open title onClose footer role width>           // 内置 Esc 关闭 + 焦点管理
<EmptyState icon title hint>
<Card title action>…</Card>
<Badge tone="neutral|ok|warn|error|accent">
```

### `Icons.tsx`
`IconProps = SVGProps<SVGSVGElement> & { size?: number }`
可用：`IconSend IconPlus IconClose IconImage IconSettings IconChat IconHistory IconUser IconSearch
IconRefresh IconBell IconTrash IconCopy IconEmoji IconChevronLeft IconChevronRight IconGift
IconCalendar IconAlarm IconDownload IconFolder IconLink IconWarning IconCheck IconInfo IconClock
IconLogout IconUpload IconExpand IconSparkle IconTray`

### `visual.tsx`
```ts
<DoodleBackground seed height className />      // FR-UI-2 手绘涂鸦（8 种图形）
<LiquidGlass strong className as>…</LiquidGlass> // FR-UI-3 液态玻璃
<ThemeReveal active origin={{x,y}} color />
useReducedMotion(): boolean                     // NFR-11
```

### `LevelBadge.tsx`
```ts
<LevelBadge level={ResolvedLevel} size={72} showEmoji />
<LevelProgressBar ratio={0..1} accent="#..." animated label />
<LevelChip level={ResolvedLevel} name? />
```

### `feedback.tsx`
```ts
<Toaster />      // 已挂在 App 根部
<Celebration />  // 已挂在 App 根部
```

---

## 4. 工具（`../lib/...`）

### `format.ts`
```ts
formatTime(ms)            // 'HH:MM'
formatDateTime(ms)        // 'YYYY-MM-DD HH:MM'
formatMonthDay(ms)        // 'MM-DD'
sameMinute(a,b)           // FR-CHAT-13 合并展示时间
isSameLocalDay(a,b)
formatBytes(n)
formatServerDate('2026-09-11')  // '9月11日'（不重算日期，FR-PROG-3）
weekdayShort(ms)
truncate(text, max)
```

### `level.ts`
```ts
normalizeLevels(config): LevelConfigEntry[]        // 已按 sort_order 排序并过滤空 level_code
resolveLevel(code, name, levels): ResolvedLevel    // {code,name,emoji,gradient,accent,animated,isDefault,thresholdDays}
resolveLevelFromStatus(status, levels): ResolvedLevel
buildLevelGuide(levels): Array<ResolvedLevel & {order:number}>
levelProgress(status, levels): { currentDays, targetDays, daysLeft, ratio, nextName, isMax }
```

---

## 5. 路由与导航

`App.tsx` 用 `react-router-dom` 的 `HashRouter`，路由表：

| 路径 | 页面 |
|---|---|
| `/login` | `features/auth/LoginPage` |
| `/chat` | `features/chat/ChatPage` |
| `/history` | `features/history/HistoryPage` |
| `/profile` | `features/profile/ProfilePage` |
| `/profile/ledger` | `features/profile/PointsHistoryPage` |
| `/profile/makeup` | `features/profile/MakeupCalendarPage` |
| `/profile/levels` | `features/profile/LevelGuidePage` |
| `/settings` | `features/settings/SettingsPage` |
| `/settings/reminders` | `features/settings/RemindersPage` |

主进程可通过 `evt:navigate` 传入 `{ to, params }`（通知点击）；
`params.messageId` 存在时聊天页需滚动定位并高亮该消息。
`evt:focus-input` 时聊天页需聚焦输入框。

---

## 6. 关键 FR 落点（务必实现，reviewer 会逐条对照）

| FR | 要点 |
|---|---|
| FR-CHAT-1 | 多行输入；发送键按 `settings.sendKey`（`enter` / `ctrl+enter`） |
| FR-CHAT-3 | 乐观上屏（主进程已入库，渲染端只镜像） |
| FR-CHAT-4 | 未连接时禁用发送并提示 `chat.notConnected` |
| FR-CHAT-5/6 | 流式占位用 `streaming[requestId]` 渲染；最终多气泡由 `messages` 提供 |
| FR-CHAT-11 | 右键菜单：复制 / 删除（仅本地）/ 重试（仅用户消息，回填输入框） |
| FR-CHAT-13 | 同一分钟连续消息合并展示时间 |
| FR-CHAT-14 | typing 按 `stage` 出不同文案（vision/generating/interaction/interaction_merge） |
| FR-CHAT-15 | `queued` 展示「已排队（{n}s 内合并）」，n 用服务端下发值，**不得硬编码 8/20**（EDGE-L17） |
| FR-CHAT-16 | 首屏 50~80 条，向上滚动分页 |
| FR-CHAT-17 | 搜索并跳转定位 + 关键词高亮 |
| FR-CHAT-9 | `restFallbackEnabled` 时提供「单次请求模式」入口并明示无流式/不参与积分 |
| FR-IMG-1..10 | 3 张 / 20MB / JPG-PNG；缩略图条可移除；拖拽；`Ctrl+V`；点击看大图；另存为 |
| FR-NOTI-2 | 窗口前台聚焦时不弹（主进程已处理，渲染端只需展示） |
| FR-SYNC-6 | 同步失败要在 UI 可见（用 `syncStatus.ok === false`） |
| FR-HIS-1 | 通知列表 + 已读/全部已读 + 未读数 |
| FR-HIS-2/4 | 记忆列表按时间倒序、**只读**（不得提供删除/编辑） |
| FR-SET-1..10 | 见 §7 |
| FR-INT-1..13 | 见 §7 |
| FR-LV-1..9 / FR-MC-1..8 / FR-PT-1..7 | 见 §7 |

---

## 7. 功能页各自的硬性要求

### 聊天页（`features/chat/`）
- 底部右下角常驻「+」悬浮按钮 → 互动菜单（FR-INT-1）
- 互动菜单为**简单平铺列表**，按服务端 `sortOrder` 顺序；展示图标 / 名称 / 所需积分（FR-INT-2/3）
- 置灰**必须**用服务端 `affordable` 字段，不得用本地余额自算（FR-INT-4）；悬浮提示「还差 N 积分」
- 点击物品：本地余额预校验 → 调 `interactionSend(itemId, requestId, text)`，
  `requestId` 用 `crypto.randomUUID()` 且**重试必须复用同一 id**（FR-INT-5/11）
- 输入框有文字时把文字作为 `text` 附言提交；成功后清空输入框，失败保留文字（§7.1a）
- 等待期间展示 typing 态；**不做 3 秒硬性报错**（FR-INT-7）
- 超时（客户端等待 ≥60s）提示「立希好像走神了」并刷新余额（FR-INT-8）
- 失败码 40201 → 用返回 `balance` 覆盖本地缓存；40204 → 展示 `fallbackText` + 提示积分已退回
- 互动回复复用**同一套气泡组件**，用 `interactionItemIcon` / `interactionItemName` 加轻量礼物标记（FR-INT-6）

### Profile 页（`features/profile/`）
- 首屏用 `refreshAll()`（内部即 `/points/overview` 一次取回）
- 等级为默认态时用 `resolveLevel` 的中性兜底文案（FR-LV-3），不得渲染空标题
- 升级进度条用 `LevelProgressBar`（FR-LV-1）
- 断签导致等级清零时明确提示积分余额未受影响（FR-LV-8）
- 展示 `breakDeadlineDate` 与 `gapDays`（FR-LV-9）
- 补签日历数据源**只能**是 `candidates`（FR-MC-2），**不得**用本地日期推算缺口
- 补签必须**用户主动点击** + 二次确认「将消耗 1 张补签卡补签 {date}」（FR-MC-3）
- 补签结果按 `level.changeType` 决定反馈强度（FR-MC-5）——`RESTORE` 克制，`UPGRADE` 才庆祝
- 错误码 40205/40206/40207 给出明确文案并刷新日历（FR-MC-6）
- 离线时展示缓存并标注「离线数据，最后更新于 {时间}」（FR-PROG-4）
- 流水事由按 `reason_code` 本地化（FR-PT-3），金额 `+N`/`-N` 带颜色（FR-PT-7）

### 设置页（`features/settings/`）
- 主题三选一（FR-SET-1），切换时用 `ThemeReveal` 播放揭示动画（FR-UI-4）
- 天气城市：读取/保存，**截断 `#` 之后内容**（FR-SET-2）
- 服务地址：可改 + 连通性测试按钮（打 `/healthz`）；`http://` 需二次确认并明示「凭据将以明文传输」，
  仅 `localhost`/`127.0.0.1` 免确认（FR-SET-3 / FR-CFG-2）
- 清空本地会话：二次确认，**与聊天页共用** `clearConversation`（FR-SET-4 / FR-CHAT-12）
- 开机自启开关（FR-SET-5）
- 关闭窗口行为（FR-SET-6）
- 全局快捷键：可录制，冲突给出明确提示；Wayland 下置灰并提示改用托盘（FR-SET-7 / EDGE-L8）
- 发送键二选一（FR-SET-8）
- 关于：版本号用 `about.version`（**不得硬编码**）、后端地址、deviceId、打开日志目录（FR-SET-9）
- 导出 JSON / 纯文本（FR-SET-10）
- 五类通知开关 + 勿扰时段（FR-NOTI-7）
- 已排程提醒列表 + 取消（FR-REM-6）

### 历史页（`features/history/`）
- 两个 Tab：Bot 通知（FR-HIS-1）、记忆档案（FR-HIS-2）
- 记忆**只读**，必须显示提示文案 `history.facts.readonly`（FR-HIS-4）
