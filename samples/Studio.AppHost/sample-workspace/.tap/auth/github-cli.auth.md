---
kind: auth
name: GitHub CLI
type: github
mode: gh-cli
tags:
  - github
  - cli
---

# GitHub CLI

Shells out to `gh auth token` on this machine and uses the returned token as a
`Authorization: Bearer …` header. Tap also adds `X-GitHub-Api-Version: 2022-11-28`
and `Accept: application/vnd.github+json` automatically.

## Prerequisites

```shell
brew install gh        # or: winget install GitHub.cli
gh auth login          # follow the prompt; pick scopes that fit the requests
```

No fields to fill in — the runner picks up whichever account is currently active
in the GitHub CLI. Switch accounts with `gh auth switch` and re-execute the
profile to refresh.
