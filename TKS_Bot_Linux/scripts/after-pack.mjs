/**
 * electron-builder afterPack 钩子：安装 Chromium 沙箱启动包装器。
 *
 * 把 electron 产出的可执行文件 `tks-desktop` 改名为 `tks-desktop.bin`，
 * 再把 `build/sandbox-launcher.sh` 装成 `tks-desktop`。于是 AppImage 的 AppRun、
 * deb/rpm 的 `.desktop`、命令行直接执行——所有入口都先经过包装器，
 * 由它在 exec 之前挑一个当前环境**确实可用**的沙箱模式。
 *
 * 为什么不能在主进程里做：Chromium 的沙箱初始化早于主进程 JS，
 * 不可用时是 FATAL 直接退出，`app.commandLine.appendSwitch` 根本来不及。
 * 详见 build/sandbox-launcher.sh 顶部注释。
 *
 * afterPack 在 app 落盘到 appOutDir 之后、各 target 打包之前执行，
 * 因此 linux-unpacked / AppImage / deb / rpm 都会带上包装器。
 */

import { chmod, readFile, rename, stat, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const LAUNCHER_SRC = join(ROOT, 'build/sandbox-launcher.sh')

async function exists(path) {
  try {
    await stat(path)
    return true
  } catch {
    return false
  }
}

export default async function afterPack(context) {
  if (context.electronPlatformName !== 'linux') return

  const outDir = context.appOutDir
  const exeName = context.packager.executableName
  const launcherPath = join(outDir, exeName)
  const realBinName = `${exeName}.bin`
  const realBinPath = join(outDir, realBinName)

  // 幂等：重复打包（electron-builder 可能复用 appOutDir）时不要把包装器再改名一次
  if (await exists(realBinPath)) {
    console.log(`  • sandbox launcher  already installed (${realBinName})`)
    return
  }

  await rename(launcherPath, realBinPath)

  const launcher = (await readFile(LAUNCHER_SRC, 'utf8')).replaceAll('@BIN_NAME@', realBinName)
  await writeFile(launcherPath, launcher, { mode: 0o755 })
  await chmod(launcherPath, 0o755)

  console.log(`  • sandbox launcher  installed as ${exeName} (real binary: ${realBinName})`)
}
