---
kind: request
name: Whoami (auth code + PKCE)
auth: ../../../auth/demo-oauth-pkce.auth.md
tags: [demo, auth, oauth2, pkce]
---

```http
GET /demo/auth/whoami/foo
```

# Authenticated — authorization code + PKCE

Interactive flow. The first time you hit **Execute**:

1. Tap opens a popup pointed at Demo.Api's `/connect/authorize`.
2. Sign in as `alice` / `wonderland`.
3. The popup redirects to Studio's `/api/auth/callback`, the code is exchanged,
   and the access token + refresh token land in Studio's token cache.
4. This request goes out with `Authorization: Bearer <access_token>`.

Subsequent executions reuse the cached token (silently refreshing when it's near
expiry). Hit the refresh icon in the auth's Try-It panel to force re-auth.
