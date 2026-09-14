/**
 * 输入条（FR-CHAT-1 / FR-CHAT-4 / FR-CHAT-9 / FR-IMG-1..3 / FR-IMG-8 / FR-UI-5）。
 *
 * - 多行输入；Enter/Shift+Enter 与 Ctrl+Enter 由 `settings.sendKey` 决定（FR-CHAT-1 / FR-SET-8）
 * - 未连接时禁用发送并给出原因（FR-CHAT-4；不做离线队列，见 EDGE-L6）
 * - 降级态下提供「单次请求模式」入口并明示无流式、不参与积分（FR-CHAT-9）
 * - 图片：文件对话框（FR-IMG-1）、`Ctrl+V` 剪贴板（FR-IMG-8）、缩略图条移除（FR-IMG-3）
 * - Emoji 面板在文档流内展开，把内容推上去而不是浮层遮盖（FR-UI-5）
 *
 * ⚠️ 输入框的文本状态由父级（ChatPage）持有：互动菜单需要读取同一份文字作为附言
 * （§7.1a），成功后由父级清空。
 */

import { useCallback, useEffect, useState, type KeyboardEvent as ReactKeyboardEvent, type RefObject } from 'react'
import { PROTOCOL } from '@shared/levels'
import { useAppStore } from '../../store/app-store'
import { describeError, useTranslation } from '../../i18n'
import { Button, IconButton, Segmented } from '../../components/primitives'
import { IconCopy, IconEmoji, IconImage, IconSend } from '../../components/Icons'
import { AttachStrip, refreshDraftImages, useAttachmentErrorToast } from './AttachStrip'
import { EmojiPanel } from './EmojiPanel'

const TEXTAREA_MAX_HEIGHT = 168

export interface ComposerProps {
  value: string
  onChange: (value: string) => void
  /** 发送成功后的回调（清空输入框）。 */
  onSent: () => void
  /** 由父级持有的输入框引用（主进程唤起时需聚焦、Emoji 插入需读写光标位置）。 */
  textareaRef: RefObject<HTMLTextAreaElement>
}

