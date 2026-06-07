---
kind: collection
name: Demo
baseUrl: '{{DEMO_API_URL}}'
defaultHeaders:
  Accept: application/json
  User-Agent: tap-studio-demo/0.1
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

Sub-folders mirror the demo surface:

- **methods/**   — every HTTP verb + a status-code playground
- **content/**   — response content-type round trips (JSON, XML, YAML, HTML, CSS,
  JS, CSV, Markdown, PNG, JPEG, SVG, binary, problem, empty, large, slow, 4xx/5xx)
- **uploads/**  — request content-type round trips (JSON, form, multipart, raw)
- **streaming/** — SSE + WebSocket
- **graphql/**  — HotChocolate queries, mutations, and the Nitro UI link
- **auth/**     — OAuth2 client-credentials and ROPC against `/demo/auth/whoami`
