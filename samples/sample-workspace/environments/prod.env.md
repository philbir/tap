---
kind: env
name: Prod (KeyVault)
defaultVariableProvider: kv-prod
providerAliases:
  kv: kv-prod
vars:
  user.name: Prod User
  user.email: prod@example.com
---

# Prod (KeyVault)

Counterpart to `dev.env.md`: the same `kv` alias, bound to the **tap-studio-01-prod**
Key Vault. Switching the Studio's environment picker from Dev to Prod re-points every
`{{kv:…}}` token at the prod vault — same secret names, different values.
