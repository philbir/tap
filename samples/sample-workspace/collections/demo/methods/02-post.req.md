---
kind: request
name: POST /demo/methods
assertions:
- status:
    in: [200, 201]
- jsonpath: $.method
  equals: POST
- jsonpath: $.contentType
  startsWith: application/json
- jsonpath: $.body
  contains: '{{user.name}}'
tags: [demo, methods, post]
---
# POST — echo

The last assertion is the interesting one: `{{user.name}}` is expanded on the expected
side using the same cascade that built the request body, so the check reads "the echo
came back carrying whatever we actually sent" without hard-coding the value.

```http
POST /demo/methods/
Content-Type: application/json

{
  "user": "{{user.name}}",
  "email": "{{user.email}}",
  "verb": "POST"
}
```
