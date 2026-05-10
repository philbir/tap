import { useEffect, useState } from 'react'
import type { InspectorConfig } from './types'

/**
 * Fetches /api/config and re-polls every 4s while at least one ingress entry is missing
 * its hostname/publicUrl. Quick tunnels and Tailscale both fill those in asynchronously
 * (cloudflared logs / MagicDNS registration); polling means the QR page and tunnel chips
 * "just appear" without a manual refresh. Stops polling once everything is resolved.
 */
export function useInspectorConfig() {
  const [config, setConfig] = useState<InspectorConfig | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | null = null

    const fetchOnce = async () => {
      try {
        const r = await fetch('/api/config', { signal: controller.signal })
        const c = (await r.json()) as InspectorConfig
        setConfig(c)
        const needsMore = c.ingress.some((e) => !e.hostname || !e.publicUrl)
        if (needsMore) timer = setTimeout(fetchOnce, 4000)
      } catch {
        /* network blip; let SSE / next mount retry */
      }
    }
    fetchOnce()

    return () => {
      controller.abort()
      if (timer) clearTimeout(timer)
    }
  }, [])

  return config
}
