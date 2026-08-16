---
kind: collection
name: Demo
baseUrl: '{{DEMO_API_URL}}'
defaultHeaders:
  Accept: application/json
  User-Agent: tap-studio-demo/0.1
vars:
  OAUTH_TOKEN_PATH: /connect/token
  OAUTH_CLIENT_ID: tap-demo
tags: [demo, local]
stages:
- name: Dev
- name: uat
  baseUrl: http://q.demo.test
- name: prod
  baseUrl: http://p.demo.test
---
# Demo

Sample requests against `Demo.Api` (the local upstream registered by
`samples/Studio.AppHost`). The collection owns the baseUrl and shared headers —
every request inside inherits them automatically.

`whoami-collection-auth.auth.md` sits inside this collection rather than in the shared
`auth/` folder, so it resolves `{{OAUTH_TOKEN_PATH}}` and `{{OAUTH_CLIENT_ID}}` from the
collection vars above (and from whichever stage is selected). Compare it with
`auth/demo-oauth-cc.auth.md`, which is workspace-scoped: those two names mean nothing to
it, so it has to spell the endpoint and client out itself.

Sub-folders mirror the demo surface:

- **methods/**   — every HTTP verb + a status-code playground
- **content/**   — response content-type round trips (JSON, XML, YAML, HTML, CSS,
  JS, CSV, Markdown, PNG, JPEG, SVG, binary, problem, empty, large, slow, 4xx/5xx)
- **uploads/**  — request content-type round trips (JSON, form, multipart, raw)
- **streaming/** — SSE + WebSocket
- **graphql/**  — HotChocolate queries, mutations, and the Nitro UI link
- **auth/**     — OAuth2 client-credentials and ROPC against `/demo/auth/whoami`
