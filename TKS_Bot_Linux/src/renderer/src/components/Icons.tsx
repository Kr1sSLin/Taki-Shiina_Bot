/**
 * 内联 SVG 图标集。
 *
 * 不引入第三方图标库以控制 AppImage 体积（NFR-3），全部为 24x24 viewBox 的纯路径图标，
 * 通过 `currentColor` 继承文字色，天然适配主题与无障碍对比度（NFR-11）。
 */

import type { SVGProps } from 'react'

export type IconProps = SVGProps<SVGSVGElement> & { size?: number }

function Base({ size = 20, children, ...rest }: IconProps & { children: React.ReactNode }): JSX.Element {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      {...rest}
    >
      {children}
    </svg>
  )
}

export const IconSend = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M4.5 12 20 4.5 12.5 20l-2.2-6.1z" />
    <path d="M10.3 13.9 20 4.5" />
  </Base>
)

export const IconPlus = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M12 5v14M5 12h14" />
  </Base>
)

export const IconClose = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M6 6l12 12M18 6L6 18" />
  </Base>
)

export const IconImage = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <rect x="3" y="4" width="18" height="16" rx="3" />
    <circle cx="8.5" cy="9.5" r="1.5" />
    <path d="M21 16l-5-5-6.5 9" />
  </Base>
)

export const IconSettings = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <circle cx="12" cy="12" r="3" />
    <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1A1.7 1.7 0 0 0 9 19.4a1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1A1.7 1.7 0 0 0 4.6 9a1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.9.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.9V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
  </Base>
)

export const IconChat = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M21 12a8 8 0 0 1-8 8H7l-4 3 1-4.5A8 8 0 0 1 13 4a8 8 0 0 1 8 8z" />
  </Base>
)

export const IconHistory = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M3 12a9 9 0 1 0 3-6.7" />
    <path d="M3 4v4h4" />
    <path d="M12 8v4l3 2" />
  </Base>
)

export const IconUser = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <circle cx="12" cy="8" r="4" />
    <path d="M4 21c0-4 3.6-6 8-6s8 2 8 6" />
  </Base>
)

export const IconSearch = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <circle cx="11" cy="11" r="7" />
    <path d="M20 20l-3.5-3.5" />
  </Base>
)

export const IconRefresh = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M20 11a8 8 0 1 0-1.5 5.7" />
    <path d="M20 5v6h-6" />
  </Base>
)

export const IconBell = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M18 9a6 6 0 1 0-12 0c0 5-2 6-2 6h16s-2-1-2-6" />
    <path d="M13.7 19a2 2 0 0 1-3.4 0" />
  </Base>
)

export const IconTrash = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M4 7h16M9 7V5h6v2M6 7l1 13h10l1-13" />
  </Base>
)

export const IconCopy = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <rect x="9" y="9" width="11" height="11" rx="2" />
    <path d="M15 9V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h3" />
  </Base>
)

export const IconEmoji = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <circle cx="12" cy="12" r="9" />
    <path d="M8.5 10h.01M15.5 10h.01" />
    <path d="M8.5 14.5s1.3 1.5 3.5 1.5 3.5-1.5 3.5-1.5" />
  </Base>
)

export const IconChevronLeft = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M15 5l-7 7 7 7" />
  </Base>
)

export const IconChevronRight = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M9 5l7 7-7 7" />
  </Base>
)

export const IconGift = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <rect x="3" y="9" width="18" height="12" rx="2" />
    <path d="M3 13h18M12 9v12" />
    <path d="M12 9S10.5 4 8 4a2 2 0 0 0 0 5zM12 9s1.5-5 4-5a2 2 0 0 1 0 5z" />
  </Base>
)

export const IconCalendar = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <rect x="3" y="5" width="18" height="16" rx="2" />
    <path d="M3 10h18M8 3v4M16 3v4" />
  </Base>
)

export const IconAlarm = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <circle cx="12" cy="13" r="8" />
    <path d="M12 9v4l2.5 2M5 3.5 3 5.5M19 3.5l2 2" />
  </Base>
)

export const IconDownload = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M12 4v11M7.5 10.5 12 15l4.5-4.5" />
    <path d="M4 19h16" />
  </Base>
)

export const IconFolder = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />
  </Base>
)

export const IconLink = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M10 13a5 5 0 0 0 7 0l2-2a5 5 0 0 0-7-7l-1 1" />
    <path d="M14 11a5 5 0 0 0-7 0l-2 2a5 5 0 0 0 7 7l1-1" />
  </Base>
)

export const IconWarning = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M10.3 4 2.6 18a2 2 0 0 0 1.7 3h15.4a2 2 0 0 0 1.7-3L13.7 4a2 2 0 0 0-3.4 0z" />
    <path d="M12 9v5M12 17.5h.01" />
  </Base>
)

export const IconCheck = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M5 13l4.5 4.5L19 7" />
  </Base>
)

export const IconInfo = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 11v5M12 8h.01" />
  </Base>
)

export const IconClock = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <circle cx="12" cy="12" r="9" />
    <path d="M12 7v5.5l3.5 2" />
  </Base>
)

export const IconLogout = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3" />
    <path d="M10 8 6 12l4 4M6 12h9" />
  </Base>
)

export const IconUpload = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M12 16V5M7.5 9.5 12 5l4.5 4.5" />
    <path d="M4 19h16" />
  </Base>
)

export const IconExpand = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M9 3H3v6M15 3h6v6M15 21h6v-6M9 21H3v-6" />
  </Base>
)

export const IconSparkle = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z" />
    <path d="M18 16l.9 2.1L21 19l-2.1.9L18 22l-.9-2.1L15 19l2.1-.9z" />
  </Base>
)

export const IconTray = (p: IconProps): JSX.Element => (
  <Base {...p}>
    <path d="M3 14h5l1 3h6l1-3h5" />
    <path d="M5 14 7 5h10l2 9v5H5z" />
  </Base>
)
