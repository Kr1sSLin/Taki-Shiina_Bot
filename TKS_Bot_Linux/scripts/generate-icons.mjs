#!/usr/bin/env node
/**
 * 程序化生成占位图标资源。
 *
 * 背景（FR-UI-8 / §11.2）：PRD 要求「请设计另出符合 XDG Icon Theme 的
 * 16/32/48/64/128/256/512 多尺寸切图」，但 `视觉资产/` 目录当前仅有 `熊猫图像.txt`，
 * 美术资源尚未交付。因此本脚本按「熊猫」意象生成**占位**图标，
 * 保证托盘、窗口、桌面快捷方式与打包产物不会出现空图标/破图。
 *
 * 交付形式完全符合 XDG Icon Theme 规范（多尺寸 PNG + `index.theme`），
 * 后续美术资源到位后**直接替换同名 PNG** 即可，无需改代码。
 *
 * 依赖：仅 Node 内置 `zlib`。用法：`npm run icons`
 */

import { deflateSync } from 'node:zlib'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = dirname(fileURLToPath(import.meta.url))
const ROOT = join(__dirname, '..')
const ICON_DIR = join(ROOT, 'resources', 'icons')
/**
 * electron-builder 的应用图标目录。
 *
 * ⚠️ 必须**只**放应用图标，且用 `{size}x{size}.png` 命名。
 *    若把 `resources/icons`（内含托盘 22px、徽章 64px、物品 128px 等杂项）直接作为
 *    `linux.icon`，electron-builder 会把每个 PNG 都当成一个尺寸，
 *    于是产出 `hicolor/5x5`、`hicolor/2x2` 这类荒谬目录（实测已复现）。
 */
const APPICON_DIR = join(ROOT, 'build', 'icons')

/* -------------------------------------------------------------------------- */
/* 最小 PNG 编码器（RGBA8，无依赖）                                             */
/* -------------------------------------------------------------------------- */

const CRC_TABLE = (() => {
  const table = new Int32Array(256)
  for (let n = 0; n < 256; n += 1) {
    let c = n
    for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    table[n] = c
  }
  return table
})()

function crc32(buf) {
  let c = 0xffffffff
  for (let i = 0; i < buf.length; i += 1) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

function chunk(type, data) {
  const len = Buffer.alloc(4)
  len.writeUInt32BE(data.length, 0)
  const typeBuf = Buffer.from(type, 'ascii')
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0)
  return Buffer.concat([len, typeBuf, data, crc])
}

/** @param {Uint8Array} rgba 长度 = width*height*4 */
function encodePng(width, height, rgba) {
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(width, 0)
  ihdr.writeUInt32BE(height, 4)
  ihdr[8] = 8 // bit depth
  ihdr[9] = 6 // color type RGBA
  ihdr[10] = 0 // compression
  ihdr[11] = 0 // filter
  ihdr[12] = 0 // interlace

  // 每行前置 filter byte 0
  const stride = width * 4
  const raw = Buffer.alloc((stride + 1) * height)
  for (let y = 0; y < height; y += 1) {
    raw[y * (stride + 1)] = 0
    Buffer.from(rgba.buffer, rgba.byteOffset + y * stride, stride).copy(raw, y * (stride + 1) + 1)
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0))
  ])
}

/* -------------------------------------------------------------------------- */
/* 简易绘图（4x 超采样抗锯齿）                                                   */
/* -------------------------------------------------------------------------- */

class Canvas {
  /**
   * @param {number} size 输出边长（像素）
   * @param {number} ss 超采样倍数
   * @param {number} design 设计稿边长；所有绘图坐标按 `size / design` 缩放。
   *   默认等于 `size`，即 1 设计单位 = 1 像素。
   */
  constructor(size, ss = 4, design = size) {
    this.size = size
    this.ss = ss
    this.design = design
    /** 设计单位 → 超采样像素 的缩放系数 */
    this.k = (size / design) * ss
    this.w = size * ss
    this.h = size * ss
    /** 超采样缓冲：RGBA，直接存 0-255 */
    this.buf = new Float64Array(this.w * this.h * 4)
  }

  /** 以「超采样坐标」写入一个不透明像素（source-over 简化：直接覆盖） */
  put(x, y, [r, g, b, a = 255]) {
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return
    const i = (y * this.w + x) * 4
    const sa = a / 255
    const da = this.buf[i + 3] / 255
    const outA = sa + da * (1 - sa)
    if (outA <= 0) return
    this.buf[i] = (r * sa + this.buf[i] * da * (1 - sa)) / outA
    this.buf[i + 1] = (g * sa + this.buf[i + 1] * da * (1 - sa)) / outA
    this.buf[i + 2] = (b * sa + this.buf[i + 2] * da * (1 - sa)) / outA
    this.buf[i + 3] = outA * 255
  }

  /** 用谓词填充（谓词接收**设计坐标**） */
  fillIf(predicate, color) {
    const k = this.k
    for (let y = 0; y < this.h; y += 1) {
      for (let x = 0; x < this.w; x += 1) {
        if (predicate((x + 0.5) / k, (y + 0.5) / k)) this.put(x, y, color)
      }
    }
  }

