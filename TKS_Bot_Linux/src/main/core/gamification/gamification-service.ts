/**
 * 积分 / 等级 / 互动 / 补签卡服务（§7 全部）。
 *
 * 一致性约束（§7.5）：
 * - FR-PROG-1：**后端为唯一数据源**，本地表仅作离线缓存与乐观展示。
 * - FR-PROG-2：客户端**不实现**任何积分获取规则（每日首次 +1 / 连续 3 天 +3 / 纪念日 +100），
 *   全部由服务端按业务时区（默认 UTC+8）判定。
 * - FR-PROG-3：所有「自然日」展示一律以服务端返回的日期为准（`lastValidDate` / `business_date` /
 *   `candidates[].date`），**不得**用本地日期重算。
 * - FR-PROG-6：事件驱动优先（5 个 WS 事件已正式交付）；轮询**仅**作为 WS 断开期间的兜底。
 *
 * 错误约定（§7.0）：业务失败为 HTTP 200 + `code != 0`，绝不能用 `response.ok` 判断成败
 * （该判定已在 `RestClient.unwrap` 内统一处理，此处只消费 `TksApiError.code`）。
 */

import { net } from 'electron'
import {
  IPC,
  type CelebrationEvent,
  type InteractionSendResult
} from '@shared/ipc'
import { PROTOCOL } from '@shared/levels'
import type {
  InteractionItemsData,
  LevelChangedPayload,
  LevelConfigData,
  LevelStatus,
  MakeupCardChangedPayload,
  MakeupCardHistoryData,
  MakeupCardSummary,
  MakeupCardUseData,
  MakeupCandidatesData,
  PointsChangedPayload,
  PointsHistoryData,
  PointsLedgerEntry,
  PointsOverviewData,
  PointsSnapshotPayload,
  StreakWarningPayload
} from '@shared/protocol'
import { TksApiError } from '@shared/errors'
import { bus } from '../../app/bus'
import { t } from '../../app/i18n'
import { createLogger } from '../../app/logger'
import {
  cacheLedgerEntries,
  cacheMakeupCards,
  getCachedLevelConfig,
  getCachedMakeupCandidates,
  getProgress,
  patchBalance,
  patchMakeupAvailable,
  replaceLevelConfig,
  replaceMakeupCandidates,
  saveProgressFromOverview
} from '../database/repositories/progress-repository'
import type { RestClient } from '../network/rest-client'
import type { NotificationService } from '../push/notification-service'

const log = createLogger('gamification')

/** FR-PROG-6：轮询仅作 WS 断开期间的兜底。 */
const FALLBACK_POLL_INTERVAL_MS = 5 * 60 * 1000

export interface GamificationDeps {
  rest: RestClient
  notify: NotificationService
  /** 把被防抖摘走的用户消息批量标记为已送达（EDGE-L22）。 */
  applyMergedRequestIds: (ids: string[]) => void
  /** 当前 WS 是否已连接（决定是否启用轮询兜底）。 */
  isWsConnected: () => boolean
  /** 跳转到补签日历（FR-LV-5a）。 */
  navigate: (route: string) => void
}

export class GamificationService {
  private deps: GamificationDeps
  private userId: string | null = null
  private pollTimer: NodeJS.Timeout | null = null
  private levelConfig: LevelConfigData | null = null
  /** FR-LV-6：本地预警态；用户当日完成一次对话后立即消除。 */
  private streakWarningActive = false

  constructor(deps: GamificationDeps) {
    this.deps = deps
  }

  setUserId(userId: string | null): void {
    this.userId = userId
  }

  /* ------------------------------------------------------------ 首屏聚合 */