export function Composer({ value, onChange, onSent, textareaRef }: ComposerProps): JSX.Element {
  const { t } = useTranslation()
  const images = useAppStore((s) => s.draftImages)
  const sendKey = useAppStore((s) => s.settings?.sendKey ?? 'enter')
  const restFallbackEnabled = useAppStore((s) => s.restFallbackEnabled)
  const connectionStatus = useAppStore((s) => s.connection.status)
  const pushToast = useAppStore((s) => s.pushToast)
  const reportAttachmentError = useAttachmentErrorToast()

  const [sending, setSending] = useState(false)
  const [emojiOpen, setEmojiOpen] = useState(false)
  /** FR-CHAT-9：仅在 `restFallbackEnabled` 时生效的单次请求模式。 */
  const [singleShot, setSingleShot] = useState(false)

  const restMode = restFallbackEnabled && singleShot
  const wsReady = connectionStatus === 'connected'
  const canSend = restMode || wsReady
  const imagesBlockRest = restMode && images.length > 0
  const hasContent = value.trim().length > 0 || (images.length > 0 && !restMode)
  const sendDisabled = sending || !canSend || !hasContent || imagesBlockRest

  // 输入框随内容增高（纯布局调整，无动画）
  useEffect(() => {
    const el = textareaRef.current
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(Math.max(el.scrollHeight, 40), TEXTAREA_MAX_HEIGHT)}px`
  }, [value, textareaRef])

  // NFR-11：Esc 收起 Emoji 面板
  useEffect(() => {
    if (!emojiOpen) return
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') setEmojiOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [emojiOpen])

  const handleSend = useCallback(async (): Promise<void> => {
    const content = value.trim()
    const attachmentIds = images.map((image) => image.attachmentId)
    if (!content && attachmentIds.length === 0) return
    if (!canSend) {
      // FR-CHAT-4：未连接时禁止发送并提示（不做离线队列，见 EDGE-L6）
      pushToast({ level: 'warn', i18nKey: 'chat.notConnected' })
      return
    }
    if (restMode && attachmentIds.length > 0) {
      pushToast({ level: 'warn', i18nKey: 'chat.restFallback.imageUnsupported' })
      return
    }

    setSending(true)
    try {
      // FR-CHAT-2：requestId 同时作为本地消息主键、WS 请求 ID 与幂等键
      const requestId = crypto.randomUUID()
      if (restMode) {
        await window.tks.chat.sendViaRest({ requestId, content })
        pushToast({ level: 'info', i18nKey: 'chat.restFallback.done' })
      } else {
        await window.tks.chat.send({ requestId, content, attachmentIds })
      }
      onSent()
      setEmojiOpen(false)
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'chat.error.prefix', text: describeError(err) })
    } finally {
      setSending(false)
    }
  }, [value, images, canSend, restMode, pushToast, onSent])

  const pickImages = async (): Promise<void> => {
    if (images.length >= PROTOCOL.MAX_IMAGE_COUNT) {
      // FR-IMG-1/2：客户端先拦截，不发往服务端
      pushToast({ level: 'warn', i18nKey: 'image.error.countLimit' })
      return
    }
    try {
      await window.tks.images.pick()
      await refreshDraftImages()
    } catch (err) {
      reportAttachmentError(err)
    }
  }

  /** FR-IMG-8：剪贴板图片。`silent` 用于 Ctrl+V 常态（剪贴板里没有图片时不该打扰）。 */
  const pasteImage = async (silent: boolean): Promise<void> => {
    if (images.length >= PROTOCOL.MAX_IMAGE_COUNT) {
      pushToast({ level: 'warn', i18nKey: 'image.error.countLimit' })
      return
    }
    try {
      await window.tks.images.readClipboard()
      await refreshDraftImages()
    } catch (err) {
      reportAttachmentError(err, { silent })
    }
  }

  const insertEmoji = (emoji: string): void => {
    const el = textareaRef.current
    if (!el) {
      onChange(value + emoji)
      return
    }
    const start = el.selectionStart ?? value.length
    const end = el.selectionEnd ?? start
    onChange(`${value.slice(0, start)}${emoji}${value.slice(end)}`)
    requestAnimationFrame(() => {
      el.focus()
      const caret = start + emoji.length
      el.setSelectionRange(caret, caret)
    })
  }

  const handleKeyDown = (event: ReactKeyboardEvent<HTMLTextAreaElement>): void => {
    // 输入法组字期间不拦截（中文输入回车选词）
    if (event.nativeEvent.isComposing) return

    const withModifier = event.ctrlKey || event.metaKey

    // FR-IMG-8：Ctrl+V 优先尝试读剪贴板图片；不 preventDefault，文本粘贴照常
    if (withModifier && (event.key === 'v' || event.key === 'V')) {
      void pasteImage(true)
      return
    }

    if (event.key !== 'Enter') return
    // FR-CHAT-1 / FR-SET-8：发送键由设置决定
    const shouldSend = sendKey === 'ctrl+enter' ? withModifier : !event.shiftKey && !withModifier
    if (!shouldSend) return
    event.preventDefault()
    void handleSend()
  }

  const hint = !canSend
    ? t('chat.notConnected')
    : imagesBlockRest
      ? t('chat.restFallback.imageUnsupported')
      : t(sendKey === 'ctrl+enter' ? 'settings.sendKey.ctrlEnter' : 'settings.sendKey.enter')

  return (
    <div className="composer">
      {restFallbackEnabled ? (
        <div className="band">
          <span className="band-title">{t('conn.restFallback.on')}</span>
          <Segmented
            value={restMode ? 'rest' : 'stream'}
            options={[
              { value: 'stream', label: t('chat.mode.stream') },
              { value: 'rest', label: t('chat.mode.rest') }
            ]}
            onChange={(next) => setSingleShot(next === 'rest')}
            ariaLabel={t('chat.mode.label')}
          />
          <span className="composer-hint">{t('conn.restFallback.hint')}</span>
        </div>
      ) : null}

      <AttachStrip disabled={sending} />

      <div className="composer-row">
        <textarea
          ref={textareaRef}
          className="composer-textarea"
          value={value}
          rows={1}
          placeholder={t('chat.placeholder')}
          aria-label={t('chat.placeholder')}
          onChange={(event) => onChange(event.target.value)}
          onKeyDown={handleKeyDown}
        />

        <div className="composer-actions">
          <IconButton label={t('chat.image.attach')} disabled={sending} onClick={() => void pickImages()}>
            <IconImage size={18} />
          </IconButton>
          <IconButton label={t('chat.image.paste')} disabled={sending} onClick={() => void pasteImage(false)}>
            <IconCopy size={18} />
          </IconButton>
          <IconButton
            label={t('chat.emoji.toggle')}
            active={emojiOpen}
            aria-expanded={emojiOpen}
            onClick={() => setEmojiOpen((open) => !open)}
          >
            <IconEmoji size={18} />
          </IconButton>
        </div>

        <Button
          variant="primary"
          icon={<IconSend size={16} />}
          loading={sending}
          disabled={sendDisabled}
          title={!canSend ? t('chat.notConnected') : t('chat.send')}
          aria-label={t('chat.send')}
          onClick={() => void handleSend()}
        >
          {t('chat.send')}
        </Button>
      </div>

      <span className="composer-hint">{hint}</span>

      {emojiOpen ? <EmojiPanel onPick={insertEmoji} /> : null}
    </div>
  )
}

export default Composer
