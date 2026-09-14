/**
 * 聊天页（FR-UI-1 / FR-CHAT-* / FR-IMG-* / FR-INT-1）。
 *
 * 结构：顶栏（联系人卡片 + 状态 + 操作）→ 消息列表 → 输入条，
 * 右下角常驻「+」互动入口，搜索浮层与大图查看器覆盖在消息区之上。
 *
 * 事件接入（单一入口是 `store/events.ts` 的 `wireEvents()`，这里只消费它分发的窗口自定义事件，
 * **不重复订阅 IPC**，否则流式增量会被叠加两次）：
 *   - `tks:focus-input` → 聚焦输入框（FR-DSK-2 / FR-DSK-6、Ctrl+L）
 *   - `tks:search`      → 打开搜索浮层（Ctrl+F、FR-CHAT-17）
 *   - `tks:escape`      → 关闭浮层
 *   - `useChatStore.scrollTarget` → 通知点击 / 搜索跳转的定位高亮（FR-NOTI-4）
 *
 * 拖拽添加图片（FR-IMG-7）：Electron 32 起 `File.path` 已移除，preload 也未暴露 `webUtils`，
 * 故只能从 `DataTransfer` 的 `text/uri-list`（文件管理器拖拽时由 Chromium 填充）取绝对路径；
 * 取不到时**降级提示**而不是静默失败。
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import type { ChatAttachment, ChatMessage } from '@shared/protocol'
import { useAppStore } from '../../store/app-store'
import { useChatStore } from '../../store/chat-store'
import { useTranslation } from '../../i18n'
import { IconImage, IconWarning } from '../../components/Icons'
import { refreshDraftImages, useAttachmentErrorToast } from './AttachStrip'
import { ChatTopBar } from './ChatTopBar'
import { Composer } from './Composer'
import { ImageViewer } from './ImageViewer'
import { MessageList } from './MessageList'
import { SearchOverlay } from './SearchOverlay'
import { InteractionMenu } from '../interaction/InteractionMenu'

/** FR-CHAT-14：typing 文案按服务端 `stage` 区分。 */
function typingKeyOf(stage: 'vision' | 'generating' | 'interaction' | 'interaction_merge' | undefined): string {
  switch (stage) {
    case 'vision':
      return 'chat.typing.vision'
    case 'generating':
      return 'chat.typing.generating'
    case 'interaction':
      return 'chat.typing.interaction'
    case 'interaction_merge':
      return 'chat.typing.interaction_merge'
    default:
      return 'chat.typing.generic'
  }
}

/**
 * 从拖拽载荷中提取本地绝对路径（FR-IMG-7）。
 *
 * 文件管理器拖拽会填 `text/uri-list`（`file:///home/x/a.png`），
 * 优先用它；`text/plain` 只作兜底。取不到就返回空数组，由调用方提示降级方案。
 */
