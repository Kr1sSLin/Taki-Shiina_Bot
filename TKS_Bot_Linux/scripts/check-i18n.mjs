#!/usr/bin/env node
/**
 * i18n 一致性检查（NFR-10 的机械保障）。
 *
 * 两个方向都查：
 *  ① **引用了但未定义** —— 运行时会直接显示成 key（例如界面上出现 `chat.send`），必须为 0
 *  ② **定义了但从未引用** —— 死文案，通常意味着两次改动重命名了一半
 *
 * 之所以做成脚本：PRD 反复警告「字段名写错不会报错，只会静默变成默认值」
 * （§7.0 FR-PROTO-1 / EDGE-L24）。文案 key 属于同一类风险，必须机械化守住。
 *
 * 用法：node scripts/check-i18n.mjs   （CI 中作为 `npm run check:i18n`）
 * 退出码：有「未定义」或重复 key 时为 1；仅有未使用 key 时为 0 但会列出。
 */

import { readFileSync } from 'node:fs'
import { readdirSync, statSync } from 'node:fs'
import { join, resolve } from 'node:path'

const ROOT = resolve(import.meta.dirname, '..')
const LOCALE = join(ROOT, 'src/shared/i18n/zh-CN.ts')

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name)
    const info = statSync(full)
    if (info.isDirectory()) walk(full, out)
    else if (/\.(ts|tsx)$/.test(name)) out.push(full)
  }
  return out
}

const localeSource = readFileSync(LOCALE, 'utf8')

// 定义侧：形如 `  'some.key': '...'`（含被注释掉的行要排除，但 body 里不含注释）
const defined = new Map()
for (const match of localeSource.matchAll(/^\s*'([^']+)':/gm)) {
  const key = match[1]
  defined.set(key, (defined.get(key) ?? 0) + 1)
}

const duplicates = [...defined.entries()].filter(([, n]) => n > 1).map(([k]) => k)

// 引用侧：扫描 src/ 下所有 ts/tsx 里出现的字符串字面量，检查是否命中已定义的 key
const files = walk(join(ROOT, 'src'))
const referenced = new Set()
let scanned = 0
for (const file of files) {
  // 语言包自身不算引用
  if (file === LOCALE) continue
  scanned += 1
  const text = readFileSync(file, 'utf8')
  for (const match of text.matchAll(/'([a-zA-Z][a-zA-Z0-9._-]*)'/g)) {
    const candidate = match[1]
    if (defined.has(candidate)) referenced.add(candidate)
  }
}

const definedKeys = [...defined.keys()]
const unused = definedKeys.filter((key) => !referenced.has(key))
const missing = [] // 反向：无法静态判定「某个 t('x') 的 x 未定义」而不做 AST 解析，这里用另一种近似

// 反向检查：把所有 `t('...')` / `i18nKey: '...'` 的字面量抽出来核对是否已定义。
// 未命中定义的候选若是「看起来像 i18n key」的（含 '.' 且首段是已知命名空间前缀），即报为缺失。
const NAMESPACES = new Set(definedKeys.map((k) => k.split('.')[0]))
for (const file of files) {
  if (file === LOCALE) continue
  const text = readFileSync(file, 'utf8')
  for (const match of text.matchAll(/(?:^|[^\w.'"])(t|translate)\(\s*'([^']+)'/g)) {
    const key = match[2]
    if (!defined.has(key)) missing.push({ key, file: file.replace(`${ROOT}/`, '') })
  }
  // i18nKey / i18nKey: 'x' 形式（含 pushToast({ i18nKey: 'x' })）
  for (const match of text.matchAll(/i18nKey:\s*'([^']+)'/g)) {
    const key = match[1]
    if (key !== '__literal__' && !defined.has(key)) missing.push({ key, file: file.replace(`${ROOT}/`, '') })
  }
  // errorI18nKey: 'x'
  for (const match of text.matchAll(/(?:errorI18nKey|reasonI18nKey):\s*'([^']+)'/g)) {
    const key = match[1]
    if (key !== 'null' && !defined.has(key)) missing.push({ key, file: file.replace(`${ROOT}/`, '') })
  }
}

/*
 * 动态拼接的 key 会让上面两个方向**同时**失效：
 *   - 「未定义」查不出来（运行时才拼出字符串）
 *   - 「未使用」会误报（静态看不到引用），从而误导别人把仍在用的 key 删掉
 *
 * 因此这里主动检出并报错，要求改回字面量 key（或在语言包里加显式映射表）。
 * 当前代码库中不存在此类用法；这条守卫是为了防止后续引入。
 */
const dynamic = []
for (const file of files) {
  if (file === LOCALE) continue
  const text = readFileSync(file, 'utf8')
  const rel = file.replace(`${ROOT}/`, '')
  for (const match of text.matchAll(/(?:^|[^\w.'"])(?:t|translate)\(\s*`([^`]*)`/g)) {
    dynamic.push({ snippet: `t(\`${match[1]}\`)`, file: rel })
  }
  for (const match of text.matchAll(/(?:i18nKey|errorI18nKey|reasonI18nKey):\s*`([^`]*)`/g)) {
    dynamic.push({ snippet: `i18nKey: \`${match[1]}\``, file: rel })
  }
}

void NAMESPACES

console.log(`\n扫描 ${scanned} 个源文件，语言包共 ${definedKeys.length} 个 key\n`)

let failed = false

if (duplicates.length > 0) {
  failed = true
  console.log(`\x1b[31m✗ 重复定义的 key（${duplicates.length}）：\x1b[0m`)
  for (const key of duplicates) console.log(`    ${key}`)
} else {
  console.log('\x1b[32m✓ 无重复 key\x1b[0m')
}

if (missing.length > 0) {
  failed = true
  console.log(`\n\x1b[31m✗ 引用了但未定义的 key（${missing.length}）—— 运行时会直接显示成 key：\x1b[0m`)
  for (const { key, file } of missing) console.log(`    ${key}   (${file})`)
} else {
  console.log('\x1b[32m✓ 所有引用的 key 均已定义\x1b[0m')
}

if (dynamic.length > 0) {
  failed = true
  console.log(`\n\x1b[31m✗ 检测到动态拼接的 i18n key（${dynamic.length}）—— 会让本检查双向失效：\x1b[0m`)
  for (const { snippet, file } of dynamic) console.log(`    ${snippet}   (${file})`)
  console.log('    请改回字面量 key，或在语言包里加显式映射表。')
} else {
  console.log('\x1b[32m✓ 无动态拼接的 key（静态检查结论可信）\x1b[0m')
}

if (unused.length > 0) {
  console.log(`\n\x1b[33m! 定义了但从未引用（${unused.length}）—— 多为重命名残留，建议清理：\x1b[0m`)
  for (const key of unused) console.log(`    ${key}`)
} else {
  console.log('\x1b[32m✓ 无未使用的 key\x1b[0m')
}

console.log()
process.exit(failed ? 1 : 0)
