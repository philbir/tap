---
kind: workspace
id: 0192-3a4c-bb71-7c1d-9e8f0a1b2c3d
name: studio-demo
defaultEnv: environments/local.env.md
defaultVariableProvider: file
variableProviders:
- name: env
  type: env
- name: file
  type: file
- name: kv-dev
  type: azkv
  settings:
    vaultName: tap-studio-01-dev
    tenantId: f6acae55-feb3-4c9a-8a7d-53c383c9fbe8
- name: kv-prod
  type: azkv
  settings:
    vaultName: tap-studio-01-prod
    tenantId: f6acae55-feb3-4c9a-8a7d-53c383c9fbe8
- name: 1password
  type: 1password
  settings:
    mode: vault
    vault: Development
vars:
  JWT_SECRET: '{{env:DEMO_JWT_SECRET}}'
tags: [demo, local, public, sample, streaming, websocket, graphql, oauth2]
---
