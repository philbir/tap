import {
  Alert, Badge, Button, Checkbox, Code, Divider, FileButton, Group, Loader, Modal, Radio,
  ScrollArea, SegmentedControl, Select, Stack, Stepper, Text, TextInput, Tooltip, UnstyledButton,
} from '@mantine/core'
import {
  IconAlertTriangle, IconCheck, IconFileCode, IconLock, IconPlugConnected, IconSearch, IconSend,
  IconUpload, IconWorld,
} from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '../api/client'
import type {
  OpenApiImportMode, WsdlDiagnostic, WsdlDocument, WsdlLayout, WsdlLink, WsdlOperation, WsdlPort,
} from '../api/types'
import { useTapStore } from '../store'

interface Props {
  open: boolean
  onOpenChange: (v: boolean) => void
  onImported: (collectionPath: string, slug: string) => void
  /** Pre-fills the slug when launched from a collection's context menu. */
  initialSlug?: string | null
}

type SourceMode = 'file' | 'url'

const NO_AUTH = '__none__'

/** SOAP 1.1 and 1.2 read differently enough at a glance to be worth different colours. */
const VERSION_COLOR: Record<string, string> = { '1.1': 'teal', '1.2': 'grape' }

export function ImportWsdlDialog({ open, onOpenChange, onImported, initialSlug }: Props) {
  const reload = useTapStore((s) => s.reload)
  const auths = useTapStore((s) => s.auths)

  const [step, setStep] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Step 0 — source
  const [sourceMode, setSourceMode] = useState<SourceMode>('file')
  const [url, setUrl] = useState('')
  const [fileName, setFileName] = useState<string | null>(null)

  // Step 1 — the staged description + selection
  const [doc, setDoc] = useState<WsdlDocument | null>(null)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [filter, setFilter] = useState('')

  // Step 2 — options
  const [slug, setSlug] = useState('')
  const [layout, setLayout] = useState<WsdlLayout>('req')
  const [baseUrl, setBaseUrl] = useState('')
  const [authPath, setAuthPath] = useState<string>(NO_AUTH)
  const [usernameToken, setUsernameToken] = useState(false)
  const [mode, setMode] = useState<OpenApiImportMode>('create')

  const [warnings, setWarnings] = useState<string[] | null>(null)

  // What already exists at the target slug, so the options step can offer the right choice
  // instead of failing the import and making the user guess.
  const [existingSlugs, setExistingSlugs] = useState<string[]>([])
  const [link, setLink] = useState<WsdlLink | null>(null)

  useEffect(() => {
    if (!open) return
    api.collections().then((c) => setExistingSlugs(c.map((x) => x.slug))).catch(() => setExistingSlugs([]))
  }, [open])

  const targetExists = existingSlugs.includes(slug.trim())

  // Launched from a collection's context menu: show what it is already linked to, and default to
  // adding rather than replacing.
  useEffect(() => {
    if (!open || !initialSlug) { setLink(null); return }
    api.wsdlLink(initialSlug).then(setLink).catch(() => setLink(null))
  }, [open, initialSlug])

  useEffect(() => {
    setMode((cur) => (cur === 'replace' ? cur : targetExists ? 'merge' : 'create'))
  }, [targetExists])

  useEffect(() => {
    if (!open) return
    reset()
    if (initialSlug) setSlug(initialSlug)
    // Only when the dialog transitions to open — re-running on every prop change would
    // wipe what the user has typed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  function reset() {
    setStep(0); setBusy(false); setError(null)
    setSourceMode('file'); setUrl(''); setFileName(null)
    setDoc(null); setSelected(new Set()); setFilter('')
    setSlug(''); setLayout('req'); setBaseUrl(''); setAuthPath(NO_AUTH)
    setUsernameToken(false); setMode('create'); setWarnings(null); setLink(null)
  }

  /** Applies a freshly staged description: everything after step 0 defaults from it. */
  function applyDocument(next: WsdlDocument) {
    setDoc(next)
    setSelected(defaultSelection(next))
    setSlug((cur) => cur || next.suggestedSlug)
    setBaseUrl(next.addresses[0] ?? '')
    setUsernameToken(next.wantsUsernameToken)
    setStep(1)
  }

  async function stageFile(file: File | null) {
    if (!file) return
    setError(null); setBusy(true); setFileName(file.name)
    try {
      // Sent verbatim — the server is the single source of truth for parsing, and it has to be:
      // resolving a message through its binding into an inlined schema is not something the
      // browser can do.
      applyDocument(await api.stageWsdlDocument(await file.text(), file.name))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  /** `override` lets the "fetch it again" shortcut skip a render cycle waiting for `url` state. */
  async function stageUrl(override?: string) {
    const target = (override ?? url).trim()
    if (!target) { setError('Enter the URL of a WSDL description.'); return }
    setError(null); setBusy(true)
    try {
      applyDocument(await api.fetchWsdlDocument(target))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally { setBusy(false) }
  }

  async function runImport() {
    if (!doc) return
    if (!slug.trim()) { setError('Give the collection a name.'); return }
    if (selected.size === 0) { setError('Select at least one operation.'); return }

    setError(null); setBusy(true)
    try {
      const result = await api.importWsdlCollection({
        documentId: doc.documentId,
        slug: slug.trim(),
        layout,
        // Null means "everything" on the wire; send explicit keys unless nothing is filtered out.
        operationKeys: selected.size === doc.operations.length ? null : [...selected],
        baseUrl: baseUrl.trim() || null,
        linkAuthPath: authPath === NO_AUTH ? null : authPath,
        addUsernameToken: usernameToken,
        mode,
      })
      await reload()
      onImported(result.collectionPath, result.slug)
      // Warnings keep the dialog open so they're actually read; a clean import just closes.
      if (result.warnings.length > 0) { setWarnings(result.warnings); setBusy(false); return }
      onOpenChange(false)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
      setBusy(false)
    }
  }

  const byPort = useMemo(() => groupByPort(doc, filter), [doc, filter])

  // Reaching this step means the description parsed, so every diagnostic on it is advisory by
  // definition. Identical messages are collapsed with a count: one omission repeated across 40
  // operations is one problem, not 40.
  const issues = useMemo(() => collapse(doc?.diagnostics ?? []), [doc])

  const authOptions = useMemo(
    () => [
      { value: NO_AUTH, label: 'None' },
      ...auths.map((a) => ({
        value: a.path,
        label: a.collection ? `${a.name} — ${a.type} (${a.collection})` : `${a.name} — ${a.type}`,
      })),
    ],
    [auths],
  )

  return (
    <Modal
      opened={open}
      onClose={() => { if (!busy) onOpenChange(false) }}
      size="xl"
      title={
        <Group gap={6}>
          <IconPlugConnected size={16} />
          <Text fw={600}>Import from WSDL</Text>
          {doc && <Badge size="sm" variant="light">{doc.title}</Badge>}
          {doc && <Badge size="sm" variant="dot" color="gray">WSDL {doc.specVersion}</Badge>}
        </Group>
      }
    >
      <Stepper active={step} onStepClick={(s) => doc && setStep(s)} size="xs" mb="md">
        <Stepper.Step label="Source" description="File or URL" />
        <Stepper.Step label="Operations" description="Pick what to import" />
        <Stepper.Step label="Options" description="Layout and security" />
      </Stepper>

      {error && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} mb="md" title="Import failed">
          {error}
        </Alert>
      )}

      {warnings && warnings.length > 0 && (
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />} mb="md" title="Imported with warnings">
          <Stack gap={4}>
            {warnings.map((w, i) => <Text key={i} size="xs">{w}</Text>)}
          </Stack>
          <Group justify="flex-end" mt="sm">
            <Button size="xs" onClick={() => { onOpenChange(false); reset() }}>Done</Button>
          </Group>
        </Alert>
      )}

      {/* ---- Step 0: source ---------------------------------------------------------- */}
      {step === 0 && (
        <Stack gap="md">
          {/* Launched from a collection that was imported before: the fastest path is almost always
              "the same description again", so offer it as one click. */}
          {link && (
            <Alert color="tap" icon={<IconPlugConnected size={16} />} title={`“${link.slug}” is linked to a WSDL`}>
              <Stack gap={6}>
                <Text size="xs">
                  {link.url ?? link.fileName ?? 'an uploaded file'}
                  {link.serviceName && ` · ${link.serviceName}`}
                  {` · ${link.trackedOperations} tracked operation${link.trackedOperations === 1 ? '' : 's'}`}
                </Text>
                <Text size="xs" c="dimmed">
                  Imported {new Date(link.fetchedAt).toLocaleString()} as{' '}
                  {link.layout === 'http' ? 'one .http file per port' : 'one .req.tap per operation'}.
                </Text>
                {link.url && (
                  <Group>
                    <Button
                      size="xs"
                      variant="light"
                      loading={busy}
                      leftSection={<IconWorld size={14} />}
                      onClick={() => { setSourceMode('url'); setUrl(link.url!); void stageUrl(link.url!) }}
                    >
                      Fetch it again
                    </Button>
                  </Group>
                )}
              </Stack>
            </Alert>
          )}

          <SegmentedControl
            value={sourceMode}
            onChange={(v) => { setSourceMode(v as SourceMode); setError(null) }}
            data={[
              { value: 'file', label: 'Upload a file' },
              { value: 'url', label: 'Fetch a URL' },
            ]}
          />

          {sourceMode === 'file' ? (
            <Stack gap="xs">
              <Group gap="sm">
                <FileButton onChange={stageFile} accept=".wsdl,.xml,text/xml,application/xml">
                  {(props) => (
                    <Button {...props} leftSection={<IconUpload size={14} />} variant="light" loading={busy}>
                      Choose a WSDL file
                    </Button>
                  )}
                </FileButton>
                {fileName && <Text size="sm" c="dimmed">{fileName}</Text>}
              </Group>
              <Text size="xs" c="dimmed">
                WSDL 1.1, with its schemas inlined. Tap never follows a <Code fz="xs">schemaLocation</Code>{' '}
                named inside the file, so a description that imports its types externally generates
                empty payloads — save the self-contained one instead.
              </Text>
            </Stack>
          ) : (
            <Stack gap="xs">
              <TextInput
                label="Description URL"
                placeholder="https://api.example.com/service.asmx?wsdl"
                value={url}
                onChange={(e) => setUrl(e.currentTarget.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') void stageUrl() }}
                rightSection={busy ? <Loader size={14} /> : null}
              />
              <Text size="xs" c="dimmed">
                Prefer the self-contained form where the service publishes one — WCF serves it at{' '}
                <Code fz="xs">?singleWsdl</Code>, which inlines every schema that{' '}
                <Code fz="xs">?wsdl</Code> only points at.
              </Text>
              <Group justify="flex-end">
                <Button onClick={() => void stageUrl()} loading={busy} leftSection={<IconWorld size={14} />}>
                  Fetch
                </Button>
              </Group>
            </Stack>
          )}
        </Stack>
      )}

      {/* ---- Step 1: operations ------------------------------------------------------ */}
      {step === 1 && doc && (
        <Stack gap="sm">
          {issues.length > 0 && (
            <Alert
              color="yellow"
              icon={<IconAlertTriangle size={16} />}
              title={`${issues.length} thing${issues.length === 1 ? '' : 's'} to know about this description`}
            >
              <Stack gap={2}>
                {issues.slice(0, 6).map((d, i) => (
                  <Text key={i} size="xs">
                    {d.message}{d.count > 1 && <Text component="span" c="dimmed"> (×{d.count})</Text>}
                  </Text>
                ))}
                {issues.length > 6 && <Text size="xs" c="dimmed">…and {issues.length - 6} more</Text>}
                <Text size="xs" c="dimmed" mt={4}>These don’t block the import.</Text>
              </Stack>
            </Alert>
          )}

          {doc.ports.some((p) => p.hasSibling) && (
            <Text size="xs" c="dimmed">
              This service binds the same operations over both SOAP versions. Only one port is
              pre-selected — importing both writes every request twice.
            </Text>
          )}

          <Group justify="space-between">
            <TextInput
              placeholder="Filter by operation, port or action"
              leftSection={<IconSearch size={14} />}
              value={filter}
              onChange={(e) => setFilter(e.currentTarget.value)}
              style={{ flex: 1 }}
            />
            <Group gap="xs">
              <Button size="xs" variant="subtle"
                onClick={() => setSelected(new Set(doc.operations.map((o) => o.opKey)))}>
                All
              </Button>
              <Button size="xs" variant="subtle" onClick={() => setSelected(new Set())}>None</Button>
            </Group>
          </Group>

          <Text size="xs" c="dimmed">
            {selected.size} of {doc.operations.length} operations selected
          </Text>

          <ScrollArea h={380} type="auto" scrollbarSize={8}>
            <Stack gap="xs" pr="sm">
              {byPort.map(([port, ops]) => {
                const allOn = ops.every((o) => selected.has(o.opKey))
                return (
                  <Stack key={port.key} gap={4}>
                    <Group gap="xs" wrap="nowrap">
                      <Checkbox
                        size="xs"
                        checked={allOn}
                        indeterminate={!allOn && ops.some((o) => selected.has(o.opKey))}
                        onChange={() => setSelected((cur) => toggleMany(cur, ops, !allOn))}
                        label={<Text fw={600} size="sm">{port.service} / {port.port}</Text>}
                      />
                      <Badge size="xs" variant="light" color={VERSION_COLOR[port.soapVersion] ?? 'gray'}>
                        SOAP {port.soapVersion}
                      </Badge>
                      <Badge size="xs" variant="default">{port.style}</Badge>
                      <Text size="xs" c="dimmed" truncate>{port.address ?? 'no address'}</Text>
                    </Group>
                    <Stack gap={2} pl="lg">
                      {ops.map((op) => (
                        <OperationRow
                          key={op.opKey}
                          op={op}
                          checked={selected.has(op.opKey)}
                          onToggle={() => setSelected((cur) => toggleOne(cur, op.opKey))}
                        />
                      ))}
                    </Stack>
                  </Stack>
                )
              })}
              {byPort.length === 0 && (
                <Text size="sm" c="dimmed" ta="center" py="xl">No operations match that filter.</Text>
              )}
            </Stack>
          </ScrollArea>
        </Stack>
      )}

      {/* ---- Step 2: options --------------------------------------------------------- */}
      {step === 2 && doc && (
        <ScrollArea h={440} type="auto" scrollbarSize={8}>
          <Stack gap="md" pr="sm">
            <TextInput
              label="Collection name"
              description="Becomes the folder under collections/"
              value={slug}
              onChange={(e) => setSlug(e.currentTarget.value)}
              withAsterisk
            />

            <Stack gap={6}>
              <Text size="sm" fw={500}>Layout</Text>
              <Radio.Group value={layout} onChange={(v) => setLayout(v as WsdlLayout)}>
                <Stack gap="xs">
                  <LayoutOption
                    value="req" active={layout === 'req'} icon={<IconSend size={18} />}
                    title="One .req.tap per operation"
                    detail={`${fileCount(selected.size)} — structured editing, assertions, per-request docs`}
                  />
                  <LayoutOption
                    value="http" active={layout === 'http'} icon={<IconFileCode size={18} />}
                    title="One .http file per port"
                    detail={`${fileCount(countPorts(doc, selected))} — portable, opens in Visual Studio and REST Client`}
                  />
                </Stack>
              </Radio.Group>
            </Stack>

            <Divider />

            {doc.addresses.length > 0 ? (
              <Select
                label="Base URL"
                description="Each request keeps its own path, so pointing this elsewhere moves all of them"
                data={doc.addresses.map((a) => ({ value: a, label: a }))}
                value={baseUrl}
                onChange={(v) => setBaseUrl(v ?? '')}
                allowDeselect={false}
                searchable
              />
            ) : (
              <TextInput
                label="Base URL"
                description="The description declares no endpoint address"
                placeholder="https://api.example.com"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.currentTarget.value)}
              />
            )}

            <Select
              label="Auth profile"
              description="WSDL describes message-level security, not an HTTP credential — link an existing profile if the endpoint needs one"
              leftSection={<IconLock size={14} />}
              data={authOptions}
              value={authPath}
              onChange={(v) => setAuthPath(v ?? NO_AUTH)}
              allowDeselect={false}
              searchable
            />

            <Divider />

            <Stack gap={6}>
              <Checkbox
                label="Add a WS-Security UsernameToken header"
                description="Puts a <wsse:Security> header in every envelope, with the credentials as collection variables"
                checked={usernameToken}
                onChange={(e) => setUsernameToken(e.currentTarget.checked)}
              />
              {doc.wantsUsernameToken && (
                <Text size="xs" c="dimmed">
                  A policy in this description asks for a UsernameToken, so this is on by default.
                </Text>
              )}
              {usernameToken && layout === 'http' && (
                <Text size="xs" c="dimmed">
                  A <Code fz="xs">.http</Code> file has no collection variables of its own, so{' '}
                  <Code fz="xs">{'{{wsseUsername}}'}</Code> only resolves inside Tap.
                </Text>
              )}
            </Stack>

            {targetExists && (
              <Stack gap={6}>
                <Text size="sm" fw={500}>A collection named “{slug.trim()}” already exists</Text>
                <Radio.Group value={mode} onChange={(v) => setMode(v as OpenApiImportMode)}>
                  <Stack gap="xs">
                    <Radio
                      value="merge"
                      label="Add to it"
                      description="Writes these operations into the existing collection and leaves everything else alone"
                    />
                    <Radio
                      value="replace"
                      label="Replace it"
                      description="Deletes the folder first — hand-written assertions and any requests not in this description are lost"
                    />
                  </Stack>
                </Radio.Group>
              </Stack>
            )}
          </Stack>
        </ScrollArea>
      )}

      {/* ---- Footer ------------------------------------------------------------------ */}
      <Group justify="space-between" mt="lg">
        <Button variant="subtle" onClick={() => onOpenChange(false)} disabled={busy}>Cancel</Button>
        <Group gap="xs">
          {step > 0 && <Button variant="default" onClick={() => setStep(step - 1)} disabled={busy}>Back</Button>}
          {step === 1 && (
            <Button onClick={() => setStep(2)} disabled={selected.size === 0}>Next</Button>
          )}
          {step === 2 && (
            <Button onClick={runImport} loading={busy} leftSection={<IconCheck size={14} />}>
              Import {selected.size} {selected.size === 1 ? 'operation' : 'operations'}
            </Button>
          )}
        </Group>
      </Group>
    </Modal>
  )
}

function OperationRow({ op, checked, onToggle }: { op: WsdlOperation; checked: boolean; onToggle: () => void }) {
  return (
    <UnstyledButton onClick={onToggle} px={4} py={2} style={{ borderRadius: 4 }}>
      <Group gap="xs" wrap="nowrap">
        <Checkbox size="xs" checked={checked} onChange={onToggle} tabIndex={-1} />
        <Text size="xs" ff="var(--mono)" style={{ flexShrink: 0 }}>{op.name}</Text>
        {op.documentation && (
          <Text size="xs" c="dimmed" truncate style={{ flex: 1 }}>{oneLine(op.documentation)}</Text>
        )}
        {op.soapAction && (
          <Tooltip label={`SOAPAction: ${op.soapAction}`}>
            <Badge size="xs" variant="dot" color="blue">action</Badge>
          </Tooltip>
        )}
        {op.hasBody && (
          <Tooltip label={`Body: <${op.bodyElement || 'multiple elements'}> — built from the schema`}>
            <Badge size="xs" variant="light" color="gray">body</Badge>
          </Tooltip>
        )}
      </Group>
    </UnstyledButton>
  )
}

function LayoutOption(
  { value, active, icon, title, detail }:
  { value: string; active: boolean; icon: React.ReactNode; title: string; detail: string },
) {
  return (
    <UnstyledButton
      component="label"
      p="sm"
      style={{
        border: `1px solid ${active ? 'var(--mantine-color-tap-filled)' : 'var(--mantine-color-default-border)'}`,
        borderRadius: 8,
        background: active ? 'var(--mantine-color-tap-light)' : 'transparent',
      }}
    >
      <Group gap="sm" wrap="nowrap">
        <Radio value={value} />
        {icon}
        <Stack gap={2}>
          <Text size="sm" fw={600}>{title}</Text>
          <Text size="xs" c="dimmed">{detail}</Text>
        </Stack>
      </Group>
    </UnstyledButton>
  )
}

/**
 * The default selection: every operation, minus the duplicates a dual-bound service produces.
 * A .NET WSDL binds the same portType over SOAP 1.1 and 1.2, and importing both writes two of
 * every request — so of each such pair only the first port is pre-ticked.
 */
function defaultSelection(doc: WsdlDocument): Set<string> {
  const skipped = new Set<string>()
  const seen = new Set<string>()
  for (const port of doc.ports) {
    if (!port.hasSibling) continue
    // `ports` is in document order, so "the first of the pair" is stable across runs.
    const signature = `${port.service}|${port.operationCount}`
    if (seen.has(signature)) skipped.add(port.key)
    else seen.add(signature)
  }
  return new Set(doc.operations.filter((o) => !skipped.has(o.portKey)).map((o) => o.opKey))
}

/** Groups by port, matching how the importer lays the files out. */
function groupByPort(doc: WsdlDocument | null, filter: string): [WsdlPort, WsdlOperation[]][] {
  if (!doc) return []

  const needle = filter.trim().toLowerCase()
  const matches = needle
    ? doc.operations.filter((o) =>
        o.name.toLowerCase().includes(needle)
        || o.port.toLowerCase().includes(needle)
        || (o.soapAction ?? '').toLowerCase().includes(needle)
        || (o.documentation ?? '').toLowerCase().includes(needle))
    : doc.operations

  return doc.ports
    .map((port) => [port, matches.filter((o) => o.portKey === port.key)] as [WsdlPort, WsdlOperation[]])
    .filter(([, ops]) => ops.length > 0)
}

const fileCount = (n: number) => `${n} ${n === 1 ? 'file' : 'files'}`

/** Collapses repeated diagnostics into one row per distinct message, keeping document order. */
function collapse(diagnostics: WsdlDiagnostic[]): { message: string; count: number }[] {
  const counts = new Map<string, number>()
  for (const d of diagnostics) counts.set(d.message, (counts.get(d.message) ?? 0) + 1)
  return [...counts.entries()].map(([message, count]) => ({ message, count }))
}

function countPorts(doc: WsdlDocument, selected: Set<string>): number {
  return new Set(doc.operations.filter((o) => selected.has(o.opKey)).map((o) => o.portKey)).size
}

const oneLine = (value: string) => value.replace(/\s+/g, ' ').trim()

function toggleOne(current: Set<string>, key: string): Set<string> {
  const next = new Set(current)
  if (!next.delete(key)) next.add(key)
  return next
}

function toggleMany(current: Set<string>, ops: WsdlOperation[], on: boolean): Set<string> {
  const next = new Set(current)
  for (const op of ops) {
    if (on) next.add(op.opKey)
    else next.delete(op.opKey)
  }
  return next
}