  /** FR-PT-1 / FR-PROG-5：进入 Profile 优先一次请求拿全量。 */
  async overview(): Promise<PointsOverviewData> {
    const data = await this.deps.rest.request<PointsOverviewData>('points/overview')
    if (this.userId) saveProgressFromOverview(this.userId, data)
    // FR-LV-6：用户恢复了活跃（服务端不再下发预警），本地预警态清除
    if ((data.level?.gapDays ?? 0) === 0) this.streakWarningActive = false
    bus.send(IPC.evtPointsSnapshot, { balance: data.balance, timestamp: Date.now() })
    return data
  }

  async levelStatus(): Promise<LevelStatus> {
    const data = await this.deps.rest.request<LevelStatus>('level/status')
    // 与 overview 的 level 对象同构，顺手刷新缓存中的等级字段
    if (this.userId) {
      const cached = getProgress(this.userId)
      saveProgressFromOverview(this.userId, {
        balance: cached?.balance ?? 0,
        balanceUpdatedAt: cached?.balanceUpdatedAt ?? Date.now(),
        level: data,
        makeupCard: {
          available: data.availableMakeupCards ?? cached?.availableMakeupCards ?? 0,
          used: 0,
          totalGranted: 0,
          maxAvailable: 0,
          monthlyGrant: 0,
          lastGrantedMonth: null,
          currentMonthGranted: false,
          atLimit: false
        }
      })
    }
    return data
  }

  /** FR-LV-2 / EDGE-L24：等级阈值表（`levels[]` 为 snake_case，逐字段手写 DTO）。 */
  async levelConfigForce(): Promise<LevelConfigData> {
    const data = await this.deps.rest.request<LevelConfigData>('level/config')
    // EDGE-L24：对 `level_code` 为空做防御，绝不让空 key 进入渲染列表
    const sanitized: LevelConfigData = {
      levels: (data.levels ?? []).filter((lv) => !!lv?.level_code),
      defaultLevelCode: data.defaultLevelCode ?? 'NONE',
      defaultLevelName: data.defaultLevelName ?? '',
      makeupCardMax: data.makeupCardMax ?? 12,
      breakGapDays: data.breakGapDays ?? 5,
      warningGapDays: data.warningGapDays ?? [3, 4]
    }
    replaceLevelConfig(sanitized)
    this.levelConfig = sanitized
    return sanitized
  }

  async levelConfigCached(): Promise<LevelConfigData> {
    if (this.levelConfig) return this.levelConfig
    const cached = getCachedLevelConfig()
    if (cached) {
      this.levelConfig = cached
      return cached
    }
    return this.levelConfigForce()
  }

  /* -------------------------------------------------------------- 流水 */

  /** FR-PT-2：分页拉取，`items[]` 为 snake_case。 */
  async pointsHistory(opts: { page: number; pageSize: number; reasonCode?: string | null }): Promise<PointsHistoryData> {
    const data = await this.deps.rest.request<PointsHistoryData>('points/history', {
      query: {
        page: Math.max(1, opts.page),
        pageSize: Math.max(1, Math.min(100, opts.pageSize)),
        reasonCode: opts.reasonCode || undefined
      }
    })
    const normalized: PointsHistoryData = {
      items: data?.items ?? [],
      total: data?.total ?? 0,
      page: data?.page ?? opts.page,
      pageSize: data?.pageSize ?? opts.pageSize,
      hasMore: !!data?.hasMore
    }
    cacheLedgerEntries(normalized.items, normalized.page)
    return normalized
  }

  async balance(): Promise<{ balance: number; updatedAt: number }> {
    const data = await this.deps.rest.request<{ balance: number; updatedAt: number }>('points/balance')
    if (this.userId) patchBalance(this.userId, data?.balance ?? 0, data?.updatedAt ?? Date.now())
    return { balance: data?.balance ?? 0, updatedAt: data?.updatedAt ?? Date.now() }
  }

  /* ---------------------------------------------------------- 互动礼物 */

