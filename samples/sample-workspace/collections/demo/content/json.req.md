---
kind: request
name: Content — JSON
assertions:
- status: 200
- jsonpath: $.kind
  equals: json
- jsonpath: $.message
  matches: ^Hello from .+$
- jsonpath: $.time
  type: string
- jsonpath: $.error
  exists: false
tags: [demo, content, json]
---
# JSON response

Source-of-truth shape served by `Results.Json`. Exercises pretty-printing and
JSON syntax highlighting in the Body tab.

The assertions show the JSONPath extractor across its range: an exact value, a regex
over a value that changes every call, a type check, and an absence check.

```http
GET /demo/content/json
```
