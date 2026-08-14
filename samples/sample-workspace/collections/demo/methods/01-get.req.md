---
kind: request
name: GET /demo/methods
assertions:
- status: 2xx
- header: content-type
  contains: application/json
- jsonpath: $.method
  equals: GET
- jsonpath: $.path
  equals: /demo/methods/
- jsonpath: $.query
  startsWith: '?env='
- duration:
    lt: 2000
tags: [demo, methods, get]
---
# GET — echo

Verbs round-trip: the handler echoes method, path, query, headers, and body. Use
this to confirm verb dispatch + header forwarding through any Tap tunnel.

The `assertions:` block turns this into a check rather than just a call: the echo has
to come back `2xx`, as JSON, reporting the verb and path it received, and it has to do
so quickly. Open the **Asserts** tab to edit them, and the response's **Asserts** tab
to see how each one fared.

```http
GET /demo/methods/?env={{user.name}}
```
