/**
 * 互动菜单（FR-INT-1..13、§7.1a、FR-PROG-4、EDGE-L18/L22/L23）。
 *
 * 关键约束（评审会逐条对照）：
 * - FR-INT-1/2/3：右下角常驻「+」悬浮按钮 → **平铺列表**，顺序按服务端 `sortOrder`，
 *   不做分类与角标；展示图标 / 名称 / 所需积分
 * - FR-INT-4：置灰依据**只用服务端 `affordable`**，绝不用本地余额自算；
 *   悬浮提示「还差 N 积分」
 * - FR-INT-5：点击先做本地余额预校验（体验优化），最终以后端返回为准；
 *   本地通过但后端 40201 → 用后端 `balance` 覆盖本地缓存并提示
 * - FR-INT-6：回复复用聊天气泡组件（见 MessageBubble 的礼物标记），本组件不渲染回复
 * - FR-INT-7/13：等待期间展示 typing 态；**不做 3 秒硬性报错**；互动走 REST，
 *   不受「WS 未连接禁止发送」（FR-CHAT-4）拦截
 * - FR-INT-8：客户端等待上限 ≥60s（取 `PROTOCOL.INTERACTION_TIMEOUT_MS`），
 *   超时提示「立希好像走神了」并刷新余额（不做事后本地补偿）
 * - FR-INT-9 / EDGE-L23：`iconUrl` → `icon`（emoji）→ 内置占位图三级回退，
 *   图片加载失败也回退，**不得渲染破图或空图标**
 * - FR-INT-10：服务端不设每日上限，客户端不做任何频次拦截
 * - FR-INT-11：`requestId` 用 `crypto.randomUUID()`，**重试复用同一 id**
 * - §7.1a：输入框有文字时作为 `text` 附言；成功后清空输入框，失败保留；
 *   `mergedRequestIds` 用于把被摘走的「发送中」气泡批量置为已送达（EDGE-L22）
 * - FR-PROG-4：离线时整个菜单禁用
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { InteractionItem } from '@shared/protocol'
import type { InteractionSendResult } from '@shared/ipc'
import { PROTOCOL } from '@shared/levels'
import { useAppStore } from '../../store/app-store'
import { useChatStore } from '../../store/chat-store'
import { useProfileStore } from '../../store/profile-store'
import { describeError, useTranslation } from '../../i18n'
import { IconButton, Spinner } from '../../components/primitives'
import { IconClose, IconGift, IconPlus } from '../../components/Icons'

/** 等待中的文案按服务端 `chat.typing.stage` 区分（FR-CHAT-14 同源）。 */
function typingKeyOf(stage: 'vision' | 'generating' | 'interaction' | 'interaction_merge' | undefined): string {
  switch (stage) {
    case 'vision':
      return 'chat.typing.vision'
    case 'generating':
      return 'chat.typing.generating'
    case 'interaction':
      return 'chat.typing.interaction'
    case 'interaction_merge':
      return 'chat.typing.interaction_merge'
    default:
      return 'interaction.sending'
  }
}

export interface InteractionMenuProps {
  /** 输入框当前文字：非空时作为附言一起提交（§7.1a）。 */
  composerText: string
  /** 互动**成功**后清空输入框（失败必须保留文字）。 */
  onSent: () => void
}

