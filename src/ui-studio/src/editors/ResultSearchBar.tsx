import { ActionIcon, Group, Text, TextInput, Tooltip } from '@mantine/core'
import { useMergedRef } from '@mantine/hooks'
import { IconChevronDown, IconChevronUp, IconLetterCase, IconRegex, IconSearch, IconX } from '@tabler/icons-react'
import { forwardRef, useEffect, useRef } from 'react'
import { MAX_MATCHES, type ResultSearchState } from './resultSearch'

/**
 * The find bar that drops in under the response panel's tab strip.
 *
 * It renders two ways because the tabs below it mean two different things by "search".
 * `find` (body, request) walks matches inside one document, so it gets a position readout and
 * step arrows. `filter` (events, frames, headers, cookies) narrows a list, so it reports how
 * much of the list survived and has nothing to step through. Keeping one bar for both is
 * deliberate: the query follows you across tabs, and a second input would only invite them to
 * drift apart.
 */
export interface ResultSearchBarProps {
  value: ResultSearchState
  onChange: (next: ResultSearchState) => void
  onClose: () => void
  mode: 'find' | 'filter'
  /** find: total matches in the document. filter: rows that survived. */
  count: number
  /** filter only: rows before the query was applied. */
  total?: number
  /** find only: zero-based index of the highlighted match. */
  active?: number
  onStep?: (delta: 1 | -1) => void
  /** Message from `RegExp` when the pattern doesn't compile. */
  error?: string | null
  /** Take focus on mount. Set when the user just opened the bar; left off when the bar is
   *  merely being restored (switching back to a tab that had it open shouldn't move focus). */
  autoFocus?: boolean
}

export const ResultSearchBar = forwardRef<HTMLInputElement, ResultSearchBarProps>(function ResultSearchBar(
  { value, onChange, onClose, mode, count, total, active = 0, onStep, error, autoFocus }, ref,
) {
  // The bar only exists while search is open, so mounting *is* the moment to take focus —
  // the panel opening it can't reliably focus across the render that mounts us.
  const inputRef = useRef<HTMLInputElement>(null)
  const mergedRef = useMergedRef(ref, inputRef)
  const focusOnMount = useRef(autoFocus)
  useEffect(() => { if (focusOnMount.current) inputRef.current?.select() }, [])

  const set = (patch: Partial<ResultSearchState>) => onChange({ ...value, ...patch })
  const hasQuery = value.query.length > 0
  const capped = count >= MAX_MATCHES

  return (
    <Group
      gap={6}
      wrap="nowrap"
      px="md"
      py={4}
      style={{ borderBottom: '1px solid var(--mantine-color-default-border)', flexShrink: 0 }}
    >
      <TextInput
        ref={mergedRef}
        size="xs"
        flex={1}
        maw={420}
        autoComplete="off"
        spellCheck={false}
        placeholder={mode === 'find' ? 'Find in response…' : 'Filter rows…'}
        aria-label={mode === 'find' ? 'Find in response' : 'Filter rows'}
        value={value.query}
        error={!!error}
        leftSection={<IconSearch size={13} />}
        rightSectionWidth={58}
        rightSection={
          <Group gap={2} wrap="nowrap" pr={4}>
            <ToggleIcon
              label="Match case"
              active={value.caseSensitive}
              onClick={() => set({ caseSensitive: !value.caseSensitive })}
            >
              <IconLetterCase size={13} />
            </ToggleIcon>
            <ToggleIcon
              label="Regular expression"
              active={value.regex}
              onClick={() => set({ regex: !value.regex })}
            >
              <IconRegex size={13} />
            </ToggleIcon>
          </Group>
        }
        onChange={(e) => set({ query: e.currentTarget.value })}
        onKeyDown={(e) => {
          if (e.key === 'Escape') { e.preventDefault(); onClose(); return }
          if (e.key === 'Enter' && mode === 'find') {
            e.preventDefault()
            onStep?.(e.shiftKey ? -1 : 1)
          }
        }}
      />

      <Text
        size="xs"
        c={error ? 'red' : 'dimmed'}
        ff={error ? undefined : 'var(--mono)'}
        title={error ?? undefined}
        // A regex complaint can run long; it must not push the step arrows off the strip.
        style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}
      >
        {error
          ? error
          : !hasQuery
            ? ''
            : count === 0
              ? 'no matches'
              : mode === 'find'
                ? `${active + 1}/${capped ? `${count}+` : count}`
                : `${count} of ${total ?? count}`}
      </Text>

      {mode === 'find' && (
        <Group gap={2} wrap="nowrap">
          <Tooltip label="Previous match (Shift+Enter)" withArrow openDelay={500}>
            <ActionIcon variant="subtle" color="gray" size="sm" disabled={count === 0} onClick={() => onStep?.(-1)} aria-label="Previous match">
              <IconChevronUp size={14} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label="Next match (Enter)" withArrow openDelay={500}>
            <ActionIcon variant="subtle" color="gray" size="sm" disabled={count === 0} onClick={() => onStep?.(1)} aria-label="Next match">
              <IconChevronDown size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>
      )}

      <Group gap={0} wrap="nowrap" ml="auto">
        <Tooltip label="Close search (Esc)" withArrow openDelay={500}>
          <ActionIcon variant="subtle" color="gray" size="sm" onClick={onClose} aria-label="Close search">
            <IconX size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>
    </Group>
  )
})

function ToggleIcon({ label, active, onClick, children }: {
  label: string
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <Tooltip label={label} withArrow openDelay={500}>
      <ActionIcon
        variant={active ? 'filled' : 'subtle'}
        color={active ? 'tap' : 'gray'}
        size={20}
        onClick={onClick}
        aria-label={label}
        aria-pressed={active}
      >
        {children}
      </ActionIcon>
    </Tooltip>
  )
}
