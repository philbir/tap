import { useEffect, useState } from 'react'

export interface AgentStatus {
  /** Agent access is switched on for this inspector run. */
  enabled: boolean
  /** An agent read recently, or is parked on wait_for_request right now. */
  attached: boolean
  /** Tool calls and REST reads served since the inspector started. */
  reads: number
  /** Agents currently blocked on wait_for_request. */
  waiting: number
  lastReadAt: string | null
}

/**
 * Polls /api/agent-status so the header can show that an agent is reading captured traffic.
 *
 * With Scope=all an enabled agent sees the whole ring, so this indicator is the consent
 * story — not a checkbox someone ticked once. That is why it polls on a short interval while
 * enabled rather than fetching once: a counter you can watch tick is the point.
 *
 * When agent access is off, nothing can change without restarting the inspector, so the poll
 * stops after the first answer.
 */
export function useAgentStatus() {
  const [status, setStatus] = useState<AgentStatus | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | null = null

    const fetchOnce = async () => {
      try {
        const r = await fetch('/api/agent-status', { signal: controller.signal })
        if (!r.ok) return
        const s = (await r.json()) as AgentStatus
        setStatus(s)
        if (s.enabled) timer = setTimeout(fetchOnce, 3000)
      } catch {
        /* network blip; the next mount retries */
      }
    }
    fetchOnce()

    return () => {
      controller.abort()
      if (timer) clearTimeout(timer)
    }
  }, [])

  return status
}
