/**
 * 图片服务（§6.4 FR-IMG-1..10、§8 FR-DSK-5、EDGE-L15、EDGE-L16）。
 *
 * - 本地与**服务端一致**的校验：≤3 张、单张 ≤20MB、仅 `image/jpeg` / `image/png`（FR-IMG-2）
 * - 选中即复制进应用私有目录（`~/.local/share/tks-desktop/attachments/`），
 *   避免用户移动/删除原文件导致历史消息图片失效（FR-IMG-5）
 * - 剪贴板贴图（FR-IMG-8 / FR-DSK-5）：`clipboard.readImage()` → PNG 编码
 * - 拖拽入窗（FR-IMG-7）
 * - 写入前检查可用磁盘空间（EDGE-L15）
 */

import { randomUUID } from 'node:crypto'
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  statSync,
  statfsSync,
  unlinkSync,
  writeFileSync
} from 'node:fs'
import { extname, join } from 'node:path'
import { BrowserWindow, clipboard, dialog, nativeImage, shell } from 'electron'
import type { ChatAttachment } from '@shared/protocol'
import { PROTOCOL } from '@shared/levels'
import { IPC, type AttachmentUsage } from '@shared/ipc'
import { bus } from '../../app/bus'
import { t } from '../../app/i18n'
import { createLogger } from '../../app/logger'
import { ensureDirs, paths } from '../../app/paths'
import {
  attachmentUsage as repoUsage,
  deleteAttachment,
  getAttachment,
  insertAttachment,
  listAllAttachmentPaths,
  listAttachmentsOlderThan,
  listDraftAttachments
} from '../database/repositories/message-repository'

const log = createLogger('images')

/** 磁盘余量下限：单张 20MB 图片需要留出足够余量（EDGE-L15）。 */
const MIN_FREE_BYTES = 64 * 1024 * 1024

function allowedMime(mime: string): boolean {
  return PROTOCOL.ALLOWED_IMAGE_MIME.includes(mime)
}

function mimeFromPath(filePath: string): string | null {
  const ext = extname(filePath).toLowerCase()
  if (ext === '.jpg' || ext === '.jpeg') return 'image/jpeg'
  if (ext === '.png') return 'image/png'
  return null
}

function ensureAttachmentsDir(): string {
  const dir = paths().attachmentsDir
  if (!existsSync(dir)) mkdirSync(dir, { recursive: true, mode: 0o700 })
  return dir
}

/** EDGE-L15：写入前检查可用空间。 */
function hasFreeSpace(neededBytes: number): boolean {
  try {
    const stats = statfsSync(paths().dataDir, { bigint: false })
    const free = Number(stats.bavail) * Number(stats.bsize)
    return free - neededBytes >= MIN_FREE_BYTES
  } catch {
    // 无法探测时不阻塞用户
    return true
  }
}

export interface AddImageResult {
  added: ChatAttachment[]
  /** 被拒绝的文件与原因 i18n key。 */
  rejected: Array<{ name: string; i18nKey: string }>
}

/** 目前草稿张数（FR-IMG-1：单次最多 3 张）。 */
function currentDraftCount(): number {
  return listDraftAttachments().length
}

class ImageServiceError extends Error {
  readonly i18nKey: string
  constructor(i18nKey: string) {
    super(i18nKey)
    this.name = 'ImageServiceError'
    this.i18nKey = i18nKey
  }
}

/**
 * 把一个已存在磁盘上的图片文件复制到应用私有目录并登记为草稿附件。
 * @throws ImageServiceError（带 i18nKey，供渲染端本地化）
 */
function adoptFile(filePath: string): ChatAttachment {
  if (!existsSync(filePath)) {
    throw new ImageServiceError('image.error.notFound')
  }

  const mime = mimeFromPath(filePath)
  if (!mime || !allowedMime(mime)) {
    throw new ImageServiceError('image.error.mime')
  }

  const size = statSync(filePath).size
  if (size > PROTOCOL.MAX_IMAGE_BYTES) {
    throw new ImageServiceError('image.error.tooLarge')
  }
  if (!hasFreeSpace(size)) {
    throw new ImageServiceError('image.error.noSpace')
  }

  // FR-IMG-5：复制到应用私有目录
  const dir = ensureAttachmentsDir()
  const ext = mime === 'image/png' ? '.png' : '.jpg'
  const target = join(dir, `${Date.now()}-${randomUUID()}${ext}`)
  copyFileSync(filePath, target)

  // FR-IMG-6：探测尺寸（用原生 Image，无需引入 sharp）
  let width: number | null = null
  let height: number | null = null
  try {
    const img = nativeImage.createFromPath(target)
    if (!img.isEmpty()) {
      const size2 = img.getSize()
      width = size2.width || null
      height = size2.height || null
    }
  } catch {
    /* 尺寸探测失败不阻塞 */
  }

  return insertAttachment({
    messageId: null,
    sessionId: PROTOCOL.DEFAULT_SESSION_ID,
    mimeType: mime,
    localPath: target,
    fileSize: size,
    width,
    height
  })
}

