import type { AgentStatus } from './useAgentStatus'

/**
 * Shows that a coding agent can read this inspector's traffic, and whether it is doing so
 * right now.
 *
 * Deliberately visible rather than dismissible. Agent access is opt-in per inspector, but the
 * opt-in happens once, in an AppHost or an environment variable, and possibly not by the
 * person now looking at the screen. A live counter answers "is something reading this?" at a
 * glance, which a consent dialog from last Tuesday does not.
 *
 * Three states, because they mean different things: switched on but idle, actively reading,
 * and parked on wait_for_request — the last one meaning an agent is expecting you to go and
 * make something happen.
 */
export function AgentChip({ status }: { status: AgentStatus | null }) {
  if (!status?.enabled) return null

  const watching = status.waiting > 0
  const active = watching || status.attached

  const label = watching
    ? status.waiting > 1
      ? `agent waiting ×${status.waiting}`
      : 'agent waiting'
    : status.attached
      ? `agent reading · ${status.reads}`
      : 'agent access on'

  return (
    <div
      data-testid="agent-chip"
      title={
        'A coding agent can read this inspector’s captured traffic through /api/agent/* or /mcp.\n\n' +
        `Reads served: ${status.reads}` +
        (status.lastReadAt ? `\nLast read: ${new Date(status.lastReadAt).toLocaleTimeString()}` : '') +
        (watching ? `\nWaiting for matching traffic: ${status.waiting}` : '') +
        '\n\nWhat it sees is redacted — credentials are masked and cannot be revealed to it ' +
        'by any tool. You still see the real values here.'
      }
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: '6px',
        fontSize: '11px',
        padding: '2px 8px',
        borderRadius: '999px',
        whiteSpace: 'nowrap',
        border: `1px solid ${active ? 'var(--accent)' : 'var(--border)'}`,
        color: active ? 'var(--accent)' : 'var(--text-muted)',
        background: 'var(--bg)',
      }}
    >
      <span
        aria-hidden
        style={{
          width: 6,
          height: 6,
          borderRadius: '50%',
          background: active ? 'var(--accent)' : 'var(--text-muted)',
          animation: watching ? 'tap-agent-pulse 1.4s ease-in-out infinite' : undefined,
        }}
      />
      {label}
    </div>
  )
}
