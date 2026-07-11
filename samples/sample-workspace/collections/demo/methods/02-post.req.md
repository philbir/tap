---
kind: request
name: POST /demo/methods
tags: [demo, methods, post]
---
# POST — echo

```http
POST /demo/methods/
Content-Type: application/json

{
  "user": "{{user.name}}",
  "email": "{{user.email}}",
  "verb": "POST"
}
```
