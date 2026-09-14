#!/bin/bash
# 一次性恢复完整的 Chromium 沙箱（需要 sudo）。
#
#   ./scripts/fix-sandbox.sh
#
# 不跑这个脚本客户端**也能启动**——启动包装器会自动降级。跑它只是为了拿回
# 被降级掉的那层沙箱防护。两件事：
#
#   ① 把开发期与 linux-unpacked 里的 chrome-sandbox 改成 root:4755，
#      让 `npm run dev` / `dist/linux-unpacked/tks-desktop` 走完整 SUID 沙箱。
#      （deb/rpm 安装版由 build/after-install.tpl 自动完成，无需手工。）
#
#   ② Ubuntu 24.04 起 `kernel.apparmor_restrict_unprivileged_userns=1`，
#      没有 AppArmor 配置的程序不许创建非特权用户命名空间，AppImage 因此
#      连命名空间沙箱也用不了。这里按 Ubuntu 自己给 Chrome/VS Code 的写法，
#      装一份只授予 `userns` 的 unconfined 配置。
#
# 卸载：sudo rm /etc/apparmor.d/tks-desktop && sudo systemctl reload apparmor

set -euo pipefail

ROOT="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"

if [ "$(id -u)" != "0" ]; then
  echo "需要 root 权限，正在通过 sudo 重新执行……"
  exec sudo -- "$0" "$@"
fi

# ---------------------------------------------------------------------------
# ① chrome-sandbox → root:4755
# ---------------------------------------------------------------------------
fixed=0
for helper in \
  "$ROOT/node_modules/electron/dist/chrome-sandbox" \
  "$ROOT/dist/linux-unpacked/chrome-sandbox"
do
  if [ -f "$helper" ]; then
    chown root:root "$helper"
    chmod 4755 "$helper"
    echo "✓ SUID 沙箱已修复：$helper"
    fixed=$((fixed + 1))
  fi
done
[ "$fixed" -gt 0 ] || echo "· 未找到 chrome-sandbox（尚未 npm install / 尚未打包），跳过"

# ---------------------------------------------------------------------------
# ② AppArmor：允许本应用创建非特权用户命名空间
# ---------------------------------------------------------------------------
if [ -d /etc/apparmor.d ] && [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2>/dev/null || echo 0)" = "1" ]; then
  cat > /etc/apparmor.d/tks-desktop <<'PROFILE'
# 本配置不做任何限制（flags=(unconfined)），唯一作用是把 `userns` 授予 TKS Desktop，
# 抵消 Ubuntu 24.04 起对非特权用户命名空间的默认拒绝——否则 AppImage 里的
# Chromium 既用不了 SUID 沙箱（nosuid 挂载），也用不了命名空间沙箱，只能 --no-sandbox。
# 写法对齐 Ubuntu 官方给 /etc/apparmor.d/chrome、/etc/apparmor.d/code 的配置。

abi <abi/4.0>,
include <tunables/global>

profile tks-desktop /{opt/TKS?Desktop,opt/tks-desktop,usr/lib/tks-desktop}/tks-desktop{,.bin} flags=(unconfined) {
  userns,

  include if exists <local/tks-desktop>
}

profile tks-desktop-appimage /tmp/.mount_*/tks-desktop{,.bin} flags=(unconfined) {
  userns,

  include if exists <local/tks-desktop>
}
PROFILE
  if apparmor_parser -r /etc/apparmor.d/tks-desktop 2>/dev/null; then
    echo "✓ AppArmor 配置已安装并加载：/etc/apparmor.d/tks-desktop"
  else
    echo "! AppArmor 配置已写入，但加载失败；执行 sudo systemctl reload apparmor 再试"
  fi
else
  echo "· 本机未启用 userns 限制，无需安装 AppArmor 配置"
fi

echo
echo "完成。重新启动客户端后，启动包装器会自动改用更强的沙箱模式。"
