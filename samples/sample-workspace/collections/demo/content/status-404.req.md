---
kind: request
name: Status — 404 Not Found
assertions:
- status: 404
- jsonpath: $.code
  equals: 404
- jsonpath: $.description
  ignoreCase: true
  contains: not found
tags: [demo, content, status]
---
# 404 Not Found

Returns a `404` so you can verify the yellow status pill and that the JSON
status body renders correctly.

A failing status code is not a failing request: assertions describe what this call is
*supposed* to do, and this one is supposed to 404. All three assertions pass.

```http
GET /demo/methods/status/404
```
