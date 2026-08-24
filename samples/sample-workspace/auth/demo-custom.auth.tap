---
kind: auth
name: Demo Custom headers
type: custom
headers:
  X-Tap-Sample: studio-demo
  X-Trace-Id: 'req-{{env:USER}}'
  Authorization: 'Bearer {{env:DEMO_BEARER_TOKEN}}'
tags:
  - demo
  - custom
---

# Demo Custom headers

Arbitrary headers, all run through the variable + secret interpolator. Useful when an
upstream needs multiple cooperating headers (e.g. a bearer + a tenant id + a trace id)
that don't map cleanly onto Tap's per-type schemas.
