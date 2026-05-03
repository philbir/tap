import { useEffect, useState } from 'react'
import type { InspectorConfig } from './types'

export function useInspectorConfig() {
  const [config, setConfig] = useState<InspectorConfig | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    fetch('/api/config', { signal: controller.signal })
      .then((r) => r.json() as Promise<InspectorConfig>)
      .then(setConfig)
      .catch(() => {})
    return () => controller.abort()
  }, [])

  return config
}
