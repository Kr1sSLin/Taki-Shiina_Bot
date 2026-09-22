#!/usr/bin/env bash
#
# TKS 一键发布 —— 构建 Linux deb 与 Android APK，输出到 ~/Desktop/Release。
#
# 版本策略（需求方确认）：
#   - deb 与 apk **各自独立递增**：Linux 走 package.json 的 version（补丁位 +1），
#     Android 走 app/build.gradle.kts 的 versionName（补丁位 +1）+ versionCode +1。
#   - 每次发布先递增版本号，再构建，因此产物文件名里的版本号永远是"本次"的。
#
# 用法：
#   scripts/release.sh              # deb + apk 全量发布
#   scripts/release.sh deb          # 只发 deb
#   scripts/release.sh apk          # 只发 apk
#   scripts/release.sh --no-bump    # 不递增版本号，按当前版本号重新构建
#
# 环境变量：
#   TKS_RELEASE_DIR   产物输出目录（默认 /home/administrator/Desktop/Release）
#   TKS_ANDROID_KEYSTORE / TKS_ANDROID_KEYSTORE_PASS / TKS_ANDROID_KEY_ALIAS
#                      Android 发布签名；缺省时自动回退到 ~/1.jks + 123456 + 别名 1
#   ANDROID_HOME       Android SDK（默认 /home/administrator/Android/Sdk）
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LINUX_DIR="$ROOT/TKS_Bot_Linux"
ANDROID_DIR="$ROOT/TKS_Bot_Android"
RELEASE_DIR="${TKS_RELEASE_DIR:-/home/administrator/Desktop/Release}"

BUMP=1
TARGETS=()
for arg in "$@"; do
  case "$arg" in
    --no-bump) BUMP=0 ;;
    deb|apk)   TARGETS+=("$arg") ;;
    -h|--help) sed -n '3,22p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知参数：$arg" >&2; exit 2 ;;
  esac
