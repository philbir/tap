---
kind: test
name: Content types
vars:
  user.name: smoke-runner
tests:
- name: JSON
  request: ../collections/demo/content/json.req.md
- name: XML
  request: ../collections/demo/content/xml.req.md
- name: A missing page is a 404
  request: ../collections/demo/content/status-404.req.md
tags: [demo, smoke]
---

# Content types

A second tagged set, so `tap-studio test --tag smoke` has more than one file to gather —
and so the JUnit report shows what several `<testsuite>` elements look like.
