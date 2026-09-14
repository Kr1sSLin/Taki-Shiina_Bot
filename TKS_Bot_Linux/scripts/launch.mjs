/**
 * 开发期启动器：`npm run dev` / `npm start` 的实际入口。
 *
 * 与打包版的 build/sandbox-launcher.sh 同一个目的——在 electron 进程真正起来之前
 * 选好沙箱模式。`node_modules/electron/dist/chrome-sandbox` 随 npm 安装下来是
 * 0755 且属主为当前用户，不满足 Chromium 要求的 root:4755，于是
 * `electron-vite dev` 一启动就 FATAL：
 *
 *   FATAL:setuid_sandbox_host.cc(163) The SUID sandbox helper binary was found,
 *   but is not configured correctly.
 *
 * 崩溃发生在主进程 JS 之前，只能从命令行补开关。electron-vite 会把 `--` 之后的参数
 * 原样透传给它 spawn 出来的 electron，这里就是往那后面塞探测结果。
 *
 * ⚠️ 不要改用 `ELECTRON_CLI_ARGS` 环境变量：electron-vite 的 CLI 无条件执行
 *    `process.env.ELECTRON_CLI_ARGS = JSON.stringify(options['--'])`，而 cac 在没有
 *    `--` 时给的是**空数组**（truthy），于是外部设好的值会被静默覆盖成 `[]`。
 *
 * 用法：node scripts/launch.mjs <dev|preview> [透传给 electron-vite 的参数...]
 */

import { spawn, spawnSync } from 'node:child_process'
import { readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const ELECTRON_DIST = join(ROOT, 'node_modules/electron/dist')

/** 读取 /proc 下的开关，读不到返回 null（视为「无此限制」） */
function readProc(path) {
  try {
    return readFileSync(path, 'utf8').trim()
  } catch {
    return null
  }
}

/** chrome-sandbox 是否满足 root 属主 + setuid 位（否则 Chromium 会 FATAL） */
function suidSandboxUsable() {
  if (typeof process.getuid === 'function' && process.getuid() === 0) return true
  try {
    const st = statSync(join(ELECTRON_DIST, 'chrome-sandbox'))
    return st.uid === 0 && (st.mode & 0o4000) !== 0
  } catch {
    // 文件不存在时 Chromium 不会 FATAL，会自行另寻沙箱方式
    return true
  }
}

/** 非特权用户命名空间是否可用（命名空间沙箱的前提） */
function usernsUsable() {
  if (readProc('/proc/sys/user/max_user_namespaces') === '0') return false
  const clone = readProc('/proc/sys/kernel/unprivileged_userns_clone')
  if (clone !== null && clone !== '1') return false

  // 实测一次最准：unshare 与 electron 一样没有 AppArmor 配置，
  // 它被拒 == Chromium 会被拒（Ubuntu 24.04 的 userns 限制按可执行文件配置生效）
  const probe = spawnSync('unshare', ['--user', '--map-root-user', 'true'], { stdio: 'ignore' })
  if (probe.error === undefined) return probe.status === 0

  return readProc('/proc/sys/kernel/apparmor_restrict_unprivileged_userns') !== '1'
}

/** @returns {{ mode: string, args: string[] }} */
function resolveSandbox() {
  const forced = process.env.TKS_SANDBOX
  const mode =
    forced && forced !== 'auto'
      ? forced
      : suidSandboxUsable()
        ? 'suid'
        : usernsUsable()
          ? 'namespace'
          : 'none'

  switch (mode) {
    case 'suid':
      return { mode, args: [] }
    case 'namespace':
      return { mode, args: ['--disable-setuid-sandbox'] }
    case 'none':
      return { mode, args: ['--no-sandbox'] }
    default:
      console.error(`TKS_SANDBOX 取值非法：${mode}（可选 auto|suid|namespace|none）`)
      process.exit(2)
  }
}

const [command, ...rest] = process.argv.slice(2)
if (command !== 'dev' && command !== 'preview') {
  console.error('用法：node scripts/launch.mjs <dev|preview> [args...]')
  process.exit(2)
}

const { mode, args } = resolveSandbox()
const NOTE = {
  suid: 'SUID 沙箱（chrome-sandbox 已是 root:4755）',
  namespace: '命名空间沙箱（--disable-setuid-sandbox）',
  none: '⚠ 无沙箱（--no-sandbox）——本机既没有可用的 SUID helper，内核也不允许非特权用户命名空间。\n' +
    '   执行 scripts/fix-sandbox.sh 可一次性修好（需要 sudo）。'
}
console.log(`[launch] 沙箱模式：${NOTE[mode]}`)

// 调用方自己写的 `--` 之后的参数要保留，但其中的沙箱开关由上面的探测结果接管
const sep = rest.indexOf('--')
const viteArgs = sep === -1 ? rest : rest.slice(0, sep)
const electronArgs = (sep === -1 ? [] : rest.slice(sep + 1)).filter(
  (a) => a !== '--no-sandbox' && a !== '--disable-setuid-sandbox'
)

const child = spawn(
  process.execPath,
  [
    join(ROOT, 'node_modules/electron-vite/bin/electron-vite.js'),
    command,
    ...viteArgs,
    '--',
    ...electronArgs,
    ...args
  ],
  { stdio: 'inherit', cwd: ROOT }
)
child.on('close', (code, signal) => process.exit(signal ? 1 : (code ?? 0)))
