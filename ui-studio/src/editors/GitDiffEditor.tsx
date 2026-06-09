import { Alert, Box, Loader, SegmentedControl, Stack, Text } from '@mantine/core'
import { PatchDiff } from '@pierre/diffs/react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import { useTapStore } from '../store'

interface Props {
  /** Tab path encoded by `encodeGitDiffTabPath`. */
  path: string
}

const GIT_DIFF_TAB_PREFIX = '__gitdiff__:'

/** Encode a (side, file path) pair into the tab id used by the store. */
export function encodeGitDiffTabPath(side: 'working' | 'staged', filePath: string): string {
  return `${GIT_DIFF_TAB_PREFIX}${side}:${filePath}`
}

/** Pull the side + file path back out of a tab id. Returns null when the tab id
 *  isn't a git-diff tab. */
export function decodeGitDiffTabPath(tabPath: string): { side: 'working' | 'staged'; filePath: string } | null {
  if (!tabPath.startsWith(GIT_DIFF_TAB_PREFIX)) return null
  const rest = tabPath.slice(GIT_DIFF_TAB_PREFIX.length)
  const sep = rest.indexOf(':')
  if (sep < 0) return null
  const side = rest.slice(0, sep)
  const filePath = rest.slice(sep + 1)
  if (side !== 'working' && side !== 'staged') return null
  return { side, filePath }
}

/** Display label for the tab strip. */
export function gitDiffTabLabel(side: 'working' | 'staged', filePath: string): string {
  const name = filePath.split('/').pop() ?? filePath
  return side === 'staged' ? `${name} (staged)` : name
}

/**
 * Editor that renders a unified-diff patch for a single git path via `<PatchDiff>`
 * from `@pierre/diffs/react`. Picks the right side from the tab id; toggling sides
 * mutates the active tab in-place so the user doesn't accumulate one tab per side.
 */
export function GitDiffEditor({ path }: Props) {
  const parsed = decodeGitDiffTabPath(path)
  const renameTab = useTapStore((s) => s.renameTab)
  const generation = useTapStore((s) => s.generation)

  // Local side state, seeded from the tab id and pushed back on toggle.
  const [side, setSide] = useState<'working' | 'staged'>(parsed?.side ?? 'working')
  useEffect(() => { if (parsed && parsed.side !== side) setSide(parsed.side) }, [parsed?.side])

  const filePath = parsed?.filePath ?? null

  const [patch, setPatch] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (!filePath) return
    setLoading(true); setError(null); setPatch(null)
    let cancelled = false
    api.gitDiff(filePath, side === 'staged')
      .then((text) => { if (!cancelled) setPatch(text) })
      .catch((e) => {
        if (cancelled) return
        setError(e instanceof ApiError ? e.message : String(e))
      })
      .finally(() => !cancelled && setLoading(false))
    return () => { cancelled = true }
  }, [filePath, side, generation])

  const sideOptions = useMemo(
    () => [
      { value: 'working', label: 'Working' },
      { value: 'staged', label: 'Staged' },
    ],
    [],
  )

  if (!parsed) {
    return <Alert color="red" m="md" variant="light">Invalid git diff tab.</Alert>
  }

  function pickSide(next: 'working' | 'staged') {
    if (next === side || !filePath) return
    setSide(next)
    renameTab(path, encodeGitDiffTabPath(next, filePath), gitDiffTabLabel(next, filePath))
  }

  return (
    <Stack gap={0} h="100%">
      <Box p="sm" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
        <SegmentedControl
          size="xs"
          value={side}
          onChange={(v) => pickSide(v as 'working' | 'staged')}
          data={sideOptions}
          maw={240}
        />
        <Text size="xs" c="dimmed" mt={6} ff="var(--mono)" title={filePath ?? undefined} truncate>
          {filePath}
        </Text>
      </Box>
      <Box style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        {loading && (
          <Stack align="center" justify="center" h="100%">
            <Loader size="sm" />
          </Stack>
        )}
        {!loading && error && (
          <Alert color="red" variant="light" m="md">{error}</Alert>
        )}
        {!loading && !error && patch !== null && patch.length === 0 && (
          <Text c="dimmed" size="sm" p="md">
            No textual diff (possibly binary, identical, or — for staged view — nothing in the index).
          </Text>
        )}
        {!loading && !error && patch !== null && patch.length > 0 && (
          <PatchDiff patch={patch} disableWorkerPool />
        )}
      </Box>
    </Stack>
  )
}
