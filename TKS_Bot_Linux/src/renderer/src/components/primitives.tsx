/**
 * 基础 UI 原语。
 *
 * NFR-11（可访问性）：全部交互元素可 Tab 聚焦、焦点态可见；
 * 对比度符合 WCAG AA；动画尊重 `prefers-reduced-motion`（见 styles/global.css）。
 */

import { forwardRef, useEffect, useRef, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from 'react'
import { IconClose } from './Icons'

/* ----------------------------------- Button ------------------------------- */

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger'
export type ButtonSize = 'sm' | 'md' | 'lg'

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: ButtonSize
  loading?: boolean
  icon?: ReactNode
  block?: boolean
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', size = 'md', loading = false, icon, block = false, children, disabled, className, ...rest },
  ref
) {
  return (
    <button
      ref={ref}
      type="button"
      className={['btn', `btn-${variant}`, `btn-${size}`, block ? 'btn-block' : '', className ?? '']
        .filter(Boolean)
        .join(' ')}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      {...rest}
    >
      {loading ? <Spinner size={size === 'sm' ? 12 : 16} /> : icon}
      {children ? <span>{children}</span> : null}
    </button>
  )
})

/* --------------------------------- IconButton ----------------------------- */

export interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** 无障碍名称（NFR-11：纯图标按钮必须有 aria-label）。 */
  label: string
  active?: boolean
}

export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton(
  { label, active, children, className, ...rest },
  ref
) {
  return (
    <button
      ref={ref}
      type="button"
      className={['icon-btn', active ? 'is-active' : '', className ?? ''].filter(Boolean).join(' ')}
      aria-label={label}
      title={label}
      {...rest}
    >
      {children}
    </button>
  )
})

/* ----------------------------------- Spinner ------------------------------ */

export function Spinner({ size = 16 }: { size?: number }): JSX.Element {
  return <span className="spinner" style={{ width: size, height: size }} role="status" aria-hidden="true" />
}

/* ----------------------------------- Input -------------------------------- */

export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string
  hint?: string
  error?: string | null
}

export const Input = forwardRef<HTMLInputElement, InputProps>(function Input(
  { label, hint, error, className, id, ...rest },
  ref
) {
  const inputId = id ?? rest.name ?? `input_${Math.random().toString(36).slice(2, 8)}`
  return (
    <div className={['field', className ?? ''].filter(Boolean).join(' ')}>
      {label ? (
        <label className="field-label" htmlFor={inputId}>
          {label}
        </label>
      ) : null}
      <input
        ref={ref}
        id={inputId}
        className={['input', error ? 'is-error' : ''].filter(Boolean).join(' ')}
        aria-invalid={error ? true : undefined}
        {...rest}
      />
      {error ? (
        <p className="field-error" role="alert">
          {error}
        </p>
      ) : hint ? (
        <p className="field-hint">{hint}</p>
      ) : null}
    </div>
  )
})

/* ---------------------------------- Textarea ------------------------------ */

export interface TextAreaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label?: string
  hint?: string
}

export const TextArea = forwardRef<HTMLTextAreaElement, TextAreaProps>(function TextArea(
  { label, hint, className, id, ...rest },
  ref
) {
  const areaId = id ?? `ta_${Math.random().toString(36).slice(2, 8)}`
  return (
    <div className={['field', className ?? ''].filter(Boolean).join(' ')}>
      {label ? (
        <label className="field-label" htmlFor={areaId}>
          {label}
        </label>
      ) : null}
      <textarea ref={ref} id={areaId} className="input textarea" {...rest} />
      {hint ? <p className="field-hint">{hint}</p> : null}
    </div>
  )
})

/* ----------------------------------- Switch ------------------------------- */

export interface SwitchProps {
  checked: boolean
  onChange: (checked: boolean) => void
  label: string
  hint?: string
  disabled?: boolean
}