export class ImageService {
  /** FR-IMG-1 / FR-IMG-2：文件对话框选择（最多 3 张）。 */
  async pick(): Promise<AddImageResult> {
    const remaining = PROTOCOL.MAX_IMAGE_COUNT - currentDraftCount()
    if (remaining <= 0) {
      throw new ImageServiceError('image.error.countLimit')
    }

    const win = BrowserWindow.getFocusedWindow() ?? BrowserWindow.getAllWindows()[0]
    const result = win
      ? await dialog.showOpenDialog(win, {
          title: 'Select images',
          properties: ['openFile', 'multiSelections'],
          filters: [{ name: 'Images', extensions: ['jpg', 'jpeg', 'png'] }]
        })
      : await dialog.showOpenDialog({
          title: 'Select images',
          properties: ['openFile', 'multiSelections'],
          filters: [{ name: 'Images', extensions: ['jpg', 'jpeg', 'png'] }]
        })

    if (result.canceled || result.filePaths.length === 0) return { added: [], rejected: [] }
    return this.addPaths(result.filePaths)
  }

  /** FR-IMG-7：拖拽入窗 / 文件对话框共用入口。 */
  addPaths(filePaths: string[]): AddImageResult {
    const added: ChatAttachment[] = []
    const rejected: AddImageResult['rejected'] = []
    let remaining = PROTOCOL.MAX_IMAGE_COUNT - currentDraftCount()

    for (const filePath of filePaths) {
      if (remaining <= 0) {
        rejected.push({ name: filePath.split('/').pop() ?? filePath, i18nKey: 'image.error.countLimit' })
        continue
      }
      try {
        added.push(adoptFile(filePath))
        remaining -= 1
      } catch (err) {
        const i18nKey = err instanceof ImageServiceError ? err.i18nKey : 'image.error.unknown'
        rejected.push({ name: filePath.split('/').pop() ?? filePath, i18nKey })
        log.warn('图片被拒绝', { filePath, i18nKey })
      }
    }

    if (added.length > 0) this.emitDraftChanged()
    // FR-IMG-2 / EDGE-L15：被拦截的图片必须**有提示**，不能静默消失。
    // 这里在主进程直接推 toast（而不是把 `rejected` 回传给调用方），
    // 使文件对话框、拖拽入窗两条入口都自动获得反馈，且无需改动 IPC 契约。
    this.reportRejections(rejected)
    return { added, rejected }
  }

  /**
   * 把被拒绝的图片按**原因**去重后逐条提示。
   *
   * 去重是有意的：一次拖入 3 个超限文件只应看到一条「单张图片不能超过 20MB」，
   * 而不是三条相同提示。
   */
  private reportRejections(rejected: AddImageResult['rejected']): void {
    if (rejected.length === 0) return
    const reasons = new Set(rejected.map((r) => r.i18nKey))
    for (const i18nKey of reasons) {
      const count = rejected.filter((r) => r.i18nKey === i18nKey).length
      const text = count > 1 ? `${t(i18nKey)}（${count}）` : t(i18nKey)
      bus.send(IPC.evtToast, { level: 'warn', i18nKey, text })
    }
    log.info('已提示被拒绝的图片', {
      files: rejected.map((r) => r.name),
      reasons: [...reasons]
    })
  }

  /**
   * FR-IMG-8 / FR-DSK-5：剪贴板直接贴图（`Ctrl+V`）。
   * 读取 `clipboard.readImage()`，PNG 编码后作为附件——桌面端最高频的增值能力（场景 S3）。
   */
  readClipboard(): AddImageResult {
    if (currentDraftCount() >= PROTOCOL.MAX_IMAGE_COUNT) {
      throw new ImageServiceError('image.error.countLimit')
    }
    const image = clipboard.readImage()
    if (image.isEmpty()) {
      throw new ImageServiceError('image.error.clipboardEmpty')
    }
    const png = image.toPNG()
    if (png.length === 0) throw new ImageServiceError('image.error.clipboardEmpty')
    if (png.length > PROTOCOL.MAX_IMAGE_BYTES) throw new ImageServiceError('image.error.tooLarge')
    if (!hasFreeSpace(png.length)) throw new ImageServiceError('image.error.noSpace')

    const dir = ensureAttachmentsDir()
    const target = join(dir, `${Date.now()}-${randomUUID()}.png`)
    writeFileSync(target, png, { mode: 0o600 })

    const size = image.getSize()
    const attachment = insertAttachment({
      messageId: null,
      sessionId: PROTOCOL.DEFAULT_SESSION_ID,
      mimeType: 'image/png',
      localPath: target,
      fileSize: png.length,
      width: size.width || null,
      height: size.height || null
    })
    log.info('已从剪贴板添加图片', { bytes: png.length })
    this.emitDraftChanged()
    return { added: [attachment], rejected: [] }
  }

