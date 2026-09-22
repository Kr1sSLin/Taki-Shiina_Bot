#!/usr/bin/env node
/**
 * 生成构建产物的 SHA256 校验和（FR-PKG-6）。
 *
 * 用法：node scripts/checksums.mjs
 * 输出：$TKS_RELEASE_DIR/SHA256SUMS（默认桌面的 Release 目录，与打包输出一致）
 */

import { createHash } from 'node:crypto'
import { createReadStream } from 'node:fs'
import { readdir, stat, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { homedir } from 'node:os'

// 产物输出目录已改到桌面的 Release（见 electron-builder.yml 的 directories.output），
// 这里跟随同一路径，可用 TKS_RELEASE_DIR 覆盖；默认值跨平台（Windows/Linux 均为 ~/Desktop/Release）。
const DIST = process.env.TKS_RELEASE_DIR ?? join(homedir(), 'Desktop', 'Release')
const PATTERNS = /\.(AppImage|deb|rpm|flatpak|zip|tar\.gz)$/i

function sha256(file) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256')
    createReadStream(file)
      .on('data', (chunk) => hash.update(chunk))
      .on('end', () => resolve(hash.digest('hex')))
      .on('error', reject)
  })
}

function human(bytes) {
  const units = ['B', 'KB', 'MB', 'GB']
  let v = bytes
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i += 1
  }
  return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`
}

async function main() {
  let entries
  try {
    entries = await readdir(DIST)
  } catch {
    console.error(`未找到 ${DIST}/ 目录，请先执行 npm run build:linux`)
    process.exit(1)
  }

  const files = entries.filter((name) => PATTERNS.test(name)).sort()
  if (files.length === 0) {
    console.error(`${DIST}/ 下没有可校验的构建产物`)
    process.exit(1)
  }

  const lines = []
  for (const name of files) {
    const full = join(DIST, name)
    const info = await stat(full)
    const digest = await sha256(full)
    lines.push(`${digest}  ${name}`)
    console.log(`${digest}  ${name}  (${human(info.size)})`)
  }

  const header = `# TKS Desktop 构建产物校验和\n# 生成时间：${new Date().toISOString()}\n# 校验方式：sha256sum -c SHA256SUMS\n`
  await writeFile(join(DIST, 'SHA256SUMS'), `${header}${lines.join('\n')}\n`, 'utf8')
  console.log(`\n已写入 ${join(DIST, 'SHA256SUMS')}（${files.length} 个产物）`)
}

main().catch((err) => {
  console.error(err)
  process.exit(1)
})
