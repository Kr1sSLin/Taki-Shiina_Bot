#!/usr/bin/env node
/**
 * `npm run fix-sandbox` 的跨平台入口。
 *
 * POSIX（Linux/macOS）：原样调用 `scripts/fix-sandbox.sh`（该脚本自己会 sudo 提权），
 * 一次性恢复 SUID 沙箱 helper 与 AppArmor userns 配置；行为与原先的
 * `bash scripts/fix-sandbox.sh` 完全一致，退出码原样透传。
 *
 * Windows：Chromium 在 win32 上不使用 Linux 的 setuid `chrome-sandbox`，
 * 也没有 AppArmor userns 限制，**没有等价的修复动作**，因此打印说明并以 0 退出，
 * 让 `npm run fix-sandbox` 在 Windows 上不再必然失败（原本的 `bash ...` 永远找不到 bash）。
 */

import { spawnSync } from 'node:child_process'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

if (process.platform === 'win32') {
  console.log('Windows 无需修复 sandbox，已跳过。')
  console.log('（该脚本只处理 Linux 的 setuid chrome-sandbox 与 AppArmor userns 限制。）')
  process.exit(0)
}

const script = join(dirname(fileURLToPath(import.meta.url)), 'fix-sandbox.sh')
const result = spawnSync('bash', [script], { stdio: 'inherit' })

if (result.error) {
  console.error(`无法执行 ${script}：${result.error.message}（需要 bash）`)
  process.exit(1)
}

process.exit(result.status ?? 1)
