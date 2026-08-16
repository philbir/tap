import {
  ActionIcon, Alert, Badge, Group, Loader, Paper, Stack, Text, Tooltip, UnstyledButton,
} from '@mantine/core'
import {
  IconAlertCircle, IconAlertTriangle, IconExternalLink, IconPlayerPlayFilled, IconPlayerStopFilled,
} from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError } from '../api/client'
import type { HttpRequestSummary, TreeNode, VariableContext, WorkspaceErrorDto } from '../api/types'
import { useActiveEnv, useTapStore } from '../store'
import { CollectionLinkChip } from './CollectionLinkChip'
import { EditorShell } from './EditorShell'
import { ResponsePanel } from './ResponsePanel'
import { SourceCodeEditor } from './SourceCodeEditor'
import { useExecution } from './useExecution'

interface Props {
  /** Workspace-relative path of the `.http` file (no fragment). */
  path: string
}

/** Method colors, matched to how the rest of Studio reads verbs (green = safe, red = destructive). */
const METHOD_COLOR: Record<string, string> = {
  GET: 'teal', HEAD: 'teal', OPTIONS: 'gray',
  POST: 'blue', PUT: 'orange', PATCH: 'orange',
  DELETE: 'red',
}

/**
 * Editor for a portable `.http` file.
 *
 * **Raw-first, deliberately.** Every other kind in Studio round-trips through a structured spec
 * and the server re-emits canonical YAML. A `.http` file gets none of that: the file on disk stays
 * the source of truth in its own format, and Tap never reformats a file it did not author. That is
 * the trust contract for someone bringing a file their team already shares with Visual Studio —
 * so this editor is the raw source editor, with http highlighting, plus a way to send.
 *
 * **The list comes from the parser, and so does the send.** The requests above the editor are
 * whatever the server's parser finds in the text currently on screen — not in the file on disk.
 * That matters because a `.http` request is named by its own content: adding a request or editing
 * a request line changes which requests exist and what they are called. Sending posts that same
 * text back, so the row the user clicked and the request the server runs are resolved from one
 * string by one parser. Iterating means type, click, read the response — no save in the loop.
 */
