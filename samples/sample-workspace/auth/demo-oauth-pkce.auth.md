---
kind: auth
name: Demo OAuth2 (authorization code + PKCE)
type: oauth2
flow: authorization_code_pkce
useDiscovery: true
authority: http://{{DEMO_API_URL}}
authorizeUrl: http://localhost:61836/connect/authorize
tokenUrl: http://localhost:61836/connect/token
clientId: tap-demo-public
scopes: [openid, profile, email, api, offline_access]
tags: [demo, oauth2, pkce]
---
