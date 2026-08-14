import { Tabs, Tooltip, type TabsListProps, type TabsTabProps } from '@mantine/core'
import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react'

/**
 * Gap painted between a tab's icon and its label. This is the same 10px Mantine puts on
 * `tabSection` for `--mantine-spacing-xs`; we take it over as a plain number so the measuring
 * code below knows the exact pixel cost of showing a label.
 */
const LABEL_GAP = 10

/**
 * Mantine's default hangs the icon/label gap off `tabSection` and lets `tabLabel` flex. We own
 * both so a tab's width is a plain sum of its parts — icon + (gap + label) + adornment — which
 * is what makes the arithmetic in `measure` exact rather than a guess.
 */
const TAB_STYLES: TabsTabProps['styles'] = {
  tabSection: { marginInlineEnd: 0 },
  tabLabel: { flex: 'none' },
}

export interface AdaptiveTab {
  value: string
  /** Shown beside the icon while it fits, and as the tooltip once it has been dropped. */
  label: string
  /** Always visible — it is what identifies the tab after the label goes. */
  icon: ReactNode
  /** Count badge / presence dot. Stays visible when the label is dropped. */
  adornment?: ReactNode
}

export interface AdaptiveTabsListProps extends Omit<TabsListProps, 'children'> {
  tabs: AdaptiveTab[]
}

/**
 * A `Tabs.List` that never wraps to a second row. When the tabs don't fit the pane, labels are
 * dropped one at a time starting from the RIGHT-most tab — the icon (and its count badge) stays,
 * with a tooltip carrying the name. Labels come back as soon as there is room again, so widening
 * the editor or closing a side pane restores them.
 *
 * Sizing is measured, not guessed. Every label lives in a clipping wrapper whose `max-width`
 * collapses to 0, while the text inside keeps its natural layout width — so one pass over the
 * DOM yields both each tab's icon-only width and the cost of its label, whatever state the row
 * is currently in. The number of dropped labels is therefore a pure function of those widths
 * and the available space, which is what keeps it from oscillating.
 */
export function AdaptiveTabsList({ tabs, ...listProps }: AdaptiveTabsListProps) {
  const listRef = useRef<HTMLDivElement>(null)
  const [hidden, setHidden] = useState(0)
  // Mirrors `hidden` so `measure` can read it without being re-created on every change.
  const hiddenRef = useRef(0)

  const measure = useCallback(() => {
    const list = listRef.current
    if (!list) return
    const count = tabs.length
    const els = Array.from(list.children) as HTMLElement[]
    if (els.length !== count) return

    const firstCollapsed = count - hiddenRef.current
    const slots: number[] = []
    // Total width of the row with every label dropped.
    let fixed = 0
    for (let i = 0; i < count; i++) {
      const text = els[i].querySelector<HTMLElement>('[data-tab-text]')
      slots[i] = text ? text.getBoundingClientRect().width + LABEL_GAP : 0
      fixed += els[i].offsetWidth - (i < firstCollapsed ? slots[i] : 0)
    }

    const style = getComputedStyle(list)
    const gaps = (parseFloat(style.columnGap) || 0) * Math.max(0, count - 1)
    const available = list.clientWidth - (parseFloat(style.paddingLeft) || 0) - (parseFloat(style.paddingRight) || 0)

    let width = fixed + gaps + slots.reduce((sum, slot) => sum + slot, 0)
    let next = 0
    while (width > available && next < count) {
      width -= slots[count - 1 - next]
      next += 1
    }

    if (next !== hiddenRef.current) {
      hiddenRef.current = next
      setHidden(next)
    }
  }, [tabs.length])

  // Re-measure after every render: labels and count badges change as the user edits.
  useLayoutEffect(measure)

  // …and when the row itself is resized (window, sidebar, dragging the Assistant divider).
  useEffect(() => {
    const list = listRef.current
    if (!list) return
    const observer = new ResizeObserver(measure)
    observer.observe(list)
    return () => observer.disconnect()
  }, [measure])

  const firstCollapsed = tabs.length - hidden

  return (
    <Tabs.List
      ref={listRef}
      {...listProps}
      style={{
        flexWrap: 'nowrap',
        // Last-resort escape hatch: if even the icon-only row is too wide, scroll rather than wrap.
        overflowX: 'auto',
        scrollbarWidth: 'none',
        ...listProps.style,
      }}
    >
      {tabs.map((tab, i) => {
        const collapsed = i >= firstCollapsed
        return (
          <Tooltip key={tab.value} label={tab.label} disabled={!collapsed} openDelay={300} withArrow>
            <Tabs.Tab value={tab.value} leftSection={tab.icon} styles={TAB_STYLES}>
              <span
                style={{
                  display: 'inline-block',
                  verticalAlign: 'middle',
                  overflow: 'hidden',
                  maxWidth: collapsed ? 0 : undefined,
                  marginInlineStart: collapsed ? 0 : LABEL_GAP,
                }}
              >
                {/* Never clamped, so its box keeps the label's natural width for measuring. */}
                <span data-tab-text="" style={{ display: 'inline-block', whiteSpace: 'nowrap' }}>
                  {tab.label}
                </span>
              </span>
              {tab.adornment}
            </Tabs.Tab>
          </Tooltip>
        )
      })}
    </Tabs.List>
  )
}
