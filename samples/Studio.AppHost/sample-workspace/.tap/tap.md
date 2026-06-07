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
vars:
  JWT_SECRET: '{{env:DEMO_JWT_SECRET}}'
tags: [demo, local, public, sample, streaming, websocket, graphql, oauth2]
---
