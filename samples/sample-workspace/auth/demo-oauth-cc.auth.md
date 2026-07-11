---
kind: auth
name: Demo OAuth2 (client credentials)
type: oauth2
flow: client_credentials
tokenUrl: http://{{DEMO_API_URL}}/connect/token
clientId: tap-demo
clientSecret: tap-demo-secret
scopes:
  - api
tags:
  - demo
  - oauth2
---

# Demo OAuth2 — client credentials

Server-to-server grant against the Demo.Api OpenIddict instance. The token URL embeds
`{{DEMO_API_URL}}` which the workspace's `env` provider resolves from the host's
`DEMO_API_URL` (allowlisted via `TAP_VARS_ALLOWED`). Provider tokens like
`{{env:NAME}}` and `{{dev:NAME}}` resolve the same way at this point.

Useful smoke test: hit **Execute** in the right pane. The runner POSTs to
`/connect/token` and returns the access token decoded inline.
