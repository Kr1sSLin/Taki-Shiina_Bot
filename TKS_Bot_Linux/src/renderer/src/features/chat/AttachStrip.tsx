/**
 * 草稿附件缩略图条（FR-IMG-2 / FR-IMG-3）。
 *
 * - 数据源是 `useAppStore.draftImages`（由主进程 `evt:draftImagesChanged` 驱动，
 *   渲染端不自己维护附件列表，避免与 `chat_attachments` 表不一致）
 * - 每张可单独移除（FR-IMG-3），移除后以 `images.listDraft()` 结果回填，保证幂等
 * - 张数 / 大小提示与「被拒绝」的统一反馈（FR-IMG-2）：主进程在超限 / 格式不符 /
 *   磁盘不足时会抛出带 `i18nKey` 的错误，这里转成 toast，**绝不发往服务端**
 */

import { useCallback, useState } from 'react'
import { PROTOCOL } from '@shared/levels'
import { useAppStore } from '../../store/app-store'
import { describeError, useTranslation } from '../../i18n'
import { Spinner } from '../../components/primitives'
import { IconClose } from '../../components/Icons'
import { formatBytes } from '../../lib/format'
import { attachmentSrc } from './ImageViewer'

/** 剪贴板里没有图片是 `Ctrl+V` 粘贴文本时的常态，不该打扰用户。 */
const SILENT_KEYS: string[] = ['image.error.clipboardEmpty']

/** 取错误对象上的 i18n key（preload 还原 `IpcResult` 时写入）。 */
function i18nKeyOf(err: unknown): string | null {
  if (typeof err !== 'object' || err === null) return null
  const value = (err as { i18nKey?: unknown }).i18nKey
  return typeof value === 'string' && value.length > 0 ? value : null
}

/**
 * 附件添加失败的统一反馈（FR-IMG-2）。
 *
 * @param options.silent 为 `true` 时忽略「剪贴板无图片」这类常态错误
 */
export function useAttachmentErrorToast(): (err: unknown, options?: { silent?: boolean }) => void {
  const pushToast = useAppStore((s) => s.pushToast)
  return useCallback(
    (err: unknown, options?: { silent?: boolean }): void => {
      const key = i18nKeyOf(err)
      if (options?.silent === true && (key === null || SILENT_KEYS.includes(key))) return
      pushToast({
        level: 'warn',
        i18nKey: key ?? 'image.error.unknown',
        text: describeError(err)
      })
    },
    [pushToast]
  )
}

/** 以主进程为准回填草稿列表（幂等：重复调用不会重复附件）。 */
export async function refreshDraftImages(): Promise<void> {
  const list = await window.tks.images.listDraft()
  useAppStore.getState().setDraftImages(list)
}

export interface AttachStripProps {
  /** 发送中 / 不可编辑时禁用移除按钮。 */
  disabled?: boolean
}

export function AttachStrip({ disabled = false }: AttachStripProps): JSX.Element | null {
  const { t } = useTranslation()
  const images = useAppStore((s) => s.draftImages)
  const reportError = useAttachmentErrorToast()
  const [removingId, setRemovingId] = useState<string | null>(null)

  const remove = useCallback(
    async (attachmentId: string): Promise<void> => {
      setRemovingId(attachmentId)
      try {
        await window.tks.images.remove(attachmentId)
        await refreshDraftImages()
      } catch (err) {
        reportError(err)
      } finally {
        setRemovingId(null)
      }
    },
    [reportError]
  )

  if (images.length === 0) return null

  const countLabel = t('chat.image.count', { n: images.length, max: PROTOCOL.MAX_IMAGE_COUNT })

  return (
    <div className="attach-strip" role="group" aria-label={countLabel}>
      {images.map((image) => (
        <div className="attach-item" key={image.attachmentId}>
          <img src={attachmentSrc(image.localPath)} alt={t('chat.image.preview')} draggable={false} />
          <button
            type="button"
            className="attach-item-remove"
            aria-label={t('chat.image.remove')}
            title={t('chat.image.remove')}
            disabled={disabled || removingId === image.attachmentId}
            onClick={() => void remove(image.attachmentId)}
          >
            {removingId === image.attachmentId ? <Spinner size={11} /> : <IconClose size={12} />}
          </button>
        </div>
      ))}
      <span className="attach-hint">
        {countLabel} · {formatBytes(PROTOCOL.MAX_IMAGE_BYTES)} · {t('chat.image.limitHint')}
      </span>
    </div>
  )
}

export default AttachStrip
