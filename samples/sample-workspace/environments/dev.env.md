---
kind: env
name: Dev (KeyVault)
defaultVariableProvider: kv-dev
providerAliases:
  kv: kv-dev
vars:
  user.name: Dev User
  user.email: dev@example.com
---

# Dev (KeyVault)

One-vault-per-environment demo: while this env is active, the `kv` alias points at the
**tap-studio-01-dev** Key Vault, so `{{kv:demo-secret}}` resolves the dev value. Prod
binds the same alias to `tap-studio-01-prod` — requests never change, only the selected
environment.

`strictVariables` is deliberately off here: the demo collection's `{{DEMO_API_URL}}`
lives in the host `env` provider, and strict mode would stop bare tokens from falling
through past the default provider.
