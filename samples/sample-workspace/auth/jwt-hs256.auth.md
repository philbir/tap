---
kind: auth
name: JWT (HS256)
type: jwt
algorithm: RS512
key: |-
  -----BEGIN PRIVATE KEY-----
  MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQCuF1yrKrGiqzZc
  Nsz6azK21eZ4nxui6j3/cdvrbfDBuNA+sS+GAukfl1V04z1Hue4Z8xryhhqcgmA4
  EPUY3WFj2Cc/vscsxitj6HGlS+cjT8oW0pJhPrJED88RhoSWXZ/0FQlPDmu9eKeS
  AIdpUPsEC7eUMddHHzID2nTcy5puNEuebE11rh9wB7oPrIKr11C+ev1bsMrarQKt
  Q3O41hmEzhTxADdAtM62zULn+htpfnOPlBe5mq692kvkbd+gNShne/GKIO5z0h2I
  YBhdm1olhJ24wnGsf4FZgK079/ba/7kp4IGEk6eONwrRmKnzXtCwMCMsoZ/Vsva+
  OFjvjELfAgMBAAECggEABgQKa7IUkH2RkUelz7qLu27+kAQbgFWAiS4q76eNVdUA
  0JQ9jSDWsnEdMA2kymvCpHD+Lh9WH/+2HWTw7H5OhUYAidJo5aD++ie5TosmCCUT
  qHkVTyUt/FpXCRq7EwYHmDyD25B10JCTSEOkffCrF5YLTDhBJlnSzh/5mZhkT2ac
  OReeno9ScPKDRc6xLR4d4lh6or+ID7VgFPbqE5tvbyDvpqEovHbhrA3BMDiHaIt8
  dXio97eaWKE8XNwLUZFNXr54Un/Z7YO/FIXe3pju4O7Lt9dqcFB6+xLuzwRDPvgE
  LisiuI41uCl6Bw1OdcLz5a1SjGXE5BBFt1fKyz7yJQKBgQDfGVxY57Zop5ke5eiV
  LG1roOg2PZaFcEIEstPXCJVUyRYke2s93/8DqGM7dBnTIw69tSssTfT/5Nfd6s4i
  ruejhw2WkM8VXNunwheWwqWz3olQ9i79L3juPpR1P4BFrbSv5msnpJoQ583nO+xr
  FQ/okiehJAOSf7PPdYOETHWDowKBgQDHw9BXVSUQtjqXP66XPyxFDYzJ6MRpWmmO
  EV3P47ho3MWRfhypMhNsiyIlfVwtvJ/iDqXO3qDqQDUIrvUe7aQxtkWH+x8u5Qs8
  Zs7Me0hMdG9gX+kNZh4poHyblxiSndKJ7ExOyK7kuVy0aesd+E8X1izClgFTlYZl
  1ctZe5EXlQKBgQCVEY8A4KKjPwky+g/0TOE3/yXpyDEhLCcQmKSvk86j/lDLQ6Qe
  jeMJgKP9f9AZod0hqUIVsmk36qLKZzAhQJkKTR0pM80KyycB6tB0Lc8mZlV+QWCY
  T/YPysTLqwh8hlqrBd0nefZvwVN1ZDbOPh6JGc9c/oFci/OLdUvRRH1o3QKBgGv9
  Ks9LM/JI0Hua7WLNv7zEimtL7YGWYqFuOex8CeCGGDeCmTPN4jo3LIpfrkj7QuMN
  UAz4xLxdYU4EZnYFuVE2W3gbBMxw/RX17a0UqAzMlLjgoDeiEIQoQbRKhfocXwg4
  OTgNvJ3gpoDREUzuQrN8EE5QkP8CQdkjLf59kf3RAoGBANuB+re4TZuLOVVIhZHl
  U6f3uqAwIw93PqdgsTBYZXmLQnyHo+R3YcTPbzVn+nFNN0Q9JagG756HMS0zDsi4
  388Nzsg9olH9qYSN9spOLjS0aHrpkBwmPApjW8YzCx6y+Y6yRuGxkJ1vFhIumysh
  HxUEM5IIP7IxtNXnsTy5uI8K
  -----END PRIVATE KEY-----
expiresIn: 600
payload: |-
  {
    "iss": "tap-studio",
    "aud": "demo-api",
    "sub": "alice",
    "scope": "read:items write:items"
  }
tags: [jwt, demo]
---
