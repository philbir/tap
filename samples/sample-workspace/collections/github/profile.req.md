---
kind: request
name: My profile (GET /user)
tags: [github, profile]
---

```http
GET /user
```

# Authenticated user profile

`GET /user` returns the GitHub user the request is authenticated as. Uses the
`github-cli` auth inherited from `apis/github.api.md`, so this fires off whichever
account `gh auth login` last signed into.

Expect a JSON body shaped like:

```json
{
  "login": "octocat",
  "id": 1,
  "name": "monalisa octocat",
  "company": "GitHub",
  "blog": "https://github.com/blog",
  "public_repos": 2,
  "followers": 20
}
```
