---
kind: auth
name: Demo OAuth2 (password / ROPC)
type: oauth2
flow: password
useDiscovery: true
authority: http://{{DEMO_API_URL}}
authorizeUrl: http://localhost:61836/connect/authorize
tokenUrl: http://localhost:61836/connect/token
clientId: tap-demo
clientSecret: tap-demo-secret
scopes: [openid, profile, email, api, offline_access]
username: alice
password: wonderland
tags: [demo, oauth2]
---
