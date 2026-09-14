/**
 * 独立「记忆档案」窗口（FR-DSK-8）。
 *
 * 多窗口路由的极薄外壳：只提供 `page` 容器，内容全部复用 `FactsWindow`，
 * 保证与历史页 Tab 内的记忆列表行为完全一致。
 */

import { useTranslation } from '../../i18n'
import { FactsWindow } from './FactsWindow'

export function HistoryWindow(): JSX.Element {
  const { t } = useTranslation()

  return (
    <div className="page">
      <header className="page-head">
        <h1 className="page-title">{t('history.tab.facts')}</h1>
      </header>
      <div className="page-body">
        <FactsWindow />
      </div>
    </div>
  )
}

export default HistoryWindow
