---
kind: request
name: GET /demo/methods
tags: [demo, methods, get]
---
# GET — echo

Verbs round-trip: the handler echoes method, path, query, headers, and body. Use
this to confirm verb dispatch + header forwarding through any Tap tunnel.

```http
GET /demo/methods/?env={{user.name}}
```
