---
kind: request
name: Whoami (client credentials)
auth: whoami-collection-auth.auth.md
tags: [demo, auth, oauth2, client-credentials]
---

```http
GET /demo/auth/whoami
```

# Authenticated request — client_credentials

Run **Authenticate** on the auth profile first; the resulting bearer token is
attached automatically by Tap.

The `auth:` ref above is a sibling, not a `../../auth/…` path: this request uses the
collection-scoped `whoami-collection-auth.auth.md`, which builds its token URL from the
collection's `{{IDP_URL}}` variable. `../../auth/demo-oauth-cc.auth.md` is the equivalent
workspace-scoped profile if you'd rather share one across collections.
