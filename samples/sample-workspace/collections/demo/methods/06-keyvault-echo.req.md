---
kind: request
name: KeyVault echo (per-env vault)
tags: [demo, keyvault, env]
---

# KeyVault echo — one vault per environment

Posts `{{kv:demo-secret}}` to the echo endpoint. The `kv` alias is bound per
environment (`kv → kv-dev` in *Dev (KeyVault)*, `kv → kv-prod` in *Prod (KeyVault)*),
so the echoed value proves which vault the active environment resolved:

- Dev  → `hello-from-DEV-vault`
- Prod → `hello-from-PROD-vault`

```http
POST /demo/methods/
Content-Type: application/json

{
  "user": "{{user.name}}",
  "secretFromVault": "{{kv:demo-secret}}"
}
```
