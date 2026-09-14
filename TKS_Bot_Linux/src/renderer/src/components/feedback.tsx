/**
 * Toast 与升级庆祝（FR-LV-4 / FR-LV-5 / FR-LV-5a、NFR-11）。
 *
 * 反馈强度**必须**区分（这是 v1.1 明确区分的三条路径）：
 *   - `UPGRADE`（首次达成）→ 弹窗 + 动画 + 桌面通知三重反馈
 *   - `RESTORE`（曾达成过，多为补签追溯）→ **仅**轻量 toast + 普通通知，不播升级动画、不弹庆祝弹窗
 *   - `RESET`（断签回落）→ 提示「连续陪伴中断，等级已重置」并引导去补签卡日历；
 *             且必须明确积分余额未受影响（FR-LV-8）
 *
 * NFR-11：在 `prefers-reduced-motion` 下禁用庆祝动画，只保留文案反馈。
 */

import { useEffect } from 'react'
import { useAppStore } from '../store/app-store'
import { useTranslation } from '../i18n'
import { Button, IconButton, Modal } from './primitives'
import { IconCheck, IconClose, IconInfo, IconSparkle, IconWarning } from './Icons'
import { useReducedMotion } from './visual'

/* ---------------------------------- Toaster ------------------------------- */

export function Toaster(): JSX.Element {
  const toasts = useAppStore((s) => s.toasts)
  const dismiss = useAppStore((s) => s.dismissToast)
  const { t } = useTranslation()

  return (
    <div className="toaster" role="status" aria-live="polite">
      {toasts.map((toast) => (
        <div key={toast.id} className={`toast glass toast-${toast.level}`}>
          <span className="toast-icon" aria-hidden="true">
            {toast.level === 'success' ? (
              <IconCheck size={16} />
            ) : toast.level === 'error' ? (
              <IconWarning size={16} />
            ) : (
              <IconInfo size={16} />
            )}
          </span>
          <span className="toast-text">{toast.text ?? t(toast.i18nKey, toast.params)}</span>
          <IconButton label={t('common.close')} onClick={() => dismiss(toast.id)}>
            <IconClose size={14} />
          </IconButton>
        </div>
      ))}
    </div>
  )
}

/* -------------------------------- Celebration ----------------------------- */

/**
 * 升级 / 等级恢复 / 断签回落 的统一反馈出口。
 * 强度差异见文件头注释。
 */
export function Celebration(): JSX.Element | null {
  const celebration = useAppStore((s) => s.celebration)
  const setCelebration = useAppStore((s) => s.setCelebration)
  const pushToast = useAppStore((s) => s.pushToast)
  const reducedMotion = useReducedMotion()
  const { t } = useTranslation()

  if (!celebration) return null

  const { kind, payload } = celebration
  const levelName = String(payload.levelName ?? '') || t('profile.level.none')
  const days = Number(payload.continuousDays ?? 0)
  const gapDays = Number(payload.gapDays ?? 0)
  const remainingDays = Number(payload.remainingDays ?? 0)
  const deadlineDate = String(payload.deadlineDate ?? '')

  const close = (): void => setCelebration(null)

  /* FR-LV-5：等级恢复 → **克制**反馈，绝不弹庆祝弹窗、不播升级动画 */
  if (kind === 'level_restore') {
    return (
      <ToastOnly
        text={t('celebration.restore.body', { level: levelName })}
        onDone={() => {
          pushToast({ level: 'info', i18nKey: 'celebration.restore.body', params: { level: levelName } })
          close()
        }}
      />
    )
  }

  /* FR-LV-5a：断签回落 → 提示并引导去补签卡日历（FR-MC-4） */
  if (kind === 'level_reset') {
    return (
      <Modal
        open
        role="alertdialog"
        title={t('celebration.reset.title')}
        onClose={close}
        footer={
          <>
            <Button variant="ghost" onClick={close}>
              {t('celebration.close')}
            </Button>
            <Button
              variant="primary"
              icon={<IconInfo size={16} />}
              onClick={() => {
                close()
                window.location.hash = '#/profile/makeup'
              }}
            >
              {t('celebration.makeup')}
            </Button>
          </>
        }
      >
        <p>{t('celebration.reset.body')}</p>
        {/* FR-LV-8：必须明确积分余额未受影响 */}
        <p className="muted">{t('profile.resetNotice')}</p>
      </Modal>
    )
  }

  /* FR-LV-6：断签预警 */
  if (kind === 'streak_warning') {
    return (
      <Modal
        open
        role="alertdialog"
        title={t('celebration.streak.title')}
        onClose={close}
        footer={
          <>
            <Button variant="ghost" onClick={close}>
              {t('celebration.close')}
            </Button>
            <Button
              variant="primary"
              icon={<IconInfo size={16} />}
              onClick={() => {
                close()
                window.location.hash = '#/profile/makeup'
              }}
            >
              {t('celebration.makeup')}
            </Button>
          </>
        }
      >
        <p>{t('celebration.streak.body', { gapDays, remainingDays, date: deadlineDate })}</p>
      </Modal>
    )
  }

  /* 补签卡发放 / 使用：走 toast，不打扰 */
  if (kind === 'makeup_granted' || kind === 'makeup_used') {
    const available = Number(payload.available ?? 0)
    return (
      <ToastOnly
        text={
          kind === 'makeup_granted'
            ? t('notify.progress.makeupGranted.body', { available })
            : t('notify.progress.makeupUsed.body', { date: '', available })
        }
        onDone={close}
      />
    )
  }

  /* FR-LV-4：**首次**达成该等级 → 弹窗 + 动画 + 桌面通知三重反馈 */
  if (kind === 'level_upgrade') {
    const legendary = String(payload.levelCode ?? '') === 'PANDA_LV7'
    return (
      <Modal
        open
        role="alertdialog"
        title={t('celebration.upgrade.title')}
        onClose={close}
        width={420}
        footer={
          <Button variant="primary" onClick={close}>
            {t('celebration.close')}
          </Button>
        }
      >
        <div className={['celebration', reducedMotion ? 'reduced' : '', legendary ? 'is-legendary' : ''].filter(Boolean).join(' ')}>
          <div className="celebration-burst" aria-hidden="true">
            <IconSparkle size={40} />
          </div>
          <p className="celebration-level">{levelName}</p>
          <p>{t('celebration.upgrade.body', { level: levelName, days })}</p>
          {reducedMotion ? <p className="muted">{t('celebration.reducedMotion')}</p> : null}
        </div>
      </Modal>
    )
  }

  return null
}

/** 「克制」反馈的载体（FR-LV-5）：只弹一条轻量 toast，然后清理事件。 */
function ToastOnly({ text, onDone }: { text: string; onDone: () => void }): JSX.Element {
  const pushToast = useAppStore((s) => s.pushToast)

  useEffect(() => {
    pushToast({ level: 'info', i18nKey: '__literal__', text })
    onDone()
    // 只在挂载时执行一次：事件随后即被清空，组件会卸载
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return <></>
}