export function Switch({ checked, onChange, label, hint, disabled }: SwitchProps): JSX.Element {
  return (
    <label className={['switch-row', disabled ? 'is-disabled' : ''].filter(Boolean).join(' ')}>
      <span className="switch-text">
        <span className="switch-label">{label}</span>
        {hint ? <span className="switch-hint">{hint}</span> : null}
      </span>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-label={label}
        disabled={disabled}
        className={['switch', checked ? 'is-on' : ''].filter(Boolean).join(' ')}
        onClick={() => onChange(!checked)}
      >
        <span className="switch-knob" />
      </button>
    </label>
  )
}

/* --------------------------------- Segmented ------------------------------ */

export interface SegmentedOption<T extends string> {
  value: T
  label: string
}

export function Segmented<T extends string>({
  value,
  options,
  onChange,
  ariaLabel
}: {
  value: T
  options: Array<SegmentedOption<T>>
  onChange: (value: T) => void
  ariaLabel: string
}): JSX.Element {
  return (
    <div className="segmented" role="radiogroup" aria-label={ariaLabel}>
      {options.map((opt) => (
        <button
          key={opt.value}
          type="button"
          role="radio"
          aria-checked={value === opt.value}
          className={['segmented-item', value === opt.value ? 'is-active' : ''].filter(Boolean).join(' ')}
          onClick={() => onChange(opt.value)}
        >
          {opt.label}
        </button>
      ))}
    </div>
  )
}

/* ----------------------------------- Modal -------------------------------- */

export interface ModalProps {
  open: boolean
  title: string
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  /** `alertdialog` 用于庆祝/确认等需要立即注意的场景。 */
  role?: 'dialog' | 'alertdialog'
  width?: number
}

export function Modal({ open, title, onClose, children, footer, role = 'dialog', width }: ModalProps): JSX.Element | null {
  const ref = useRef<HTMLDivElement>(null)

  // NFR-11：Esc 关闭 + 打开时把焦点移入弹层
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') {
        e.stopPropagation()
        onClose()
      }
    }
    window.addEventListener('keydown', onKey)
    const timer = window.setTimeout(() => {
      const focusable = ref.current?.querySelector<HTMLElement>(
        'button:not([disabled]), [href], input:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
      )
      focusable?.focus()
    }, 30)
    return () => {
      window.removeEventListener('keydown', onKey)
      window.clearTimeout(timer)
    }
  }, [open, onClose])

  if (!open) return null

  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div
        className="modal glass"
        role={role}
        aria-modal="true"
        aria-label={title}
        ref={ref}
        style={width ? { width } : undefined}
      >
        <header className="modal-head">
          <h2>{title}</h2>
          <IconButton label="关闭" onClick={onClose}>
            <IconClose size={18} />
          </IconButton>
        </header>
        <div className="modal-body">{children}</div>
        {footer ? <footer className="modal-foot">{footer}</footer> : null}
      </div>
    </div>
  )
}

/* -------------------------------- EmptyState ------------------------------ */

export function EmptyState({ icon, title, hint }: { icon?: ReactNode; title: string; hint?: string }): JSX.Element {
  return (
    <div className="empty-state">
      {icon ? <div className="empty-icon">{icon}</div> : null}
      <p className="empty-title">{title}</p>
      {hint ? <p className="empty-hint">{hint}</p> : null}
    </div>
  )
}

/* ----------------------------------- Card --------------------------------- */

export function Card({
  title,
  action,
  children,
  className
}: {
  title?: string
  action?: ReactNode
  children: ReactNode
  className?: string
}): JSX.Element {
  return (
    <section className={['card glass', className ?? ''].filter(Boolean).join(' ')}>
      {title || action ? (
        <header className="card-head">
          {title ? <h3 className="card-title">{title}</h3> : <span />}
          {action}
        </header>
      ) : null}
      <div className="card-body">{children}</div>
    </section>
  )
}

/* ----------------------------------- Badge -------------------------------- */

export function Badge({ children, tone = 'neutral' }: { children: ReactNode; tone?: 'neutral' | 'ok' | 'warn' | 'error' | 'accent' }): JSX.Element {
  return <span className={`badge badge-${tone}`}>{children}</span>
}