  /** FR-IMG-3：单张移除。 */
  remove(attachmentId: string): void {
    const att = getAttachment(attachmentId)
    if (!att) return
    deleteAttachment(attachmentId)
    // 仅在草稿状态（未发送）时删除磁盘文件，历史消息图片必须保留
    if (!att.messageId) this.deleteFileQuietly(att.localPath)
    this.emitDraftChanged()
  }

  /** 读取文件为 base64（供 WS `chat.message` 的 `images[].dataBase64`）。 */
  listDraft(): ChatAttachment[] {
    return listDraftAttachments()
  }

  /** 清空草稿（发送成功后调用，FR-IMG-3）。 */
  clearDraft(deleteFiles = true): void {
    const drafts = listDraftAttachments()
    for (const draft of drafts) {
      deleteAttachment(draft.attachmentId)
      if (deleteFiles) this.deleteFileQuietly(draft.localPath)
    }
    if (drafts.length) this.emitDraftChanged()
  }

  /** FR-IMG-9：另存为。 */
  async saveAs(localPath: string): Promise<{ saved: boolean; path: string | null }> {
    if (!existsSync(localPath)) return { saved: false, path: null }
    const win = BrowserWindow.getFocusedWindow() ?? BrowserWindow.getAllWindows()[0]
    const defaultName = localPath.split('/').pop() ?? 'image.png'
    const result = win
      ? await dialog.showSaveDialog(win, { defaultPath: defaultName })
      : await dialog.showSaveDialog({ defaultPath: defaultName })
    if (result.canceled || !result.filePath) return { saved: false, path: null }
    try {
      copyFileSync(localPath, result.filePath)
      return { saved: true, path: result.filePath }
    } catch (err) {
      log.error('另存为失败', { error: String(err) })
      return { saved: false, path: null }
    }
  }

  /** 用系统默认查看器打开大图（FR-IMG-9 的降级路径）。 */
  async openExternal(localPath: string): Promise<void> {
    if (!existsSync(localPath)) return
    await shell.openPath(localPath)
  }

  /** EDGE-L15：附件占用统计。 */
  usage(): AttachmentUsage {
    return repoUsage()
  }

  /** EDGE-L15：按时间清理附件（仅清理未挂到消息上的历史草稿 + 指定阈值之前的已发送附件需谨慎）。 */
  cleanup(olderThanMs: number): { removed: number; freedBytes: number } {
    const cutoff = Date.now() - olderThanMs
    const targets = listAttachmentsOlderThan(cutoff)
    let removed = 0
    let freedBytes = 0
    for (const att of targets) {
      // 只清理草稿（未发送）附件；已发送消息的图片是历史记录的一部分，不自动删除
      if (att.messageId) continue
      deleteAttachment(att.attachmentId)
      this.deleteFileQuietly(att.localPath)
      removed += 1
      freedBytes += att.fileSize
    }
    // 同时清理磁盘上已无数据库记录的孤儿文件
    removed += this.sweepOrphanFiles()
    if (removed > 0) this.emitDraftChanged()
    log.info('附件清理完成', { removed, freedBytes })
    return { removed, freedBytes }
  }

  /** 删除磁盘上没有数据库记录的附件文件。 */
  private sweepOrphanFiles(): number {
    const dir = paths().attachmentsDir
    if (!existsSync(dir)) return 0
    const known = new Set(listAllAttachmentPaths())
    let removed = 0
    try {
      for (const name of readdirSync(dir)) {
        const full = join(dir, name)
        if (!known.has(full)) {
          this.deleteFileQuietly(full)
          removed += 1
        }
      }
    } catch (err) {
      log.debug('孤儿附件扫描失败', { error: String(err) })
    }
    return removed
  }

  private deleteFileQuietly(filePath: string): void {
    try {
      if (existsSync(filePath)) unlinkSync(filePath)
    } catch (err) {
      log.debug('删除附件文件失败', { filePath, error: String(err) })
    }
  }

  private emitDraftChanged(): void {
    bus.send(IPC.evtDraftImagesChanged, { images: listDraftAttachments() })
  }
}

/* -------------------------------------------------------------------------- */
/* 供 chat-service 同步读取文件内容（WS 发送需要 base64）                         */
/* -------------------------------------------------------------------------- */

/**
 * 同步读取图片为 base64。
 * ⚠️ NFR-6：**禁止**把返回值写入日志。
 */
export function readFileAsBase64(filePath: string): string {
  try {
    return readFileSync(filePath).toString('base64')
  } catch (err) {
    log.error('读取图片失败', { filePath, error: String(err) })
    return ''
  }
}

/** 确保附件目录存在（启动时调用）。 */
export function initAttachmentDir(): void {
  ensureDirs()
  ensureAttachmentsDir()
}