export function HttpFileEditor({ path }: Props) {
  const tree = useTapStore((s) => s.tree)
  const generation = useTapStore((s) => s.generation)
  const openTab = useTapStore((s) => s.openTab)
  const collections = useTapStore((s) => s.collections)
  const activeEnv = useActiveEnv()

  /** Last-saved text, as the server handed it over. `null` until the first load lands. */
  const [source, setSource] = useState<string | null>(null)
  const [draft, setDraft] = useState('')
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  /** A rejected save (or a parse failure in the draft) — pinned on its line in the editor. */
  const [fileError, setFileError] = useState<WorkspaceErrorDto | null>(null)
  const [parsed, setParsed] = useState<HttpRequestSummary[] | null>(null)
  const [warnings, setWarnings] = useState<WorkspaceErrorDto[]>([])
  /** Which request the response pane belongs to — a file holds several. */
  const [sentPath, setSentPath] = useState<string | null>(null)
  const [reveal, setReveal] = useState<{ line: number; nonce: number } | null>(null)
  const revealNonce = useRef(0)
  /** Stage override for this session; `null` means "the collection's default". */
  const [stage, setStage] = useState<string | null>(null)

  const { rendered, execution, error: sendError, sending, stopped, send, stop, clear } = useExecution()

  const fileName = path.split('/').pop() ?? path
  const dirty = source !== null && draft !== source

  // The owning collection is purely positional — every request lives under
  // `collections/<slug>/…`, so a .http file's collection is whatever sits at the same slug.
  // It supplies the baseUrl every relative request line in this file resolves against, which
  // is why the chip belongs here as much as it does in the request editor.
  const linkedCollection = useMemo(() => {
    const parts = path.split('/')
    if (parts.length < 3 || parts[0] !== 'collections') return null
    return collections.find((c) => c.slug === parts[1]) ?? null
  }, [collections, path])

  useEffect(() => { setStage(null) }, [linkedCollection?.slug])
  const effectiveStage = stage ?? linkedCollection?.defaultStage ?? null

  // Scoped to the file, not to one request inside it: the server attributes a collection by
  // walking the path's directories, so the file path resolves the same collection/stage/env
  // cascade its requests do. The request-scope layer is simply absent, which is correct —
  // a file has no single request's vars.
  const variableContext = useMemo<VariableContext>(() => ({
    requestPath: path,
    envPath: activeEnv ?? undefined,
    stage: effectiveStage ?? undefined,
  }), [path, activeEnv, effectiveStage])

  useEffect(() => {
    let cancelled = false
    setSource(null); setLoadError(null); setFileError(null); setParsed(null); setWarnings([])
    api.source(path)
      .then((s) => { if (!cancelled) { setSource(s); setDraft(s) } })
      .catch((e: unknown) => { if (!cancelled) setLoadError(e instanceof Error ? e.message : String(e)) })
    return () => { cancelled = true }
  }, [path, generation])

  // Drop any response when the editor switches files — it belongs to the old one.
  useEffect(() => { clear(); setSentPath(null) }, [path, clear])

  // Re-parse the draft server-side as it changes. Debounced while typing; immediate for text
  // that just arrived from disk, so opening a file doesn't blink through an empty list.
  useEffect(() => {
    if (source === null) return
    let cancelled = false
    const timer = setTimeout(() => {
      api.parseHttpFile(path, draft)
        .then((r) => {
          if (cancelled) return
          setParsed(r.requests)
          setWarnings(r.errors.filter((e) => e.severity === 'warning'))
          // A hard parse error is worth a marker while typing — but only from the parse, never
          // sticky: the next keystroke re-parses and clears it.
          setFileError(r.errors.find((e) => e.severity === 'error') ?? null)
        })
        // A failed parse call is not worth an error bar. The list simply stops updating, and
        // the authoritative answer arrives from the next keystroke or the next Send.
        .catch(() => {})
    }, draft === source ? 0 : 250)
    return () => { cancelled = true; clearTimeout(timer) }
  }, [draft, source, path])

  /** Requests as of the last successful parse, falling back to the workspace tree until the
   *  first parse lands (which is what is on disk — correct until the user types). */
  const treeRequests = useMemo(() => {
    const find = (nodes: TreeNode[]): TreeNode | null => {
      for (const n of nodes) {
        if (n.kind === 'httpfile' && n.path === path) return n
        const hit = find(n.children ?? [])
        if (hit) return hit
      }
      return null
    }
    return find(tree)?.children ?? []
  }, [tree, path])

  const requests: HttpRequestSummary[] = useMemo(
    () => parsed ?? treeRequests.map((n) => ({ path: n.path, name: n.name, method: '', url: '', line: 1 })),
    [parsed, treeRequests],
  )

  /** Fragment paths that exist on disk. A request that only exists in the draft can be sent
   *  but not opened in its own tab — the request editor reads the workspace, not this draft. */
  const savedPaths = useMemo(() => new Set(treeRequests.map((n) => n.path)), [treeRequests])

  const save = useCallback(async () => {
    setSaving(true); setFileError(null); setLoadError(null)
    try {
      await api.saveSource(path, draft)
      // The file watcher bumps `generation`, which refetches and resets `source` to `draft`.
    } catch (e) {
      if (e instanceof ApiError && e.payload && typeof e.payload === 'object' && 'code' in e.payload) {
        setFileError(e.payload as WorkspaceErrorDto)
      } else {
        setLoadError(e instanceof Error ? e.message : String(e))
      }
    } finally { setSaving(false) }
  }, [path, draft])

  function sendRequest(request: HttpRequestSummary) {
    setSentPath(request.path)
    send({
      path: request.path,
      env: activeEnv,
      stage: effectiveStage,
      // Only send the draft when it differs. On a clean file the on-disk read is the same
      // text, and skipping it keeps the saved path exercising exactly the saved-file code.
      source: dirty ? draft : undefined,
    })
  }

  function revealRequest(request: HttpRequestSummary) {
    revealNonce.current += 1
    setReveal({ line: request.line, nonce: revealNonce.current })
  }

  const sentRequest = requests.find((r) => r.path === sentPath) ?? null

  return (
    <EditorShell
      title={fileName}
      kindLabel="HTTP file"
      dirty={dirty}
      saving={saving}
      errorMessage={loadError}
      onSave={save}
      onDiscard={() => { if (source !== null) setDraft(source) }}
      toolbarExtras={
        <Tooltip
          label="Portable .http file — Tap reads and sends it, but never reformats it."
          withArrow multiline w={260}
        >
          <Badge variant="light" color="blue" size="sm">raw</Badge>
        </Tooltip>
      }
      bottomPane={
        (execution || rendered || sendError || sending) ? (
          <ResponsePanel
            rendered={rendered}
            execution={execution}
            error={sendError}
            busy={sending}
            stopped={stopped}
            onStop={sending ? stop : undefined}
            requestPath={sentPath ?? path}
            requestName={sentRequest?.name ?? fileName}
            onClose={() => { clear(); setSentPath(null) }}
          />
        ) : undefined
      }
    >
      <Stack gap="sm" mt="xs">
        {/* Sits above the request list rather than beside any one row: the collection and
            stage apply to every request in the file, and every relative request line below
            is read against them. */}
        {linkedCollection && (
          <Group gap="xs" wrap="nowrap">
            <CollectionLinkChip
              summary={linkedCollection}
              stage={effectiveStage}
              onStageChange={setStage}
              variableContext={variableContext}
              onOpen={() => openTab({
                path: `collections/${linkedCollection.slug}`,
                kind: 'collection',
                label: linkedCollection.name,
              })}
            />
            <Text size="xs" c="dimmed">
              Relative request lines below resolve against this base URL.
            </Text>
          </Group>
        )}

        {fileError && (
          <Alert color="red" variant="light" icon={<IconAlertCircle size={14} />} title={fileError.code}>
            <Stack gap={2}>
              <Text size="sm">{fileError.message}</Text>
              {fileError.line != null && (
                <Text size="xs" c="dimmed" ff="var(--mono)">{fileError.path}:{fileError.line}</Text>
              )}
            </Stack>
          </Alert>
        )}

        {warnings.length > 0 && (
          <Alert
            color="yellow" variant="light" icon={<IconAlertTriangle size={14} />}
            title={`${warnings.length} construct${warnings.length === 1 ? '' : 's'} Tap does not run`}
          >
            <Stack gap={2}>
              {warnings.map((w, i) => (
                <Text key={i} size="xs">
                  {w.line != null && <Text component="span" c="dimmed" ff="var(--mono)">line {w.line}: </Text>}
                  {w.message}
                </Text>
              ))}
            </Stack>
          </Alert>
        )}

        {source === null && !loadError && <Loader size="sm" />}

        {source !== null && (
          <>
            <Paper withBorder radius="sm" p={4}>
              {requests.length === 0 ? (
                <Text size="sm" c="dimmed" p="xs">
                  No requests yet — a <Text component="span" ff="var(--mono)" fz="xs">METHOD URL</Text> line
                  below makes one.
                </Text>
              ) : (
                <Stack gap={2}>
                  {requests.map((r) => {
                    const isSending = sending && sentPath === r.path
                    return (
                      <Group key={r.path} gap="xs" wrap="nowrap" pl="xs" pr={4}>
                        <UnstyledButton
                          onClick={() => revealRequest(r)}
                          title="Show in the editor"
                          style={{ flex: 1, minWidth: 0 }}
                        >
                          <Group gap="xs" wrap="nowrap">
                            {r.method && (
                              <Badge
                                size="xs" variant="light" radius="sm"
                                color={METHOD_COLOR[r.method] ?? 'gray'}
                                style={{ flexShrink: 0, fontFamily: 'var(--mono)' }}
                              >
                                {r.method}
                              </Badge>
                            )}
                            <Text size="sm" fw={500} style={{ flexShrink: 0 }}>{r.name}</Text>
                            <Text size="xs" c="dimmed" ff="var(--mono)" truncate>{r.url}</Text>
                          </Group>
                        </UnstyledButton>

                        <Tooltip label={savedPaths.has(r.path) ? 'Open in its own tab' : 'Save the file to open this request in its own tab'} withArrow>
                          <ActionIcon
                            variant="subtle" color="gray" size="sm"
                            aria-label={`Open ${r.name}`}
                            disabled={!savedPaths.has(r.path)}
                            onClick={() => openTab({ path: r.path, kind: 'request', label: r.name })}
                          >
                            <IconExternalLink size={14} />
                          </ActionIcon>
                        </Tooltip>

                        <Tooltip
                          label={isSending ? 'Stop' : dirty ? 'Send (using unsaved changes)' : 'Send'}
                          withArrow
                        >
                          <ActionIcon
                            variant={isSending ? 'filled' : 'light'}
                            color={isSending ? 'red' : 'tap'}
                            size="sm"
                            aria-label={isSending ? `Stop ${r.name}` : `Send ${r.name}`}
                            onClick={() => (isSending ? stop() : sendRequest(r))}
                            disabled={sending && !isSending}
                          >
                            {isSending ? <IconPlayerStopFilled size={12} /> : <IconPlayerPlayFilled size={12} />}
                          </ActionIcon>
                        </Tooltip>
                      </Group>
                    )
                  })}
                </Stack>
              )}
            </Paper>

            <Text size="xs" c="dimmed">
              This file is the source of truth and is never reformatted by Tap. Send runs the text
              as it is on screen, saved or not.
            </Text>

            <SourceCodeEditor
              value={draft}
              onChange={setDraft}
              language="http"
              error={fileError}
              onSave={save}
              reveal={reveal}
            />
          </>
        )}
      </Stack>
    </EditorShell>
  )
}
