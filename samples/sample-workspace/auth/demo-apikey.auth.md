---
kind: auth
name: Demo API Key
type: apiKey
in: header
apiKeyName: X-API-Key
apiKeyValue: '{{env:DEMO_API_KEY}}'
tags:
  - demo
  - apiKey
---

# Demo API Key

Injects `X-API-Key: <value>` on every request. The value resolves through the
workspace's `env` provider — set `DEMO_API_KEY=…` before launching the AppHost. Switch
`in:` to `query` (or `cookie`) to put the key elsewhere.
