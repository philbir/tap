---
kind: auth
name: Azure CLI (Microsoft Graph)
type: azure-cli
scope: https://graph.microsoft.com/.default
tags:
  - azure
  - azure-cli
---

# Azure CLI — Microsoft Graph

Shells out to `az account get-access-token --scope https://graph.microsoft.com/.default`
and surfaces the resulting access token. Requires the user to have run `az login`
locally; Tap doesn't manage Azure credentials itself.

Swap `scope:` to `resource: https://management.azure.com/` for ARM-flavoured (v1) tokens.
