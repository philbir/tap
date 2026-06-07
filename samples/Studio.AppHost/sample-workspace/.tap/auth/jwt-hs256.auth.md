---
kind: auth
name: JWT (HS256)
type: jwt
algorithm: HS256
issuer: tap-studio
audience: demo-api
subject: alice
key: '{{JWT_SECRET}}'
expiresIn: 600
payload: "{\n  \"scope\": \"read:items write:items\",\n  \"roles\": [\"demo-user\"],\n  \"tenant\": \"studio-demo\"\n}\n"
tags: [jwt, demo]
---
