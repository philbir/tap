---
kind: collection
name: GitHub
baseUrl: https://api.github.com
defaultAuth: ../../auth/github-cli.auth.md
defaultHeaders:
  Accept: application/vnd.github+json
  User-Agent: tap-studio-demo/0.1
tags: [github, demo]
---
# GitHub

Sample requests against `api.github.com`. The collection defaults to the
`github-cli` auth profile — every call below picks up whichever account
`gh auth login` is signed in as on this machine.

- **profile.req.md** — `GET /user`, the authenticated user's profile
- **repos.req.md**   — `GET /user/repos`, repos the authenticated user can access

Switch accounts with `gh auth switch`, then execute the auth profile again from
the editor to refresh the cached token.

`X-GitHub-Api-Version: 2022-11-28` is added automatically by the GitHub auth runner;
no need to set it here.
