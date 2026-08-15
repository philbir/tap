---
kind: request
name: Upload — JSON
tags: [demo, upload, json]
---
# JSON upload

Echoes the parsed JSON back so you can verify the request-body capture.

```http
POST /demo/upload/json
Content-Type: application/json

{
  "user": "{{user.name}}",
  "email": "{{user.email}}",
  "items": [1, 2, 3]
}
```
