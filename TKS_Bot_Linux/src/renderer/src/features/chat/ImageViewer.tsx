/**
 * 大图查看器（FR-IMG-9）。
 *
 * - 图片一律经主进程注册的自定义协议 `tks-attachment://` 读取
 *   （NFR-5：渲染端不直接触碰文件系统，也不能用 `file://`，CSP 只放行 `tks-attachment:`）
 * - 缩放 / 重置缩放 / 另存为 / 用系统程序打开
 * - 多张附件时可左右切换（同一条消息的多个附件）
 *
 * NFR-11：缩放过渡由 CSS（`.image-viewer-stage img`）负责，样式层已带
 * `prefers-reduced-motion` 守卫；本文件不做任何 JS 驱动的动画。
 */

import { useEffect, useState } from 'react'
import type { ChatAttachment } from '@shared/protocol'
import { useAppStore } from '../../store/app-store'
import { describeError, useTranslation } from '../../i18n'
import { Button, IconButton, Spinner } from '../../components/primitives'
import {
  IconChevronLeft,
  IconChevronRight,
  IconClose,
  IconDownload,
  IconLink,
  IconWarning
} from '../../components/Icons'

const ZOOM_MIN = 0.25
const ZOOM_MAX = 4
const ZOOM_STEP = 0.25

/**
 * 本地附件 → 自定义协议 URL。
 *
 * `main/desktop/protocol.ts` 约定的形状为 `tks-attachment://local<URI 编码后的绝对路径>`。
 */
export function attachmentSrc(localPath: string): string {
  return `tks-attachment://local${encodeURI(localPath)}`
}

export interface ImageViewerProps {
  /** 同一条消息的全部附件（用于左右切换）。 */
  attachments: ChatAttachment[]
  index: number
  onIndexChange: (index: number) => void
  onClose: () => void
}

export function ImageViewer({ attachments, index, onIndexChange, onClose }: ImageViewerProps): JSX.Element | null {
  const { t } = useTranslation()
  const pushToast = useAppStore((s) => s.pushToast)
  const [zoom, setZoom] = useState(1)
  const [saving, setSaving] = useState(false)
  const [broken, setBroken] = useState(false)

  const total = attachments.length
  const current: ChatAttachment | undefined = attachments[index] ?? attachments[0]

  // 切换图片时重置缩放与加载失败标记
  useEffect(() => {
    setZoom(1)
    setBroken(false)
  }, [current?.attachmentId])

  // 键盘：Esc 关闭、方向键切换、+/-/0 缩放（NFR-11 全键盘可达）
  useEffect(() => {
    if (!current) return
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.preventDefault()
        onClose()
        return
      }
      if (event.key === 'ArrowLeft' && total > 1) {
        event.preventDefault()
        onIndexChange((index - 1 + total) % total)
        return
      }
      if (event.key === 'ArrowRight' && total > 1) {
        event.preventDefault()
        onIndexChange((index + 1) % total)
        return
      }
      if (event.key === '+' || event.key === '=') {
        event.preventDefault()
        setZoom((value) => Math.min(ZOOM_MAX, value + ZOOM_STEP))
        return
      }
      if (event.key === '-' || event.key === '_') {
        event.preventDefault()
        setZoom((value) => Math.max(ZOOM_MIN, value - ZOOM_STEP))
        return
      }
      if (event.key === '0') {
        event.preventDefault()
        setZoom(1)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [current, index, total, onClose, onIndexChange])

  if (!current) return null

  const handleSaveAs = async (): Promise<void> => {
    setSaving(true)
    try {
      const result = await window.tks.images.saveAs(current.localPath)
      if (result.saved) {
        pushToast({
          level: 'success',
          i18nKey: 'chat.image.saveAs.done',
          params: { path: result.path ?? '' }
        })
      } else {
        pushToast({ level: 'info', i18nKey: 'chat.image.saveAs.cancelled' })
      }
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    } finally {
      setSaving(false)
    }
  }

  const handleOpenExternal = async (): Promise<void> => {
    try {
      await window.tks.images.openExternal(current.localPath)
    } catch (err) {
      pushToast({ level: 'error', i18nKey: 'error.unknown', text: describeError(err) })
    }
  }

  return (
    <div className="image-viewer" role="dialog" aria-modal="true" aria-label={t('chat.image.viewLarge')}>
      <div className="image-viewer-toolbar">
        <div className="composer-actions">
          <Button size="sm" variant="ghost" onClick={() => setZoom((v) => Math.min(ZOOM_MAX, v + ZOOM_STEP))}>
            {t('chat.image.zoomIn')}
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setZoom((v) => Math.max(ZOOM_MIN, v - ZOOM_STEP))}>
            {t('chat.image.zoomOut')}
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setZoom(1)}>
            {t('chat.image.zoomReset')}
          </Button>
          <span className="muted" aria-live="polite">
            {Math.round(zoom * 100)}%
            {total > 1 ? ` · ${t('chat.image.counter', { n: index + 1, total })}` : ''}
          </span>
        </div>
        <div className="composer-actions">
          {total > 1 ? (
            <>
              <IconButton label={t('chat.image.prev')} onClick={() => onIndexChange((index - 1 + total) % total)}>
                <IconChevronLeft size={18} />
              </IconButton>
              <IconButton label={t('chat.image.next')} onClick={() => onIndexChange((index + 1) % total)}>
                <IconChevronRight size={18} />
              </IconButton>
            </>
          ) : null}
          <IconButton label={t('chat.image.saveAs')} onClick={() => void handleSaveAs()} disabled={saving}>
            {saving ? <Spinner size={16} /> : <IconDownload size={18} />}
          </IconButton>
          <IconButton label={t('chat.image.openExternal')} onClick={() => void handleOpenExternal()}>
            <IconLink size={18} />
          </IconButton>
          <IconButton label={t('common.close')} onClick={onClose}>
            <IconClose size={18} />
          </IconButton>
        </div>
      </div>
      <div className="image-viewer-stage" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
        {broken ? (
          <p className="muted" role="alert">
            <IconWarning size={16} /> {t('chat.image.loadFailed')}
          </p>
        ) : (
          <img
            src={attachmentSrc(current.localPath)}
            alt={t('chat.image.viewLarge')}
            draggable={false}
            style={{ transform: `scale(${zoom})` }}
            onError={() => setBroken(true)}
          />
        )}
      </div>
    </div>
  )
}

export default ImageViewer