  /** 用谓词擦除（destination-out），谓词接收**设计坐标** */
  clearIf(predicate) {
    const k = this.k
    for (let y = 0; y < this.h; y += 1) {
      for (let x = 0; x < this.w; x += 1) {
        if (!predicate((x + 0.5) / k, (y + 0.5) / k)) continue
        const i = (y * this.w + x) * 4
        this.buf[i] = 0
        this.buf[i + 1] = 0
        this.buf[i + 2] = 0
        this.buf[i + 3] = 0
      }
    }
  }

  circle(cx, cy, r, color) {
    this.ellipse(cx, cy, r, r, 0, color)
  }

  ellipse(cx, cy, rx, ry, rotation, color) {
    const cos = Math.cos(rotation)
    const sin = Math.sin(rotation)
    this.fillIf((x, y) => {
      const dx = x - cx
      const dy = y - cy
      const u = dx * cos + dy * sin
      const v = -dx * sin + dy * cos
      return (u / rx) ** 2 + (v / ry) ** 2 <= 1
    }, color)
  }

  clearEllipse(cx, cy, rx, ry, rotation) {
    const cos = Math.cos(rotation)
    const sin = Math.sin(rotation)
    this.clearIf((x, y) => {
      const dx = x - cx
      const dy = y - cy
      const u = dx * cos + dy * sin
      const v = -dx * sin + dy * cos
      return (u / rx) ** 2 + (v / ry) ** 2 <= 1
    })
  }

  roundRect(x0, y0, w, h, r, color) {
    const x1 = x0 + w
    const y1 = y0 + h
    this.fillIf((x, y) => {
      if (x < x0 || x > x1 || y < y0 || y > y1) return false
      const cx = Math.min(Math.max(x, x0 + r), x1 - r)
      const cy = Math.min(Math.max(y, y0 + r), y1 - r)
      return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
    }, color)
  }

  /** 降采样到目标尺寸并返回 RGBA Buffer */
  resolve() {
    const S = this.ss
    const out = Buffer.alloc(this.size * this.size * 4)
    const n = S * S
    for (let y = 0; y < this.size; y += 1) {
      for (let x = 0; x < this.size; x += 1) {
        let r = 0
        let g = 0
        let b = 0
        let a = 0
        for (let dy = 0; dy < S; dy += 1) {
          for (let dx = 0; dx < S; dx += 1) {
            const i = ((y * S + dy) * this.w + (x * S + dx)) * 4
            const pa = this.buf[i + 3] / 255
            r += this.buf[i] * pa
            g += this.buf[i + 1] * pa
            b += this.buf[i + 2] * pa
            a += pa
          }
        }
        const o = (y * this.size + x) * 4
        if (a > 0) {
          out[o] = Math.round(r / a)
          out[o + 1] = Math.round(g / a)
          out[o + 2] = Math.round(b / a)
        }
        out[o + 3] = Math.round((a / n) * 255)
      }
    }
    return out
  }
}

/* -------------------------------------------------------------------------- */
/* 熊猫图形（占位）                                                             */
/* -------------------------------------------------------------------------- */

const INK = [26, 28, 33, 255]
const FUR = [250, 250, 252, 255]
const BAMBOO = [92, 178, 106, 255]
const BLUSH = [240, 160, 170, 255]

/**
 * 绘制熊猫头。
 * @param {number} size
 * @param {{bg?: number[]|null, mono?: number[]|null, badge?: number[]|null}} opts
 */
function drawPanda(size, opts = {}) {
  // 以 100x100 设计稿为基准，Canvas 负责缩放到目标尺寸
  const c = new Canvas(size, 4, 100)

  // 圆形底板
  if (opts.bg) c.circle(50, 50, 49, opts.bg)

  const ink = opts.mono ?? INK
  const fur = opts.mono ? opts.mono : FUR

  // 耳朵
  c.circle(24, 24, 15, ink)
  c.circle(76, 24, 15, ink)

  // 脸
  c.circle(50, 52, 36, fur)

  // 眼斑（倾斜椭圆）
  c.ellipse(35, 48, 12, 15, -0.32, ink)
  c.ellipse(65, 48, 12, 15, 0.32, ink)

  if (!opts.mono) {
    // 眼白高光
    c.circle(36, 46, 5, FUR)
    c.circle(64, 46, 5, FUR)
    c.circle(37, 45, 2.6, INK)
    c.circle(63, 45, 2.6, INK)
    // 腮红
    c.circle(27, 63, 5, BLUSH)
    c.circle(73, 63, 5, BLUSH)
  }

  // 鼻子 + 嘴
  c.ellipse(50, 64, 7, 5, 0, ink)
  if (!opts.mono) {
    // 竹叶（点缀，象征 TKS 的竹林意象）
    c.ellipse(80, 82, 12, 5, -0.6, BAMBOO)
    c.ellipse(87, 74, 9, 4, -1.1, BAMBOO)
  }

  // 未读角标（FR-CONN-9 / FR-DSK-1）
  if (opts.badge) {
    c.circle(78, 78, 20, [255, 255, 255, 235])
    c.circle(78, 78, 16, opts.badge)
  }

  return c.resolve()
}

