import {
  ActionIcon, Alert, Badge, Box, Divider, Group, Menu, Modal, Paper, ScrollArea, Stack, Text, Tooltip,
} from '@mantine/core'
import { useClipboard } from '@mantine/hooks'
import {
  IconAlertTriangleFilled, IconBuildingBank, IconCertificate, IconCertificateOff, IconCheck,
  IconCircleCheckFilled, IconCircleXFilled, IconCopy, IconDownload, IconFingerprint,
  IconHelpCircleFilled, IconKey, IconLock, IconServer, IconShieldCheck, IconShieldX, IconWorld,
} from '@tabler/icons-react'
import type { TlsCertificate, TlsCheck, TlsDiagnosis, TlsStatus } from '../api/types'
import { downloadText, sanitizeFilenamePart } from '../shell/download'

/**
 * What the server actually presented, read back as a report rather than a stack trace.
 *
 * A failed send says one thing ("the remote certificate is invalid"); this says which
 * certificate, why, and what is fine. That split matters — the reader's next move depends on
 * whether the chain is untrusted (install a CA), expired (renew it), or for another host
 * (fix the URL), and those are three different jobs. So every fact carries its own verdict:
 * the checks up top are the summary, the cards below are the evidence, and only the part that
 * is genuinely wrong is painted red.
 */
export function TlsDiagnosisModal({ diagnosis, onClose }: { diagnosis: TlsDiagnosis | null; onClose: () => void }) {
  return (
    <Modal
      opened={diagnosis !== null}
      onClose={onClose}
      size="lg"
      scrollAreaComponent={ScrollArea.Autosize}
      title={
        <Group gap={8} wrap="nowrap">
          {diagnosis?.valid
            ? <IconShieldCheck size={18} color="var(--mantine-color-green-6)" />
            : <IconShieldX size={18} color="var(--mantine-color-red-6)" />}
          <Text fw={600}>TLS diagnosis</Text>
        </Group>
      }
    >
      {diagnosis && <DiagnosisBody diagnosis={diagnosis} />}
    </Modal>
  )
}

function DiagnosisBody({ diagnosis }: { diagnosis: TlsDiagnosis }) {
  const endpoint = diagnosis.host
    ? `${diagnosis.host}${diagnosis.port && diagnosis.port !== 443 ? `:${diagnosis.port}` : ''}`
    : diagnosis.url
  // Chain faults that a named check already speaks for would only be said twice — the checks
  // are the readable version, this list is the raw flags that didn't map to one.
  const covered = new Set(
    (diagnosis.certificates ?? []).flatMap((c) => (c.errors ?? []).map((e) => e.code)))
  const unmapped = diagnosis.errors.filter((e) => !covered.has(e.code))

  return (
    <Stack gap="md">
      <Group gap="xs" wrap="wrap">
        <Group gap={6} wrap="nowrap">
          <IconServer size={14} color="var(--mantine-color-dimmed)" />
          <Text size="sm" ff="var(--mono)">{endpoint}</Text>
        </Group>
        {diagnosis.protocol && (
          <Badge size="sm" variant="light" color="gray" leftSection={<IconLock size={10} />}>
            {diagnosis.protocol}
          </Badge>
        )}
        {diagnosis.cipherSuite && (
          <Tooltip label="Negotiated cipher suite" withArrow>
            <Badge size="sm" variant="light" color="gray" ff="var(--mono)">{diagnosis.cipherSuite}</Badge>
          </Tooltip>
        )}
        {diagnosis.handshakeMs !== null && (
          <Text size="xs" c="dimmed">{diagnosis.handshakeMs} ms</Text>
        )}
      </Group>

      <Alert
        variant="light"
        color={diagnosis.valid ? 'green' : 'red'}
        icon={diagnosis.valid ? <IconCircleCheckFilled size={16} /> : <IconCircleXFilled size={16} />}
        title={diagnosis.valid ? 'Certificate validation passed' : 'Certificate validation failed'}
        p="sm"
      >
        {diagnosis.error
          ? <Text size="sm">{diagnosis.error}</Text>
          : diagnosis.valid
            ? <Text size="sm">This host's certificate chain is trusted, in date, and issued for this hostname.</Text>
            : <Text size="sm">The connection completed, so the chain below is what the server really sent — one of these checks is why a send would be refused.</Text>}
      </Alert>

      {diagnosis.checks && diagnosis.checks.length > 0 && (
        <Paper withBorder radius="md" p="sm">
          <Stack gap={8}>
            {diagnosis.checks.map((check) => <CheckRow key={check.id} check={check} />)}
          </Stack>
        </Paper>
      )}

      {unmapped.length > 0 && (
        <Stack gap={4}>
          {unmapped.map((status) => <StatusRow key={status.code} status={status} />)}
        </Stack>
      )}

      {diagnosis.certificates.length > 0 && (
        <Box>
          <Group justify="space-between" align="center" mb={6} wrap="nowrap">
            <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
              Certificate chain
            </Text>
            <ChainDownloadMenu diagnosis={diagnosis} />
          </Group>
          <Stack gap="sm">
            {diagnosis.certificates.map((certificate, i) => (
              <CertificateCard
                key={`${certificate.thumbprint}-${i}`}
                certificate={certificate}
                last={i === diagnosis.certificates.length - 1}
                fileBase={fileBase(diagnosis)}
              />
            ))}
          </Stack>
        </Box>
      )}
    </Stack>
  )
}

