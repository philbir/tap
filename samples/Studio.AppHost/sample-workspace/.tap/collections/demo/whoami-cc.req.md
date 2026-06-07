---
kind: request
name: Whoami (client credentials)
auth: ../../auth/demo-oauth-cc.auth.md
tags: [demo, auth, oauth2, client-credentials]
---

```http
GET /demo/auth/whoami
```

# Authenticated request — client_credentials

Run **Authenticate** on the auth profile first; the resulting bearer token is
attached automatically by Tap.