export function InteractionMenu({ composerText, onSent }: InteractionMenuProps): JSX.Element {
  const { t } = useTranslation()
  const interaction = useProfileStore((s) => s.interaction)
  const offline = useProfileStore((s) => s.offline)
  const balance = useProfileStore((s) => s.balance)
  const balanceLoading = useProfileStore((s) => s.interactionLoading)
  const pending = useProfileStore((s) => s.interactionPending)
  const typing = useChatStore((s) => s.typing)
  const pushToast = useAppStore((s) => s.pushToast)

  const [open, setOpen] = useState(false)
  const [brokenIcons, setBrokenIcons] = useState<Record<string, boolean>>({})
  const wrapRef = useRef<HTMLDivElement | null>(null)
  /** FR-INT-11：幂等键缓存，重试复用同一 requestId。 */
  const requestRef = useRef<{ itemId: string; requestId: string } | null>(null)
  const activeRequestRef = useRef<string | null>(null)
  const timeoutRef = useRef<number | null>(null)

  /** FR-INT-2/3：严格按服务端 `sortOrder` 排列（不按价格、不做分类）。 */
  const items = useMemo(
    () => [...(interaction?.items ?? [])].sort((a, b) => a.sortOrder - b.sortOrder),
    [interaction]
  )

  // 打开菜单时刷新一次：`affordable` 必须来自服务端（FR-INT-4）
  useEffect(() => {
    if (!open) return
    void useProfileStore.getState().refreshInteraction()
  }, [open])

  // Esc 关闭；同时响应主进程广播的 `tks:escape`（events.ts 统一分发）
  useEffect(() => {
    if (!open) return
    const close = (): void => setOpen(false)
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') close()
    }
    const onPointerDown = (event: MouseEvent): void => {
      const target = event.target
      if (target instanceof Node && wrapRef.current && !wrapRef.current.contains(target)) close()
    }
    window.addEventListener('keydown', onKey)
    window.addEventListener('tks:escape', close)
    window.addEventListener('pointerdown', onPointerDown)
    return () => {
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('tks:escape', close)
      window.removeEventListener('pointerdown', onPointerDown)
    }
  }, [open])

  // 卸载时清理等待计时器
  useEffect(
    () => () => {
      if (timeoutRef.current !== null) window.clearTimeout(timeoutRef.current)
    },
    []
  )

  /** EDGE-L22：把被服务端「摘走」的消息批量置为已送达（主进程也会推，这里是渲染端兜底）。 */
  const markMergedDelivered = useCallback((requestIds: string[]): void => {
    if (requestIds.length === 0) return
    const chat = useChatStore.getState()
    const affected = chat.messages.filter((message) => requestIds.includes(message.messageId) && message.status === 'sending')
    if (affected.length === 0) return
    chat.upsert(affected.map((message) => ({ ...message, status: 'sent' as const })))
  }, [])

  const finishPending = useCallback((requestId: string): void => {
    if (timeoutRef.current !== null) {
      window.clearTimeout(timeoutRef.current)
      timeoutRef.current = null
    }
    if (activeRequestRef.current === requestId) {
      activeRequestRef.current = null
      useProfileStore.getState().setInteractionPending(null)
    }
  }, [])

  /** 失败码分支（§7.0 / §7.1a）。 */
  const handleFailureCode = useCallback(
    (code: number, result: InteractionSendResult): void => {
      const store = useProfileStore.getState()
      if (code === 40201) {
        // FR-INT-5：以后端 balance 覆盖本地缓存（主进程未回传 data 时退回一次余额刷新）
        if (result.data) store.applyBalance(result.data.balance)
        else void store.refreshBalance()
        pushToast({ level: 'warn', i18nKey: 'error.api.40201' })
        return
      }
      if (code === 40202) {
        pushToast({ level: 'warn', i18nKey: 'error.api.40202' })
        // 物品不存在或已下架 → 刷新菜单
        void store.refreshInteraction()
        return
      }
      if (code === 40204) {
        // 兜底文案 + 积分已退回提示 + 刷新余额（退款由服务端保证，客户端不做本地补偿）
        const fallback = result.data?.fallbackText
        pushToast({ level: 'warn', i18nKey: 'error.api.40204', text: fallback ?? t('error.api.40204') })
        pushToast({ level: 'info', i18nKey: 'interaction.refunded' })
        void store.refreshBalance()
        return
      }
      if (result.data) store.applyBalance(result.data.balance)
      // 其它业务码（40001 / 5000…）：有本地化文案用文案，否则退回通用「送礼失败」
      const key = `error.api.${code}`
      const localized = t(key)
      pushToast({ level: 'error', i18nKey: localized === key ? 'interaction.failed' : key })
    },
    [pushToast, t]
  )

  const send = useCallback(
    async (item: InteractionItem): Promise<void> => {
      const store = useProfileStore.getState()
      if (store.offline) {
        pushToast({ level: 'warn', i18nKey: 'interaction.disabled.offline' })
        return
      }
      if (store.interactionPending) return

      // FR-INT-5：本地余额预校验（体验优化），最终以后端返回为准
      if (store.balance < item.costPoints) {
        pushToast({ level: 'warn', i18nKey: 'error.api.40201' })
        void store.refreshBalance()
        return
      }

      // FR-INT-11：重试复用同一 requestId（同一物品才复用，换物品重新生成）
      const previous = requestRef.current
      const requestId = previous && previous.itemId === item.id ? previous.requestId : crypto.randomUUID()
      requestRef.current = { itemId: item.id, requestId }
      activeRequestRef.current = requestId

      const appendText = composerText.trim()
      store.setInteractionPending({ itemId: item.id, requestId, startedAt: Date.now() })

      // FR-INT-8：等待上限 ≥60s（§7.1a：25s 合并等待 + 15s AI 兜底，nginx 侧 60s）
      timeoutRef.current = window.setTimeout(() => {
        timeoutRef.current = null
        if (activeRequestRef.current !== requestId) return
        activeRequestRef.current = null
        useProfileStore.getState().setInteractionPending(null)
        pushToast({ level: 'warn', i18nKey: 'interaction.timeout' })
        void useProfileStore.getState().refreshBalance()
      }, PROTOCOL.INTERACTION_TIMEOUT_MS)

      try {
        const result = await window.tks.gamification.interactionSend(
          item.id,
          requestId,
          appendText.length > 0 ? appendText : undefined
        )
        // 已超时：结果按后端为准（余额已刷新），这里不再重复提示
        if (activeRequestRef.current !== requestId) return

        const code = result.code
        const succeeded = (code === 0 || code === null) && result.data !== null

        if (succeeded && result.data) {
          const data = result.data
          useProfileStore.getState().applyBalance(data.balance)
          // EDGE-L22：被摘走的「发送中」气泡批量置为已送达
          markMergedDelivered(result.mergedRequestIds)
          pushToast({
            level: 'success',
            i18nKey: 'interaction.success',
            params: { name: data.item?.name ?? item.name, cost: data.charged }
          })
          // §7.1a：成功后清空输入框
          requestRef.current = null
          onSent()
          setOpen(false)
          return
        }

        if (code !== null && code !== 0) {
          handleFailureCode(code, result)
          // §7.1a：失败时**保留**输入框文字，用户仍可正常发送
        } else {
          pushToast({ level: 'error', i18nKey: 'interaction.failed' })
        }
      } catch (err) {
        if (activeRequestRef.current !== requestId) return
        // 网络层错误（离线 / 超时 / 5xx）：走统一错误文案
        pushToast({ level: 'error', i18nKey: 'interaction.failed', text: describeError(err) })
      } finally {
        finishPending(requestId)
      }
    },
    [composerText, finishPending, handleFailureCode, markMergedDelivered, onSent, pushToast]
  )

  /** FR-INT-9 / EDGE-L23：`iconUrl` → emoji → 内置占位图（图片加载失败也回退）。 */
  const iconNode = (item: InteractionItem): JSX.Element => {
    if (item.iconUrl && !brokenIcons[item.id]) {
      return (
        <img
          src={item.iconUrl}
          alt=""
          draggable={false}
          onError={() => setBrokenIcons((previous) => ({ ...previous, [item.id]: true }))}
        />
      )
    }
    if (item.icon) return <span aria-hidden="true">{item.icon}</span>
    return (
      <span role="img" aria-label={t('interaction.placeholderIcon')}>
        <IconGift size={22} />
      </span>
    )
  }

  const waiting = pending !== null
  const menuDisabled = offline

  return (
    <div ref={wrapRef}>
      <button
        type="button"
        className="floating-add"
        aria-label={t('interaction.open')}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-disabled={menuDisabled || waiting}
        title={menuDisabled ? t('interaction.disabled.offline') : t('interaction.open')}
        disabled={menuDisabled || waiting}
        onClick={() => setOpen((value) => !value)}
      >
        <IconPlus size={22} />
      </button>

      {open ? (
        <div className="interaction-sheet glass" role="dialog" aria-label={t('interaction.title')}>
          <div className="interaction-head">
            <span className="band-title">{t('interaction.title')}</span>
            <span>{t('interaction.balance', { n: balance })}</span>
            <IconButton label={t('interaction.close')} onClick={() => setOpen(false)}>
              <IconClose size={16} />
            </IconButton>
          </div>

          {menuDisabled ? (
            <p className="muted" role="alert">
              {t('interaction.disabled.offline')}
            </p>
          ) : null}

          {composerText.trim().length > 0 ? <p className="muted">{t('interaction.titleWithText')}</p> : null}

          {waiting ? (
            <p className="muted" role="status" aria-live="polite" aria-label={t('interaction.pendingAria')}>
              <Spinner size={12} /> {t(typingKeyOf(typing.stage))}
            </p>
          ) : null}

          {items.length === 0 ? (
            <p className="muted">
              {balanceLoading ? t('common.loading') : t('interaction.empty')}
            </p>
          ) : (
            <div className="interaction-grid">
              {items.map((item) => {
                const shortfall = Math.max(item.costPoints - balance, 0)
                // FR-INT-4：置灰只依据服务端 `affordable`
                const unaffordable = !item.affordable
                const itemDisabled = unaffordable || waiting || menuDisabled
                return (
                  <button
                    key={item.id}
                    type="button"
                    className={['interaction-item', unaffordable ? 'is-disabled' : ''].filter(Boolean).join(' ')}
                    aria-disabled={itemDisabled}
                    aria-label={t('interaction.itemAria', { name: item.name, cost: item.costPoints })}
                    title={unaffordable ? t('interaction.needMore', { n: shortfall }) : t('interaction.itemAria', { name: item.name, cost: item.costPoints })}
                    onClick={() => {
                      if (itemDisabled) {
                        if (unaffordable) pushToast({ level: 'warn', i18nKey: 'interaction.needMore', params: { n: shortfall } })
                        return
                      }
                      void send(item)
                    }}
                  >
                    {/* 图标三级回退见 iconNode；图标本身对读屏静默（按钮已有 aria-label） */}
                    <span className="interaction-icon">{iconNode(item)}</span>
                    <span className="interaction-name">{item.name}</span>
                    <span className="interaction-cost">{t('interaction.cost', { n: item.costPoints })}</span>
                    {unaffordable ? (
                      <span className="interaction-need">{t('interaction.needMore', { n: shortfall })}</span>
                    ) : null}
                  </button>
                )
              })}
            </div>
          )}
        </div>
      ) : null}
    </div>
  )
}

export default InteractionMenu