const CHECK_ICON: Record<TlsCheck['state'], { color: string; node: React.ReactNode }> = {
  ok: { color: 'green', node: <IconCircleCheckFilled size={15} color="var(--mantine-color-green-6)" /> },
  fail: { color: 'red', node: <IconCircleXFilled size={15} color="var(--mantine-color-red-6)" /> },
  warn: { color: 'yellow', node: <IconAlertTriangleFilled size={14} color="var(--mantine-color-yellow-6)" /> },
  unknown: { color: 'gray', node: <IconHelpCircleFilled size={15} color="var(--mantine-color-gray-5)" /> },
}

function CheckRow({ check }: { check: TlsCheck }) {
  const icon = CHECK_ICON[check.state] ?? CHECK_ICON.unknown
  return (
    <Group gap={8} wrap="nowrap" align="flex-start">
      <Box mt={2} style={{ lineHeight: 0 }}>{icon.node}</Box>
      <Box style={{ minWidth: 0 }}>
        <Text size="sm" fw={500} c={check.state === 'ok' ? undefined : icon.color}>{check.label}</Text>
        {check.detail && <Text size="xs" c="dimmed">{check.detail}</Text>}
      </Box>
    </Group>
  )
}

/** A raw chain flag, kept as `Code: sentence` so the searchable name survives next to the prose. */
function StatusRow({ status }: { status: TlsStatus }) {
  return (
    <Group gap={8} wrap="nowrap" align="flex-start">
      <Box mt={2} style={{ lineHeight: 0 }}><IconCircleXFilled size={15} color="var(--mantine-color-red-6)" /></Box>
      <Text size="sm" c="red">
        <Text component="span" fw={600} ff="var(--mono)" fz="xs">{status.code}</Text>
        {status.description ? ` — ${status.description}` : ''}
      </Text>
    </Group>
  )
}