  /** FR-INT-2 / FR-INT-4：菜单按服务端顺序返回，置灰依据**服务端** `affordable`。 */
  async interactionItems(): Promise<InteractionItemsData> {
    const data = await this.deps.rest.request<InteractionItemsData>('interaction/items')
    const normalized: InteractionItemsData = {
      balance: data?.balance ?? 0,
      dailyLimit: data?.dailyLimit ?? null,
      items: (data?.items ?? []).filter((item) => !!item?.id).sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))
    }
    if (this.userId) patchBalance(this.userId, normalized.balance)
    return normalized
  }

  /**
   * FR-INT-5/8/11/13、§7.1a、EDGE-L22。
   *
   * - **互联网可用即可发送**，不受 FR-CHAT-4 的 WS 未连接拦截（FR-INT-13），
   *   但 FR-PROG-4 要求断网时整体禁用，故此处按 `net.isOnline()` 判定。
   * - **HTTP 超时必须 > 60s**（最长 ≈25s 合并等待 + 15s AI 兜底，nginx `proxy_read_timeout=60s`）。
   * - **重试必须复用同一 `requestId`**（幂等键），否则会重复扣分。
   */
  async interactionSend(itemId: string, requestId: string, text?: string): Promise<InteractionSendResult> {
    if (!net.isOnline()) {
      throw new TksApiError('offline', { code: null, httpStatus: null })
    }

    const body: { itemId: string; requestId: string; text?: string } = { itemId, requestId }
    const trimmed = (text ?? '').trim()
    if (trimmed) body.text = trimmed

    try {
      const data = await this.deps.rest.request<{
        success: boolean
        itemId: string
        requestId: string
        balance: number
        charged: number
        refunded: boolean
        duplicate: boolean
        item: { id: string; name: string; icon: string; costPoints: number }
        reply?: string
        messageId?: string
        timerInstruction?: { target: string; text: string } | null
        mergedCount?: number
        mergedRequestIds?: string[]
        fallbackText?: string
      }>('interaction/send', {
        method: 'POST',
        body,
        // FR-INT-8：必须 > 60s
        timeoutMs: PROTOCOL.INTERACTION_TIMEOUT_MS
      })

      if (this.userId) patchBalance(this.userId, data?.balance ?? 0)

      // §7.1a / EDGE-L22：把被摘走的「发送中」气泡批量标记为已送达
      const mergedRequestIds = data?.mergedRequestIds ?? []
      if (mergedRequestIds.length) this.deps.applyMergedRequestIds(mergedRequestIds)

      log.info('互动发送成功', {
        itemId,
        requestId,
        balance: data?.balance,
        charged: data?.charged,
        duplicate: data?.duplicate,
        mergedCount: data?.mergedCount ?? 0
      })

      return { data: data as never, code: 0, mergedRequestIds }
    } catch (err) {
      const code = err instanceof TksApiError ? err.code : null

      // 业务失败（HTTP 200 + code != 0）：仍要处理余额与退款语义
      if (code === 40201 || code === 40202 || code === 40204) {
        const detail = (err as TksApiError).message
        log.warn('互动业务失败', { itemId, requestId, code, detail })
        // 40204：积分已由服务端退回 → 刷新余额；客户端**不做**本地补偿（EDGE-L18）
        if (code === 40204) {
          void this.balance().catch(() => undefined)
        }
        return { data: null, code, mergedRequestIds: [] }
      }
      throw err
    }
  }

  /* ------------------------------------------------------------ 补签卡 */

  async makeupCard(): Promise<MakeupCardSummary> {
    const data = await this.deps.rest.request<MakeupCardSummary>('points/makeup-card')
    const normalized: MakeupCardSummary = {
      available: data?.available ?? 0,
      used: data?.used ?? 0,
      totalGranted: data?.totalGranted ?? 0,
      maxAvailable: data?.maxAvailable ?? 12,
      monthlyGrant: data?.monthlyGrant ?? 1,
      lastGrantedMonth: data?.lastGrantedMonth ?? null,
      currentMonthGranted: !!data?.currentMonthGranted,
      atLimit: !!data?.atLimit
    }
    if (this.userId) patchMakeupAvailable(this.userId, normalized.available)
    return normalized
  }

  /** FR-MC-2：补签日历的**唯一**数据源，客户端不得用本地日期推算缺口（FR-PROG-3）。 */
  async makeupCandidates(limit = 120): Promise<MakeupCandidatesData> {
    const data = await this.deps.rest.request<MakeupCandidatesData>('points/makeup-card/candidates', {
      query: { limit }
    })
    const normalized: MakeupCandidatesData = {
      items: (data?.items ?? []).filter((it) => !!it?.date),
      total: data?.total ?? 0,
      firstActivityDate: data?.firstActivityDate ?? null,
      available: data?.available ?? 0
    }
    replaceMakeupCandidates(normalized.items)
    if (this.userId) patchMakeupAvailable(this.userId, normalized.available)
    return normalized
  }

  /** FR-MC-3/5/6：**必须**由用户主动点击触发；失败不消耗卡片。 */
  async makeupUse(targetDate: string): Promise<{ ok: boolean; code: number | null; data: MakeupCardUseData | null }> {
    try {
      const data = await this.deps.rest.request<MakeupCardUseData>('points/makeup-card/use', {
        method: 'POST',
        body: { targetDate }
      })
      if (this.userId) {
        patchMakeupAvailable(this.userId, data?.availableCards ?? 0)
      }
      // FR-MC-5：依据返回的 `level.changeType` 决定反馈强度
      if (data?.level) {
        this.handleLevelChanged({
          levelCode: data.level.levelCode,
          levelName: data.level.levelName,
          prevLevelCode: data.level.prevLevelCode,
          continuousDays: data.level.continuousDays,
          changeType: data.level.changeType ?? 'RESTORE',
          changeSource: data.level.changeSource ?? 'MAKEUP_CARD',
          highestLevelCode: data.level.highestLevelCode,
          gapDays: data.level.gapDays,
          breakDeadlineDate: data.level.breakDeadlineDate,
          nextLevelCode: data.level.nextLevelCode,
          nextLevelName: data.level.nextLevelName,
          daysToNextLevel: data.level.daysToNextLevel,
          timestamp: Date.now()
        })
      }
      // 刷新日历与总览
      void this.overview().catch(() => undefined)
      void this.makeupCard().catch(() => undefined)
      void this.makeupCandidates().catch(() => undefined)
      log.info('补签成功', { targetDate, availableCards: data?.availableCards })
      return { ok: true, code: 0, data }
    } catch (err) {
      const code = err instanceof TksApiError ? err.code : null
      // FR-MC-6：40205 / 40206 / 40207 → 刷新余额与日历，失败不消耗卡片
      if (code === 40205 || code === 40206 || code === 40207) {
        log.warn('补签失败', { targetDate, code })
        void this.makeupCard().catch(() => undefined)
        void this.makeupCandidates().catch(() => undefined)
        return { ok: false, code, data: null }
      }
      throw err
    }
  }

  async makeupHistory(opts: { page: number; pageSize: number }): Promise<MakeupCardHistoryData> {
    const data = await this.deps.rest.request<MakeupCardHistoryData>('points/makeup-card/history', {
      query: { page: Math.max(1, opts.page), pageSize: Math.max(1, Math.min(100, opts.pageSize)) }
    })
    const normalized: MakeupCardHistoryData = {
      items: data?.items ?? [],
      total: data?.total ?? 0,
      page: data?.page ?? opts.page,
      pageSize: data?.pageSize ?? opts.pageSize,
      hasMore: !!data?.hasMore
    }
    cacheMakeupCards(normalized.items)
    return normalized
  }

  /** 离线缓存读取（FR-PROG-4）。 */
  cachedProgress() {
    return this.userId ? getProgress(this.userId) : null
  }

  cachedCandidates() {
    return getCachedMakeupCandidates()
  }

  /* ------------------------------------------------- WS 事件（5 个） */

  /** `points.snapshot`：登录/重连后覆盖本地余额缓存（§11.1 客户端建议）。 */
  handlePointsSnapshot(payload: PointsSnapshotPayload): void {
    if (!this.userId) this.userId = payload.userId ?? null
    if (this.userId) patchBalance(this.userId, payload.balance, payload.timestamp)
    bus.send(IPC.evtPointsSnapshot, { balance: payload.balance, timestamp: payload.timestamp })
    log.debug('余额已同步（points.snapshot）', { balance: payload.balance })
  }

  /** `points.changed`：FR-PT-4 同日多条规则各自独立推送，客户端逐条更新余额。 */
  handlePointsChanged(payload: PointsChangedPayload): void {
    // 优先用 `balanceAfter` / `balance` 直接覆盖缓存（FR-PT-6）
    const balance = typeof payload.balanceAfter === 'number' ? payload.balanceAfter : payload.balance
    if (this.userId && typeof balance === 'number') patchBalance(this.userId, balance, payload.timestamp)

    const entry: PointsLedgerEntry = {
      id: payload.ledgerId,
      user_id: this.userId ?? '',
      change_amount: payload.changeAmount,
      reason_code: payload.reasonCode,
      balance_after: balance,
      related_item_id: payload.relatedItemId,
      idempotency_key: '',
      created_at: payload.timestamp,
      business_date: payload.businessDate
    }
    cacheLedgerEntries([entry], 1)
    bus.send(IPC.evtPointsChanged, { balance, entries: [entry], payload })
    log.info('积分变动', {
      reason: payload.reasonCode,
      change: payload.changeAmount,
      balanceAfter: balance
    })
  }

  /**
   * `level.changed`：`changeType` 三态反馈强度必须区分（FR-LV-4/5/5a）。
   * FR-LV-7：服务端已广播给该账号**全部**在线设备，本端不做「仅最近活跃设备」过滤。
   */
  handleLevelChanged(payload: LevelChangedPayload): void {
    const changeType = String(payload.changeType ?? '')
    const levelName = payload.levelName || t('profile.level.none')

    if (changeType === 'UPGRADE') {
      // FR-LV-4：首次达成 → 弹窗 + 动画 + 桌面通知三重反馈
      this.deps.notify.notify({
        kind: 'progress',
        title: t('notify.progress.levelUp.title'),
        body: t('notify.progress.levelUp.body', { level: levelName, days: payload.continuousDays }),
        urgency: 'normal',
        route: '/profile'
      })
      this.emitCelebration({
        kind: 'level_upgrade',
        payload: { ...payload, levelName }
      })
    } else if (changeType === 'RESTORE') {
      // FR-LV-5：曾经达成过（多为补签追溯挽回）→ 明显更克制的反馈：轻量 toast + 普通通知，
      //         不播放升级动画、不弹庆祝弹窗
      this.deps.notify.notify({
        kind: 'progress',
        title: t('notify.progress.levelRestore.title'),
        body: t('notify.progress.levelRestore.body', { level: levelName })
      })
      this.emitCelebration({ kind: 'level_restore', payload: { ...payload, levelName } })
    } else if (changeType === 'RESET') {
      // FR-LV-5a：断签回落 → 提示并引导去补签卡日历
      // FR-LV-8：必须明确提示积分余额未受影响
      this.deps.notify.notify({
        kind: 'progress',
        title: t('notify.progress.levelReset.title'),
        body: t('notify.progress.levelReset.body'),
        urgency: 'normal',
        route: '/profile/makeup'
      })
      this.emitCelebration({ kind: 'level_reset', payload: { ...payload, levelName } })
    } else {
      log.debug('等级事件（未识别的 changeType，忽略动画）', { changeType })
    }

    bus.send(IPC.evtLevelChanged, { kind: `level_${changeType.toLowerCase()}`, payload })
    log.info('等级变化', {
      changeType,
      levelCode: payload.levelCode,
      continuousDays: payload.continuousDays,
      changeSource: payload.changeSource
    })
  }

  /** `streak.warning`：FR-LV-6 断签预警。 */
  handleStreakWarning(payload: StreakWarningPayload): void {
    this.streakWarningActive = true
    this.deps.notify.notify({
      kind: 'progress',
      title: t('notify.progress.streakWarning.title'),
      body: t('notify.progress.streakWarning.body', {
        gapDays: payload.gapDays,
        remainingDays: payload.remainingDays
      }),
      urgency: 'critical',
      route: '/profile'
    })
    this.emitCelebration({ kind: 'streak_warning', payload: { ...payload } })
    bus.send(IPC.evtStreakWarning, { kind: 'streak_warning', payload })
    log.warn('断签预警', {
      gapDays: payload.gapDays,
      remainingDays: payload.remainingDays,
      deadlineDate: payload.deadlineDate
    })
  }

  /** `makeup_card.changed`：FR-MC-8 实时同步，无需轮询。 */
  handleMakeupCardChanged(payload: MakeupCardChangedPayload): void {
    if (this.userId) patchMakeupAvailable(this.userId, payload.available)
    bus.send(IPC.evtMakeupCardChanged, { available: payload.available, reason: payload.reason })

    if (payload.reason === 'MONTHLY_GRANT') {
      this.deps.notify.notify({
        kind: 'progress',
        title: t('notify.progress.makeupGranted.title'),
        body: t('notify.progress.makeupGranted.body', { available: payload.available }),
        route: '/profile/makeup'
      })
      this.emitCelebration({ kind: 'makeup_granted', payload: { ...payload } })
    } else if (payload.reason === 'USED') {
      this.emitCelebration({ kind: 'makeup_used', payload: { ...payload } })
    }
    log.info('补签卡变动', { reason: payload.reason, available: payload.available })
  }

  /** FR-LV-6：用户当日完成一次对话后本地立即消除预警态。 */
  clearStreakWarning(): void {
    if (!this.streakWarningActive) return
    this.streakWarningActive = false
    bus.send(IPC.evtStreakWarning, { cleared: true })
  }

  isStreakWarningActive(): boolean {
    return this.streakWarningActive
  }

  /* ------------------------------------------------ 登录 / 重连同步 */

  /**
   * FR-PT-5：登录成功与 WS 重连成功后，以后端数据**同步覆盖**本地积分/等级缓存。
   */
  async refreshAll(): Promise<void> {
    try {
      await this.overview()
      await this.makeupCard()
      await this.makeupCandidates()
      log.info('积分/等级缓存已刷新')
    } catch (err) {
      log.warn('积分/等级刷新失败', { error: String(err) })
    }
  }

  /* ------------------------------------------------ 轮询兜底（FR-PROG-6） */

  /**
   * 轮询**仅**在 WS 断开期间启用（FR-PROG-6）。
   * FR-PROG-6 明确禁止把「每 5 分钟轮询」作为主路径。
   */
  startFallbackPolling(): void {
    if (this.pollTimer) return
    log.info('WS 断开，启用积分轮询兜底（5 分钟）')
    this.pollTimer = setInterval(() => {
      if (this.deps.isWsConnected()) {
        this.stopFallbackPolling()
        return
      }
      void this.balance().catch(() => undefined)
      void this.makeupCard().catch(() => undefined)
    }, FALLBACK_POLL_INTERVAL_MS)
    this.pollTimer.unref?.()
  }

  stopFallbackPolling(): void {
    if (!this.pollTimer) return
    clearInterval(this.pollTimer)
    this.pollTimer = null
    log.info('WS 已恢复，停止积分轮询兜底')
    // 恢复事件驱动：立即用真实数据覆盖一次
    void this.refreshAll()
  }

  private emitCelebration(event: CelebrationEvent): void {
    bus.send(IPC.evtCelebration, event)
  }

  dispose(): void {
    this.stopFallbackPolling()
  }
}