done
[ ${#TARGETS[@]} -eq 0 ] && TARGETS=(deb apk)

# 沙箱/无桌面环境下的 Gradle 与 JDK 定位（工作区内可写目录）
export GRADLE_USER_HOME="$ROOT/.gradle-home"
export ANDROID_USER_HOME="$ROOT/.android-home"
export XDG_DATA_HOME="$ROOT/.xdg-home"
export ANDROID_HOME="${ANDROID_HOME:-/home/administrator/Android/Sdk}"
export JAVA_HOME="${JAVA_HOME:-$ROOT/.jdk-home/jdk-17.0.20.1+1}"

# Android 发布签名（沿用旧包同一密钥 CN=KrisSLin，保证可覆盖安装）
export TKS_ANDROID_KEYSTORE="${TKS_ANDROID_KEYSTORE:-$HOME/1.jks}"
export TKS_ANDROID_KEYSTORE_PASS="${TKS_ANDROID_KEYSTORE_PASS:-123456}"
export TKS_ANDROID_KEY_ALIAS="${TKS_ANDROID_KEY_ALIAS:-1}"

mkdir -p "$RELEASE_DIR"

log()  { printf '\n\033[1m▶ %s\033[0m\n' "$*"; }
warn() { printf '\033[33m  ! %s\033[0m\n' "$*"; }
ok()   { printf '\033[32m  ✓ %s\033[0m\n' "$*"; }

# ---- 版本号递增 -------------------------------------------------------------
# bump_patch 1.2.0 -> 1.2.1
bump_patch() {
  local v="$1" major minor patch
  IFS='.' read -r major minor patch <<<"$v"
  patch="${patch:-0}"
  echo "$major.$minor.$((patch + 1))"
}

bump_linux_version() {
  local pkg="$LINUX_DIR/package.json"
  local cur next
  cur="$(node -p "require('$pkg').version")"
  next="$(bump_patch "$cur")"
  # 只替换顶层 version 字段，避免动到依赖里的版本号
  node -e "
    const fs=require('fs');const p='$pkg';
    const raw=fs.readFileSync(p,'utf8');
    const out=raw.replace(/^(\s*\"version\":\s*\")[^\"]+(\",)/m, '\$1$next\$2');
    if(out===raw){console.error('未匹配到 version 字段');process.exit(1)}
    fs.writeFileSync(p,out);
  "
  LINUX_VERSION="$next"
  ok "Linux 版本：$cur → $next"
}

bump_android_version() {
  local gradle="$ANDROID_DIR/app/build.gradle.kts"
  local cur_code cur_name next_name next_code
  cur_code="$(grep -oP 'versionCode\s*=\s*\K[0-9]+' "$gradle" | head -1)"
  cur_name="$(grep -oP 'versionName\s*=\s*"\K[^"]+' "$gradle" | head -1)"
  next_name="$(bump_patch "$cur_name")"
  next_code=$((cur_code + 1))
  sed -i -E "s/(versionCode\s*=\s*)$cur_code/\1$next_code/" "$gradle"
  sed -i -E "s/(versionName\s*=\s*\")$cur_name(\")/\1$next_name\2/" "$gradle"
  ANDROID_VERSION="$next_name"
  ANDROID_VERSION_CODE="$next_code"
  ok "Android 版本：$cur_name($cur_code) → $next_name($next_code)"
}

# ---- 构建 ------------------------------------------------------------------
build_deb() {
  log "构建 Linux deb"
  ( cd "$LINUX_DIR" && npm run build:deb )
  local src="$RELEASE_DIR/TKS Desktop-$LINUX_VERSION-amd64.deb"
  # electron-builder 的产物名用 `${arch}`，x64 会落成 amd64
  if [ ! -f "$src" ]; then
    src="$(ls -t "$RELEASE_DIR"/TKS\ Desktop-"$LINUX_VERSION"-*.deb 2>/dev/null | head -1 || true)"
  fi
  [ -f "$src" ] || { echo "未找到 deb 产物" >&2; exit 1; }
  # 统一命名，便于区分版本
  local dst="$RELEASE_DIR/TKS-Desktop-$LINUX_VERSION-linux-amd64.deb"
  mv -f "$src" "$dst"
  deb_path="$dst"
  ok "deb → $dst ($(du -h "$dst" | cut -f1))"
  # electron-builder 会在输出目录留下 unpacked 目录与两个 builder-*.yml 中间文件，
  # Release 只保留可分发产物，构建完成后清理干净。
  rm -rf "$RELEASE_DIR"/linux-unpacked "$RELEASE_DIR"/builder-debug.yml \
         "$RELEASE_DIR"/builder-effective-config.yaml
}

build_apk() {
  log "构建 Android APK（release，含签名）"
  # ⚠️ 关键坑（Windows 侧实测同源，Gradle 行为与平台无关）：
  # TKS_Bot_Android/app/build.gradle.kts 判定 hasReleaseSigning 时**只看环境变量是否有值**，
  # 并不校验密钥文件是否存在：
  #     val hasReleaseSigning = !releaseKeystorePath.isNullOrBlank() && !releaseKeystorePass.isNullOrBlank()
  # 因此密钥文件缺失时必须把这些变量**清掉**，否则 Gradle 会创建出 signingConfig 并在
  # :app:validateSigningRelease 直接硬失败（Keystore file '...' not found for signing config 'release'），
  # 而不是按设计退化为未签名产物。脚本顶部（第 48-51 行）默认导出这些变量，所以必须在这里撤销。
  # 注意 `set -u` 下 unset 后不能再引用，故先取副本用于提示。
  ks_path="${TKS_ANDROID_KEYSTORE:-}"
  if [ -z "$ks_path" ] || [ ! -f "$ks_path" ]; then
    warn "未找到签名密钥 ${ks_path:-（未设置）} —— 已清除签名环境变量，将产出未签名 APK"
    unset TKS_ANDROID_KEYSTORE TKS_ANDROID_KEYSTORE_PASS TKS_ANDROID_KEY_ALIAS
  fi
  ( cd "$ANDROID_DIR" && ./gradlew :app:assembleRelease --console=plain \
      -Dkotlin.compiler.execution.strategy=in-process )
  local src="$ANDROID_DIR/app/build/outputs/apk/release/app-release.apk"
  [ -f "$src" ] || src="$ANDROID_DIR/app/build/outputs/apk/release/app-release-unsigned.apk"
  [ -f "$src" ] || { echo "未找到 apk 产物" >&2; exit 1; }
  local dst="$RELEASE_DIR/TKS-Android-$ANDROID_VERSION-$ANDROID_VERSION_CODE.apk"
  cp -f "$src" "$dst"
  apk_path="$dst"
  # 校验签名
  local bt="" signer=""
  bt="$(ls -d "$ANDROID_HOME"/build-tools/* 2>/dev/null | sort -V | tail -1)"
  if [ -n "$bt" ] && [ -x "$bt/apksigner" ]; then
    signer="$("$bt/apksigner" verify --print-certs "$dst" 2>/dev/null | grep -m1 'certificate DN' || true)"
  fi
  ok "apk → $dst ($(du -h "$dst" | cut -f1))"
  if [ -n "$signer" ]; then ok "签名：${signer#*: }"; else warn "APK 未签名（无法覆盖安装已装的 release 版）"; fi
}

# ---- 主流程 ----------------------------------------------------------------
log "TKS 发布　输出目录：$RELEASE_DIR"
[ "$BUMP" -eq 1 ] && log "递增版本号" || warn "跳过版本号递增（--no-bump）"

deb_path=""; apk_path=""

for t in "${TARGETS[@]}"; do
  case "$t" in
    deb)
      [ "$BUMP" -eq 1 ] && bump_linux_version || LINUX_VERSION="$(node -p "require('$LINUX_DIR/package.json').version")"
      build_deb
      ;;
    apk)
      if [ "$BUMP" -eq 1 ]; then bump_android_version
      else
        ANDROID_VERSION="$(grep -oP 'versionName\s*=\s*"\K[^"]+' "$ANDROID_DIR/app/build.gradle.kts" | head -1)"
        ANDROID_VERSION_CODE="$(grep -oP 'versionCode\s*=\s*\K[0-9]+' "$ANDROID_DIR/app/build.gradle.kts" | head -1)"
      fi
      build_apk
      ;;
  esac
done

log "产物清单"
( cd "$RELEASE_DIR" && ls -la ) | sed 's/^/  /'
[ -n "$deb_path" ] && sha256sum "$deb_path" | sed 's/^/  /'
[ -n "$apk_path" ] && sha256sum "$apk_path" | sed 's/^/  /'
printf '\n\033[32m发布完成\033[0m\n'
