/**
 * Emoji 选择面板（FR-UI-5）。
 *
 * ⚠️ 关键点：面板**占据底部空间并把聊天内容推上去**，而不是浮层遮盖
 * （Android `EmojiPickerPanel.kt` 的行为）。因此本组件必须渲染在 `.composer` 内部
 * 的文档流里，不能使用绝对定位。
 *
 * 分类平铺（不做 Tab 切换），emoji 字符本身即按钮文案，
 * 分类标题一律走 i18n（NFR-10）。
 */

import { Fragment } from 'react'
import { useTranslation } from '../../i18n'

export interface EmojiCategory {
  key: string
  labelKey: string
  emojis: string[]
}

export const EMOJI_CATEGORIES: EmojiCategory[] = [
  {
    key: 'smile',
    labelKey: 'chat.emoji.category.smile',
    emojis: [
      '😀', '😄', '😁', '😆', '😅', '😂', '🙂', '🙃', '😉', '😊', '😌', '😍',
      '🥰', '😘', '😜', '🤪', '🤗', '🤔', '🤨', '😐', '😑', '😴', '🥱', '😪',
      '😢', '😭', '😤', '😠', '🤯', '😱', '🥺', '😳', '🤢', '😷', '🤒', '🥳',
      '😎', '🤓', '😇', '🙄'
    ]
  },
  {
    key: 'gesture',
    labelKey: 'chat.emoji.category.gesture',
    emojis: ['👍', '👎', '👌', '✌️', '🤞', '🤟', '🤘', '🤙', '👏', '🙌', '🙏', '💪', '👋', '🤝', '🖐️', '✋']
  },
  {
    key: 'heart',
    labelKey: 'chat.emoji.category.heart',
    emojis: [
      '❤️', '🧡', '💛', '💚', '💙', '💜', '🖤', '🤍', '💔', '💕', '💞', '💓',
      '💗', '💖', '💘', '💝', '✨', '⭐', '🌟', '💫', '🔥', '🌙'
    ]
  },
  {
    key: 'animal',
    labelKey: 'chat.emoji.category.animal',
    emojis: ['🐼', '🐱', '🐶', '🐰', '🦊', '🐻', '🐨', '🐯', '🦁', '🐮', '🐷', '🐸', '🐵', '🐔', '🐧', '🦄', '🐴', '🐢', '🐳']
  },
  {
    key: 'food',
    labelKey: 'chat.emoji.category.food',
    emojis: ['☕', '🍜', '🍫', '🎮', '🍰', '🍕', '🍔', '🍟', '🍣', '🍙', '🍎', '🍓', '🍇', '🍉', '🍵', '🥤', '🧋']
  },
  {
    key: 'object',
    labelKey: 'chat.emoji.category.object',
    emojis: ['🎁', '🎂', '🎈', '🎉', '🎊', '🌸', '🌷', '🌻', '🌹', '🍀', '☀️', '☁️', '⚡', '🌊', '❄️', '⛄', '🎵', '🎧', '📚', '💤']
  }
]

export interface EmojiPanelProps {
  /** 选中一个 emoji（由输入框插入到光标处）。 */
  onPick: (emoji: string) => void
}

export function EmojiPanel({ onPick }: EmojiPanelProps): JSX.Element {
  const { t } = useTranslation()

  return (
    <div className="emoji-panel" role="group" aria-label={t('chat.emoji.panel')}>
      {EMOJI_CATEGORIES.map((category) => (
        <Fragment key={category.key}>
          <span className="emoji-category-title" id={`emoji_cat_${category.key}`}>
            {t(category.labelKey)}
          </span>
          <div className="emoji-grid" role="group" aria-labelledby={`emoji_cat_${category.key}`}>
            {category.emojis.map((emoji) => (
              <button
                key={`${category.key}_${emoji}`}
                type="button"
                className="emoji-btn"
                aria-label={emoji}
                title={emoji}
                onClick={() => onPick(emoji)}
              >
                {emoji}
              </button>
            ))}
          </div>
        </Fragment>
      ))}
    </div>
  )
}

export default EmojiPanel