function CertificateCard(
  { certificate, last, fileBase }: { certificate: TlsCertificate; last: boolean; fileBase: string },
) {
  const clipboard = useClipboard({ timeout: 1200 })
  const errors = certificate.errors ?? []
  const bad = errors.length > 0
  const expiry = describeExpiry(certificate)
  const role = certificateRole(certificate, last)

  return (
    <Paper
      withBorder
      radius="md"
      p="sm"
      style={{ borderColor: bad ? 'var(--mantine-color-red-5)' : undefined }}
    >
      <Stack gap={6}>
        <Group gap={8} wrap="nowrap" align="flex-start">
          <Box mt={2} style={{ lineHeight: 0 }}>
            {bad
              ? <IconCertificateOff size={16} color="var(--mantine-color-red-6)" />
              : <IconCertificate size={16} color="var(--mantine-color-dimmed)" />}
          </Box>
          <Box style={{ flex: 1, minWidth: 0 }}>
            <Group gap={6} wrap="nowrap" align="baseline">
              <Text size="sm" fw={600} style={{ wordBreak: 'break-word' }}>
                {certificate.commonName ?? certificate.subject}
              </Text>
              <Badge size="xs" variant="light" color={role.color} leftSection={role.icon}>{role.label}</Badge>
            </Group>
            {certificate.commonName && certificate.subject !== certificate.commonName && (
              <Text size="xs" c="dimmed" style={{ wordBreak: 'break-word' }}>{certificate.subject}</Text>
            )}
          </Box>
        </Group>

        {errors.length > 0 && (
          <Stack gap={2}>
            {errors.map((error) => <StatusRow key={error.code} status={error} />)}
          </Stack>
        )}

        <Divider />

        <Stack gap={4}>
          <Field label="Issuer" icon={<IconBuildingBank size={12} />}>
            {certificate.selfSigned ? 'Self-signed' : certificate.issuer}
          </Field>

          <Group gap={6} wrap="nowrap" align="flex-start">
            <Box mt={3} style={{ lineHeight: 0, width: 12 }}>
              {expiry.state === 'ok'
                ? <IconCircleCheckFilled size={12} color="var(--mantine-color-green-6)" />
                : expiry.state === 'warn'
                  ? <IconAlertTriangleFilled size={11} color="var(--mantine-color-yellow-6)" />
                  : <IconCircleXFilled size={12} color="var(--mantine-color-red-6)" />}
            </Box>
            <Text size="xs" c="dimmed" style={{ flex: 1 }}>
              {formatDate(certificate.notBefore)} → {formatDate(certificate.notAfter)}
              {' '}
              <Text component="span" c={expiry.state === 'ok' ? 'green' : expiry.state === 'warn' ? 'yellow' : 'red'} fw={500}>
                ({expiry.label})
              </Text>
            </Text>
          </Group>

          {certificate.dnsNames && certificate.dnsNames.length > 0 && (
            <Field label="Names" icon={<IconWorld size={12} />}>
              <Group gap={4} wrap="wrap">
                {certificate.dnsNames.slice(0, 8).map((name) => (
                  <Badge key={name} size="xs" variant="default" ff="var(--mono)" tt="none">{name}</Badge>
                ))}
                {certificate.dnsNames.length > 8 && (
                  <Text size="xs" c="dimmed">+{certificate.dnsNames.length - 8} more</Text>
                )}
              </Group>
            </Field>
          )}

          {(certificate.keyAlgorithm || certificate.signatureAlgorithm) && (
            <Field label="Key" icon={<IconKey size={12} />}>
              {[
                certificate.keyAlgorithm && `${certificate.keyAlgorithm}${certificate.keySizeBits ? ` ${certificate.keySizeBits}-bit` : ''}`,
                certificate.signatureAlgorithm && `signed with ${certificate.signatureAlgorithm}`,
              ].filter(Boolean).join(' · ')}
            </Field>
          )}

          <Group gap={6} wrap="nowrap" align="center">
            <Box style={{ lineHeight: 0, width: 12 }}><IconFingerprint size={12} color="var(--mantine-color-dimmed)" /></Box>
            <Text size="xs" ff="var(--mono)" c="dimmed" style={{ flex: 1, wordBreak: 'break-all' }}>
              {certificate.thumbprint}
            </Text>
            <Tooltip label={clipboard.copied ? 'Copied' : 'Copy thumbprint'} withArrow>
              <ActionIcon
                size="sm"
                variant="subtle"
                color={clipboard.copied ? 'green' : 'gray'}
                onClick={() => clipboard.copy(certificate.thumbprint)}
                aria-label="Copy thumbprint"
              >
                {clipboard.copied ? <IconCheck size={12} /> : <IconCopy size={12} />}
              </ActionIcon>
            </Tooltip>
            {certificate.pem && (
              <Tooltip label={`Download this ${role.label.toLowerCase()} certificate (.pem)`} withArrow>
                <ActionIcon
                  size="sm"
                  variant="subtle"
                  color="gray"
                  onClick={() => downloadPem([certificate], certificateFileName(fileBase, certificate, last))}
                  aria-label="Download certificate"
                >
                  <IconDownload size={12} />
                </ActionIcon>
              </Tooltip>
            )}
          </Group>
        </Stack>
      </Stack>
    </Paper>
  )
}

// ---- Saving what the server presented -------------------------------------------------

/**
 * The three shapes a certificate is actually wanted in. A single card's button covers "give me
 * that one"; this menu covers the two whole-chain forms, which differ by exactly one entry and
 * are wanted for opposite reasons — the full chain to hand someone what the server sends, the
 * issuers alone to trust a private CA without pinning the leaf that rotates every 90 days.
 */
