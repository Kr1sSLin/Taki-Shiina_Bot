/**
 * 自定义协议 `tks-attachment://`（FR-IMG-9，NFR-5）。
 *
 * 渲染进程运行在 `sandbox: true` + `contextIsolation: true` 下，**不能**直接读文件，
 * 也不应把本地图片以 `file://` 暴露给页面。因此注册一个受限协议：
 *
 *   `tks-attachment://local<URI 编码后的绝对路径>`
 *
 * **安全约束（关键）**：只允许读取**应用附件目录**内的文件。
 * 任何越界路径（如 `../../.ssh/id_rsa`）一律拒绝，防止渲染进程借协议读任意文件。
 */

import { protocol, net } from 'electron'
import { existsSync, realpathSync } from 'node:fs'
import { extname, sep } from 'node:path'
import { pathToFileURL } from 'node:url'
import { createLogger } from '../app/logger'
import { paths } from '../app/paths'

const log = createLogger('protocol')

export const ATTACHMENT_SCHEME = 'tks-attachment'

/**
 * 必须在 `app.whenReady()` **之前**调用。
 * `standard: true` 让 URL 具备正常的 host/path 解析能力；
 * `secure: true` 使其被视为安全上下文（否则会被 CSP 的 `img-src` 拦掉）。
 */
export function registerAttachmentScheme(): void {
  protocol.registerSchemesAsPrivileged([
    {
      scheme: ATTACHMENT_SCHEME,
      privileges: {
        standard: true,
        secure: true,
        supportFetchAPI: true,
        stream: true,
        bypassCSP: false
      }
    }
  ])
}

const MIME_BY_EXT: Record<string, string> = {
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg'
}

/** 校验目标路径确实位于附件目录内（先解析真实路径，防符号链接越界）。 */
function resolveWithinAttachments(decoded: string): string | null {
  const attachmentsDir = realpathSync(paths().attachmentsDir)
  let target: string
  try {
    target = realpathSync(decoded)
  } catch {
    return null
  }
  const normalizedDir = attachmentsDir.endsWith(sep) ? attachmentsDir : `${attachmentsDir}${sep}`
  if (!target.startsWith(normalizedDir)) {
    log.warn('拒绝越界附件读取', { requested: decoded })
    return null
  }
  return target
}

/** 在 `app.whenReady()` 之后调用。 */
export function registerAttachmentHandler(): void {
  protocol.handle(ATTACHMENT_SCHEME, async (request) => {
    let decoded = ''
    try {
      const url = new URL(request.url)
      // `tks-attachment://local/home/x/a.png` → host='local', pathname='/home/x/a.png'
      decoded = decodeURIComponent(url.pathname)
    } catch {
      return new Response('bad request', { status: 400 })
    }

    if (!decoded || !existsSync(decoded)) {
      return new Response('not found', { status: 404 })
    }

    const safePath = resolveWithinAttachments(decoded)
    if (!safePath) {
      return new Response('forbidden', { status: 403 })
    }

    const mime = MIME_BY_EXT[extname(safePath).toLowerCase()]
    if (!mime) {
      return new Response('unsupported media type', { status: 415 })
    }

    try {
      // 交给 Electron 的 net 读取本地文件并流式返回
      const response = await net.fetch(pathToFileURL(safePath).toString())
      return new Response(response.body, {
        status: 200,
        headers: {
          'content-type': mime,
          // 附件是不可变内容，可安全长缓存
          'cache-control': 'private, max-age=31536000, immutable'
        }
      })
    } catch (err) {
      log.warn('附件读取失败', { error: String(err) })
      return new Response('read error', { status: 500 })
    }
  })

  log.info('附件协议已注册', { scheme: ATTACHMENT_SCHEME, dir: paths().attachmentsDir })
}

/**
 * 把本地附件绝对路径转成渲染进程可用的 URL。
 * 与 `docs/RENDERER_CONTRACT.md` 中约定的格式保持一致。
 */
export function toAttachmentUrl(localPath: string): string {
  return `${ATTACHMENT_SCHEME}://local${encodeURI(localPath)}`
}
