---
kind: auth
name: Demo OAuth2 (collection-scoped)
type: oauth2
flow: client_credentials
tokenUrl: 'http://{{DEMO_API_URL}}{{OAUTH_TOKEN_PATH}}'
clientId: '{{OAUTH_CLIENT_ID}}'
clientSecret: tap-demo-secret
scopes:
  - api
tags:
  - demo
  - oauth2
---

# Demo OAuth2 — collection-scoped

Same client-credentials grant as `auth/demo-oauth-cc.auth.md`, but this file lives
*inside* the Demo collection instead of the shared `auth/` folder. That's what lets it
reference `{{OAUTH_TOKEN_PATH}}` and `{{OAUTH_CLIENT_ID}}` — variables defined on
`_collection.md` next door. A profile under `auth/` only sees workspace + environment
variables, so those two names would fail to resolve there.

(`{{DEMO_API_URL}}` is different: it comes from the `env` variable provider, which every
profile can reach regardless of scope.)

The practical payoff shows up with stages: override `OAUTH_CLIENT_ID` on the `uat` or
`prod` stage and this profile follows, with no edit here. Tokens are cached per stage, so
a `dev` token is never handed to a `prod` request.

Hit **Execute** in the right pane to try it — the runner POSTs to `/connect/token` and
decodes the access token inline.
