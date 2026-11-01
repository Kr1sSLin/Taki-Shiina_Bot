// Import the picker subset of Twemoji 14.0.2 for both desktop clients.
import { readFile, mkdir, writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { resolve, dirname } from 'node:path'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const windows = resolve(root, 'TKS_Bot_Windows/TKSDesktop/Resources/Assets/Emoji')
const linux = resolve(root, 'TKS_Bot_Linux/src/renderer/public/emoji')
const sources = await Promise.all([
  readFile(resolve(root, 'TKS_Bot_Windows/TKSDesktop/ViewModels/EmojiGroup.cs'), 'utf8'),
  readFile(resolve(root, 'TKS_Bot_Linux/src/renderer/src/features/chat/EmojiPanel.tsx'), 'utf8')
])
const glyphs = new Set(sources.flatMap(source =>
  [...source.matchAll(/["']([^"'\r\n]+)["']/g)]
    .map(match => match[1]).filter(value => /\p{Extended_Pictographic}/u.test(value))))
const codes = [...new Set([...glyphs].map(glyph => [...glyph]
  .filter(char => char.codePointAt(0) !== 0xfe0f)
  .map(char => char.codePointAt(0).toString(16)).join('-')))]
await Promise.all([mkdir(windows, { recursive: true }), mkdir(linux, { recursive: true })])
const base = 'https://raw.githubusercontent.com/twitter/twemoji/v14.0.2'
async function download(path) {
  const response = await fetch(`${base}/${path}`)
  if (!response.ok) throw new Error(`${path}: ${response.status}`)
  return Buffer.from(await response.arrayBuffer())
}
let next = 0
await Promise.all(Array.from({ length: 8 }, async () => {
  while (next < codes.length) {
    const code = codes[next++]
    const bytes = await download(`assets/72x72/${code}.png`)
    if (!bytes.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10])))
      throw new Error(`Invalid PNG: ${code}`)
    await Promise.all([windows, linux].map(dir => writeFile(resolve(dir, `${code}.png`), bytes)))
  }
}))
const license = await download('LICENSE-GRAPHICS')
const attribution = 'Twemoji 14.0.2 graphics by Twitter, Inc. and other contributors.\n'
  + 'Source: https://github.com/twitter/twemoji/tree/v14.0.2/assets/72x72\n'
  + 'Licensed under CC BY 4.0: https://creativecommons.org/licenses/by/4.0/\n'
  + 'Original artwork is unchanged; displayed at reduced size.\n\n'
await Promise.all([windows, linux].map(dir => writeFile(resolve(dir, 'LICENSE.txt'),
  Buffer.concat([Buffer.from(attribution), license]))))
console.log(`Imported ${codes.length} emoji PNGs into each client.`)
