---
kind: auth
name: Azure CLI · On-Behalf-Of
type: azure-cli
flow: on_behalf_of
tenant: '{{env:AZURE_TENANT_ID}}'
userScope: 'api://{{env:AZURE_MIDTIER_APP_ID}}/access_as_user'
clientId: '{{env:AZURE_MIDTIER_APP_ID}}'
clientSecret: '{{env:AZURE_MIDTIER_APP_SECRET}}'
scopes:
  - 'api://{{env:AZURE_DOWNSTREAM_APP_ID}}/.default'
tags:
  - azure
  - obo
---

# Azure CLI · On-Behalf-Of

Same `azure-cli` type as the basic profile, with `flow: on_behalf_of` to chain an OBO
exchange on top of the az step:

1. **az step.** `az account get-access-token --scope <userScope>` mints a user token
   targeting the middle-tier API (`AZURE_MIDTIER_APP_ID`). Tenant is pinned so the
   token is issued by the right AAD instance.
2. **OBO exchange.** Tap POSTs the user token to
   `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token` with
   `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` and the middle-tier app's
   client credentials. The token endpoint returns a downstream token whose audience is
   `AZURE_DOWNSTREAM_APP_ID`.

Required env vars (allowlist them in `tap.md`):

- `AZURE_TENANT_ID` — tenant id or domain.
- `AZURE_MIDTIER_APP_ID` — application (client) id of the middle-tier API.
- `AZURE_MIDTIER_APP_SECRET` — client secret used for the OBO exchange.
- `AZURE_DOWNSTREAM_APP_ID` — application id of the downstream API.

Run `az login` first. If the middle-tier hasn't granted itself permission to call the
downstream API, AAD returns `AADSTS65001` (consent missing).