/** 托盘用单色剪影（避免小尺寸下细节糊成一团）；眼睛做 destination-out 挖空 */
function drawTrayGlyph(size, color) {
  const c = new Canvas(size, 4, 100)
  c.circle(28, 26, 15, color)
  c.circle(72, 26, 15, color)
  c.circle(50, 54, 38, color)
  c.clearEllipse(36, 50, 9, 12, -0.3)
  c.clearEllipse(64, 50, 9, 12, 0.3)
  return c.resolve()
}

/* -------------------------------------------------------------------------- */
/* 输出                                                                        */
/* -------------------------------------------------------------------------- */

function writeIcon(name, size, rgba) {
  const file = join(ICON_DIR, name)
  writeFileSync(file, encodePng(size, size, rgba))
  return file
}

const SIZES = [16, 32, 48, 64, 128, 256, 512]

function main() {
  mkdirSync(ICON_DIR, { recursive: true })
  mkdirSync(APPICON_DIR, { recursive: true })
  const written = []

  // 应用图标：多尺寸（FR-UI-8）
  for (const size of SIZES) {
    const rgba = drawPanda(size, { bg: size >= 48 ? [244, 246, 250, 255] : null })
    written.push(writeIcon(`tks-${size}.png`, size, rgba))
    // 同时输出 electron-builder 约定的 `{size}x{size}.png`（仅应用图标）
    const appIcon = join(APPICON_DIR, `${size}x${size}.png`)
    writeFileSync(appIcon, encodePng(size, size, rgba))
    written.push(appIcon)
  }
  // 桌面/打包主图标
  const mainIcon = drawPanda(512, { bg: [244, 246, 250, 255] })
  written.push(writeIcon('icon.png', 512, mainIcon))
  writeFileSync(join(APPICON_DIR, 'icon.png'), encodePng(512, 512, mainIcon))

  // 托盘图标（在线 / 离线 / 未读），22px 与 44px（HiDPI）
  const trayVariants = {
    'tray-online': [58, 178, 106, 255],
    'tray-offline': [150, 156, 166, 255],
    'tray-unread': [232, 88, 96, 255]
  }
  for (const [name, color] of Object.entries(trayVariants)) {
    for (const size of [22, 44]) {
      const suffix = size === 22 ? '' : '@2x'
      written.push(writeIcon(`${name}${suffix}.png`, size, drawTrayGlyph(size, color)))
    }
  }

  // 等级徽章占位（FR-LV-2：配色按「视觉资产/熊猫图像.txt」，最终以 /level/config 驱动）
  const levelColors = {
    'lv1-newborn': [185, 189, 193],
    'lv2-curious': [143, 203, 107],
    'lv3-rookie': [55, 166, 92],
    'lv4-knight': [44, 90, 140],
    'lv5-master': [216, 163, 43],
    'lv6-elder': [139, 92, 214],
    'lv7-legend': [255, 138, 91]
  }
  for (const [name, color] of Object.entries(levelColors)) {
    for (const size of [64, 128]) {
      written.push(writeIcon(`badge-${name}-${size}.png`, size, drawPanda(size, { mono: color.concat([255]), bg: null })))
    }
  }

  // 互动物品占位图（FR-INT-9 / EDGE-L23：`iconUrl` 为空时回退 emoji，再回退内置占位图）
  const itemColors = {
    coffee: [140, 96, 62],
    noodles: [214, 148, 62],
    gamepad: [86, 110, 160],
    white_dragon: [226, 226, 230],
    energy_bar: [110, 76, 48]
  }
  for (const [name, color] of Object.entries(itemColors)) {
    for (const size of [64, 128]) {
      written.push(writeIcon(`item-${name}-${size}.png`, size, drawPanda(size, { mono: color.concat([255]) })))
    }
  }

  // XDG Icon Theme 索引
  const indexTheme = [
    '[Icon Theme]',
    'Name=TKS Desktop',
    'Comment=Taki Shiina desktop client icons',
    'Directories=16x16/apps,32x32/apps,48x48/apps,64x64/apps,128x128/apps,256x256/apps,512x512/apps',
    '',
    ...[16, 32, 48, 64, 128, 256, 512].flatMap((s) => [
      `[${s}x${s}/apps]`,
      'Size=' + s,
      'Type=Fixed',
      'Context=Applications',
      ''
    ])
  ].join('\n')
  writeFileSync(join(ICON_DIR, 'index.theme'), indexTheme)

  console.log(`已生成 ${written.length} 个占位图标 → ${ICON_DIR}`)
  console.log('提示：正式美术资源到位后，直接替换同名 PNG 即可，无需修改代码。')
}

main()
