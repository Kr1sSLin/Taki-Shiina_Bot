/**
 * 积分 / 等级 / 补签卡 状态（§7）。
 *
 * FR-PROG-1：后端为唯一数据源。本 store 只是「服务端数据的渲染端镜像 + 离线缓存回显」，
 * 绝不在此处实现任何积分规则（FR-PROG-2）。
 */

import { create } from 'zustand'
import type {
  InteractionItemsData,
  LevelConfigData,
  MakeupCardSummary,
  MakeupCandidatesData,
  PointsLedgerEntry,
  PointsOverviewData,
  UserProgressCache
} from '@shared/protocol'
import { normalizeLevels, resolveLevel, type ResolvedLevel } from '../lib/level'

const LEDGER_PAGE_SIZE = 20

export interface ProfileState {
  loaded: boolean
  /** 服务端数据不可用时为 true，UI 需标注「离线数据」（FR-PROG-4）。 */
  offline: boolean
  overview: PointsOverviewData | null
  cached: UserProgressCache | null
  levelConfig: LevelConfigData | null
  levels: ResolvedLevel[]
  ledger: PointsLedgerEntry[]
  ledgerPage: number
  ledgerHasMore: boolean
  ledgerTotal: number
  ledgerReasonFilter: string | null
  ledgerLoading: boolean
  makeupCard: MakeupCardSummary | null
  candidates: MakeupCandidatesData | null
  interaction: InteractionItemsData | null
  interactionLoading: boolean
  /** FR-INT-8：互动请求进行中（等待上限 ≥60s）。 */
  interactionPending: { itemId: string; requestId: string; startedAt: number } | null
  balance: number

  refreshAll: () => Promise<void>
  refreshOverview: () => Promise<void>
  refreshLevelConfig: () => Promise<void>
  loadLedger: (opts: { reset?: boolean; reasonCode?: string | null }) => Promise<void>
  refreshBalance: () => Promise<void>
  refreshInteraction: () => Promise<void>
  refreshMakeup: () => Promise<void>
  applyPointsChanged: (balance: number, entries: PointsLedgerEntry[]) => void
  applyBalance: (balance: number) => void
  applyMakeupAvailable: (available: number) => void
  setInteractionPending: (pending: ProfileState['interactionPending']) => void
  reset: () => void
}