function pathsFromDataTransfer(dataTransfer: DataTransfer | null): string[] {
  if (!dataTransfer) return []
  const raw = dataTransfer.getData('text/uri-list') || dataTransfer.getData('text/plain')
  if (!raw) return []
  const paths: string[] = []
  for (const line of raw.split(/\r?\n/)) {
    const value = line.trim()
    if (value.length === 0 || value.startsWith('#')) continue
    if (!value.startsWith('file://')) continue
    try {
      paths.push(decodeURIComponent(value.replace(/^file:\/\//, '')))
    } catch {
      /* 忽略无法解码的行 */
    }
  }
  return paths
}

export function ChatPage(): JSX.Element {
  const { t } = useTranslation()
  const loaded = useChatStore((s) => s.loaded)
  const load = useChatStore((s) => s.load)
  const scrollTarget = useChatStore((s) => s.scrollTarget)
  const setScrollTarget = useChatStore((s) => s.setScrollTarget)
  const locate = useChatStore((s) => s.locate)
  const syncStatus = useChatStore((s) => s.syncStatus)
  const typing = useChatStore((s) => s.typing)
  const queued = useChatStore((s) => s.queued)
  const pushToast = useAppStore((s) => s.pushToast)
  const reportAttachmentError = useAttachmentErrorToast()

  const [draftText, setDraftText] = useState('')
  const [searchOpen, setSearchOpen] = useState(false)
  const [dragActive, setDragActive] = useState(false)
  const [viewer, setViewer] = useState<{ attachments: ChatAttachment[]; index: number } | null>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const [searchParams] = useSearchParams()

  // FR-SYNC-1：本地历史先上屏（App 启动时已触发，此处兜底，保证直接进聊天页也有数据）
  useEffect(() => {
    if (!loaded) void load()
  }, [loaded, load])

  // FR-NOTI-4：`#/chat?messageId=xxx` 也要能定位（主进程直接 setScrollTarget 的补充路径）
  const messageIdParam = searchParams.get('messageId')
  useEffect(() => {
    if (messageIdParam) locate(messageIdParam)
  }, [messageIdParam, locate])

  // 主进程唤起 / 快捷键：聚焦输入框、打开搜索、Esc 收起浮层
  useEffect(() => {
    const focusInput = (): void => textareaRef.current?.focus()
    const openSearch = (): void => setSearchOpen(true)
    const closeOverlays = (): void => {
      setSearchOpen(false)
      setViewer(null)
      setDragActive(false)
    }
    window.addEventListener('tks:focus-input', focusInput)
    window.addEventListener('tks:search', openSearch)
    window.addEventListener('tks:escape', closeOverlays)
    return () => {
      window.removeEventListener('tks:focus-input', focusInput)
      window.removeEventListener('tks:search', openSearch)
      window.removeEventListener('tks:escape', closeOverlays)
    }
  }, [])

  // FR-IMG-7：拖拽图片文件到窗口即添加为附件
  useEffect(() => {
    let depth = 0
    const hasFiles = (event: DragEvent): boolean => Array.from(event.dataTransfer?.types ?? []).includes('Files')

    const onDragEnter = (event: DragEvent): void => {
      if (!hasFiles(event)) return
      depth += 1
      setDragActive(true)
    }
    const onDragOver = (event: DragEvent): void => {
      if (!hasFiles(event)) return
      // 必须 preventDefault，否则浏览器不会派发 drop
      event.preventDefault()
      if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'
    }
    const onDragLeave = (): void => {
      depth = Math.max(0, depth - 1)
      if (depth === 0) setDragActive(false)
    }
    const onDrop = (event: DragEvent): void => {
      if (!hasFiles(event)) return
      event.preventDefault()
      depth = 0
      setDragActive(false)

      const paths = pathsFromDataTransfer(event.dataTransfer)
      if (paths.length === 0) {
        // 取不到绝对路径时优雅降级（Electron 32 已移除 `File.path`）
        pushToast({ level: 'warn', i18nKey: 'chat.drop.unsupported' })
        return
      }
      void (async () => {
        try {
          await window.tks.images.addPaths(paths)
          await refreshDraftImages()
        } catch (err) {
          reportAttachmentError(err)
        }
      })()
    }

    window.addEventListener('dragenter', onDragEnter)
    window.addEventListener('dragover', onDragOver)
    window.addEventListener('dragleave', onDragLeave)
    window.addEventListener('drop', onDrop)
    return () => {
      window.removeEventListener('dragenter', onDragEnter)
      window.removeEventListener('dragover', onDragOver)
      window.removeEventListener('dragleave', onDragLeave)
      window.removeEventListener('drop', onDrop)
    }
  }, [pushToast, reportAttachmentError])

  // FR-CHAT-11：重试用户消息 → 内容与图片回填输入框，由用户确认后重发
  const handleRetry = useCallback(
    (message: ChatMessage): void => {
      setDraftText(message.content)
      const attachments = message.attachments ?? []
      if (attachments.length > 0) {
        void window.tks.images
          .addPaths(attachments.map((attachment) => attachment.localPath))
          .then(() => refreshDraftImages())
          .catch(() => undefined)
      }
      pushToast({ level: 'info', i18nKey: 'chat.message.retryPrefilled' })
      textareaRef.current?.focus()
    },
    [pushToast]
  )

  const openImage = useCallback((attachment: ChatAttachment, siblings: ChatAttachment[]): void => {
    const index = Math.max(
      0,
      siblings.findIndex((item) => item.attachmentId === attachment.attachmentId)
    )
    setViewer({ attachments: siblings, index })
  }, [])

  const handleTargetHandled = useCallback((): void => setScrollTarget(null), [setScrollTarget])

  const firstQueued = queued.length > 0 ? queued[0] : null

  return (
    <div className="chat-page">
      <ChatTopBar onOpenSearch={() => setSearchOpen(true)} />

      <div className="chat-body">
        {/* FR-SYNC-6：同步失败必须在 UI 可见 */}
        {syncStatus && !syncStatus.ok ? (
          <div className="offline-banner" role="status">
            <IconWarning size={14} />
            {t('sync.failed')}
          </div>
        ) : null}

        {/* FR-CHAT-14：typing 状态条（按 stage 出不同文案） */}
        {typing.typing ? (
          <div className="chat-activity" role="status" aria-live="polite" aria-label={t('chat.typing.aria')}>
            <span>{t(typingKeyOf(typing.stage))}</span>
          </div>
        ) : null}

        {/* FR-CHAT-15 / EDGE-L17：排队提示的秒数一律用服务端下发值 */}
        {firstQueued ? (
          <div className="chat-activity" role="status">
            <span>{t('chat.queued', { n: firstQueued.debounceWindowSec })}</span>
          </div>
        ) : null}

        {/* FR-IMG-7：拖拽悬停提示 */}
        {dragActive ? (
          <div className="chat-activity" role="status">
            <IconImage size={14} />
            <span>{t('chat.drop.hint')}</span>
          </div>
        ) : null}

        <MessageList
          highlightId={scrollTarget}
          onTargetHandled={handleTargetHandled}
          onRetry={handleRetry}
          onOpenImage={openImage}
        />

        <Composer
          value={draftText}
          onChange={setDraftText}
          onSent={() => setDraftText('')}
          textareaRef={textareaRef}
        />

        {/* FR-INT-1：右下角常驻「+」；§7.1a：输入框文字作为附言 */}
        <InteractionMenu composerText={draftText} onSent={() => setDraftText('')} />

        <SearchOverlay open={searchOpen} onClose={() => setSearchOpen(false)} />
      </div>

      {viewer ? (
        <ImageViewer
          attachments={viewer.attachments}
          index={viewer.index}
          onIndexChange={(index) => setViewer((current) => (current ? { ...current, index } : current))}
          onClose={() => setViewer(null)}
        />
      ) : null}
    </div>
  )
}

export default ChatPage