function ChainDownloadMenu({ diagnosis }: { diagnosis: TlsDiagnosis }) {
  const exportable = diagnosis.certificates.filter((c) => c.pem)
  const issuers = exportable.filter((c) => c.index > 0)
  // Nothing encodable means nothing to offer — a disabled menu would only pose a question
  // whose answer is "this chain didn't survive being re-encoded".
  if (exportable.length === 0) return null
  const base = fileBase(diagnosis)

  return (
    <Menu shadow="md" position="bottom-end" withinPortal width={250}>
      <Menu.Target>
        <Tooltip label="Download certificates" withArrow>
          <ActionIcon variant="subtle" color="gray" size="sm" aria-label="Download certificates">
            <IconDownload size={15} />
          </ActionIcon>
        </Tooltip>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>Download as PEM</Menu.Label>
        <Menu.Item
          leftSection={<IconCertificate size={14} />}
          onClick={() => downloadPem(exportable, `${base}-chain.pem`)}
        >
          Full chain ({exportable.length})
        </Menu.Item>
        <Menu.Item
          leftSection={<IconBuildingBank size={14} />}
          disabled={issuers.length === 0}
          onClick={() => downloadPem(issuers, `${base}-issuers.pem`)}
        >
          Issuers only, no leaf ({issuers.length})
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  )
}

/** Concatenated PEM blocks, one per certificate, leaf-first — the order every tool that reads
 *  a bundle expects. Each block is re-terminated because a missing final newline is the
 *  classic way a two-certificate file parses as one. */
function downloadPem(certificates: TlsCertificate[], filename: string): void {
  const pem = certificates.map((c) => `${c.pem!.trim()}\n`).join('')
  downloadText(pem, filename, 'application/x-pem-file')
}

/** Where in the chain this certificate sits — the badge on the card and the name of the file
 *  it downloads as, decided once so the two can't drift apart. */
function certificateRole(certificate: TlsCertificate, last: boolean): { label: string; color: string; icon: React.ReactNode } {
  if (certificate.index === 0) return { label: 'Leaf', color: 'tap', icon: <IconWorld size={12} /> }
  if (last && certificate.selfSigned) return { label: 'Root', color: 'gray', icon: <IconBuildingBank size={12} /> }
  return { label: 'Intermediate', color: 'gray', icon: <IconCertificate size={12} /> }
}

/** `example.com-leaf.pem` / `example.com-intermediate-1.pem` / `example.com-root.pem`. The
 *  chain position is in the name because saving two intermediates from the same host is the
 *  normal case, and `example.com.pem (1)` says nothing about which one it is. */
function certificateFileName(base: string, certificate: TlsCertificate, last: boolean): string {
  const role = certificateRole(certificate, last).label.toLowerCase()
  return `${base}-${role === 'intermediate' ? `${role}-${certificate.index}` : role}.pem`
}

/** Filename stem for everything saved off this report: the host, reduced to characters a
 *  filesystem will take. */
function fileBase(diagnosis: TlsDiagnosis): string {
  return sanitizeFilenamePart(diagnosis.host ?? diagnosis.url) || 'certificate'
}

function Field({ label, icon, children }: { label: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <Group gap={6} wrap="nowrap" align="flex-start">
      <Tooltip label={label} withArrow>
        <Box mt={3} style={{ lineHeight: 0, width: 12, color: 'var(--mantine-color-dimmed)' }}>{icon}</Box>
      </Tooltip>
      {/* `component="div"`: the Names field puts badges in here, and a <div> inside a <p> is
          invalid HTML — React says so loudly, and the browser silently reparents it. */}
      <Text component="div" size="xs" c="dimmed" style={{ flex: 1, minWidth: 0, wordBreak: 'break-word' }}>
        {children}
      </Text>
    </Group>
  )
}

/** "expired 11 years ago" / "expires in 87 days" — the fact people actually read a validity
 *  window for. Rendered next to the absolute dates, never instead of them. */
function describeExpiry(certificate: TlsCertificate): { state: 'ok' | 'warn' | 'fail'; label: string } {
  const now = Date.now()
  const notBefore = Date.parse(certificate.notBefore)
  const notAfter = Date.parse(certificate.notAfter)
  if (Number.isNaN(notAfter)) return { state: 'ok', label: 'validity unknown' }
  if (now > notAfter) return { state: 'fail', label: `expired ${relative(notAfter - now)}` }
  if (!Number.isNaN(notBefore) && now < notBefore) return { state: 'fail', label: `not valid until ${relative(notBefore - now)}` }
  const days = (notAfter - now) / 86_400_000
  return { state: days <= 14 ? 'warn' : 'ok', label: `expires ${relative(notAfter - now)}` }
}

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 31_536_000_000],
  ['month', 2_592_000_000],
  ['day', 86_400_000],
  ['hour', 3_600_000],
  ['minute', 60_000],
]

function relative(deltaMs: number): string {
  const format = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })
  for (const [unit, ms] of UNITS) {
    if (Math.abs(deltaMs) >= ms) return format.format(Math.round(deltaMs / ms), unit)
  }
  return format.format(Math.round(deltaMs / 1000), 'second')
}

function formatDate(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleDateString()
}