export const useProfileStore = create<ProfileState>()((set, get) => ({
  loaded: false,
  offline: false,
  overview: null,
  cached: null,
  levelConfig: null,
  levels: [],
  ledger: [],
  ledgerPage: 0,
  ledgerHasMore: true,
  ledgerTotal: 0,
  ledgerReasonFilter: null,
  ledgerLoading: false,
  makeupCard: null,
  candidates: null,
  interaction: null,
  interactionLoading: false,
  interactionPending: null,
  balance: 0,

  /**
   * FR-PT-5 / FR-PROG-5：进入 Profile 优先 `GET /points/overview` 一次取回余额+等级+补签卡。
   * 失败时回退本地缓存并标注离线（FR-PROG-4）。
   */
  async refreshAll() {
    const [overviewResult, configResult, makeupResult, candidatesResult] = await Promise.allSettled([
      window.tks.gamification.overview(),
      window.tks.gamification.levelConfig(),
      window.tks.gamification.makeupCard(),
      window.tks.gamification.makeupCandidates(120)
    ])

    const overview = overviewResult.status === 'fulfilled' ? overviewResult.value : null
    const levelConfig = configResult.status === 'fulfilled' ? configResult.value : null
    const makeupCard = makeupResult.status === 'fulfilled' ? makeupResult.value : null
    const candidates = candidatesResult.status === 'fulfilled' ? candidatesResult.value : null

    const anyFailed =
      overviewResult.status === 'rejected' ||
      configResult.status === 'rejected' ||
      makeupResult.status === 'rejected' ||
      candidatesResult.status === 'rejected'

    const cached = anyFailed ? await window.tks.gamification.cachedProgress().catch(() => null) : null

    // FR-LV-2：等级视觉由服务端 `/level/config` 驱动，本地常量只提供配色与兜底
    const normalized = normalizeLevels(levelConfig)
    set({
      loaded: true,
      offline: anyFailed && overview === null,
      overview,
      levelConfig,
      levels: normalized.map((lv) => resolveLevel(lv.level_code, lv.level_name, normalized)),
      makeupCard,
      candidates,
      cached,
      balance: overview?.balance ?? cached?.balance ?? get().balance
    })
  },

  async refreshOverview() {
    try {
      const overview = await window.tks.gamification.overview()
      set({ overview, offline: false, balance: overview.balance })
    } catch {
      const cached = await window.tks.gamification.cachedProgress().catch(() => null)
      set({ offline: true, cached })
    }
  },

  /** FR-LV-2：等级阈值由服务端动态驱动；此处只做排序与防御性清洗（EDGE-L24）。 */
  async refreshLevelConfig() {
    try {
      const levelConfig = await window.tks.gamification.levelConfig()
      const normalized = normalizeLevels(levelConfig)
      set({
        levelConfig,
        levels: normalized.map((lv) => resolveLevel(lv.level_code, lv.level_name, normalized))
      })
    } catch {
      /* 保留缓存 */
    }
  },

  /** FR-PT-2：分页拉取，支持按 `reasonCode` 过滤。 */
  async loadLedger(opts) {
    const reset = opts.reset === true
    const reasonCode = opts.reasonCode !== undefined ? opts.reasonCode : get().ledgerReasonFilter
    const page = reset ? 1 : get().ledgerPage + 1

    set({ ledgerLoading: true })
    try {
      const data = await window.tks.gamification.pointsHistory({ page, pageSize: LEDGER_PAGE_SIZE, reasonCode })
      const merged = reset
        ? data.items
        : [...get().ledger, ...data.items.filter((it) => !get().ledger.some((e) => e.id === it.id))]
      set({
        ledger: merged,
        ledgerPage: page,
        ledgerHasMore: data.hasMore,
        ledgerTotal: data.total,
        ledgerReasonFilter: reasonCode ?? null
      })
    } catch {
      set({ ledgerHasMore: false })
    } finally {
      set({ ledgerLoading: false })
    }
  },

  async refreshBalance() {
    try {
      const data = await window.tks.gamification.balance()
      set({ balance: data.balance })
    } catch {
      /* 离线时保留缓存余额 */
    }
  },

  /** FR-INT-2 / FR-INT-4：互动菜单；置灰依据**服务端** `affordable`，不用本地余额自算。 */
  async refreshInteraction() {
    set({ interactionLoading: true })
    try {
      const interaction = await window.tks.gamification.interactionItems()
      set({ interaction, balance: interaction.balance, offline: false })
    } catch {
      set({ offline: true })
    } finally {
      set({ interactionLoading: false })
    }
  },

  async refreshMakeup() {
    const [makeupCard, candidates] = await Promise.allSettled([
      window.tks.gamification.makeupCard(),
      window.tks.gamification.makeupCandidates(120)
    ])
    if (makeupCard.status === 'fulfilled') set({ makeupCard: makeupCard.value })
    if (candidates.status === 'fulfilled') set({ candidates: candidates.value })
  },

  /** `points.changed` / `points.snapshot`：优先直接覆盖余额（FR-PT-6）。 */
  applyPointsChanged(balance, entries) {
    const ledger = [...entries, ...get().ledger.filter((e) => !entries.some((n) => n.id === e.id))]
    set({ balance, ledger, ledgerTotal: get().ledgerTotal + entries.length })
  },

  applyBalance(balance) {
    set({ balance })
  },

  applyMakeupAvailable(available) {
    const current = get().makeupCard
    const candidates = get().candidates
    set({
      makeupCard: current ? { ...current, available } : current,
      candidates: candidates ? { ...candidates, available } : null
    })
  },

  setInteractionPending(interactionPending) {
    set({ interactionPending })
  },

  reset() {
    set({
      loaded: false,
      offline: false,
      overview: null,
      cached: null,
      ledger: [],
      ledgerPage: 0,
      ledgerHasMore: true,
      ledgerTotal: 0,
      makeupCard: null,
      candidates: null,
      interaction: null,
      interactionPending: null,
      balance: 0
    })
  }
}))

export { LEDGER_PAGE_SIZE }
