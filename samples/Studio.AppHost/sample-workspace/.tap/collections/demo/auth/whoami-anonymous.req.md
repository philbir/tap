---
kind: request
name: Whoami (no auth, expect 401)
auth: none
tags: [demo, auth, anonymous, 401]
---

```http
GET /demo/auth/whoami
```

# Unauthenticated — should return 401

`/demo/auth/whoami` requires a valid bearer token. Hitting it with `auth: none`
verifies that the OpenIddict validation handler is wired up and rejects the
request — useful as a negative test.
