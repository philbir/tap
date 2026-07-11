---
kind: request
name: My repos (GET /user/repos)
tags: [github, repos]
---

```http
GET /user/repos?per_page=20&sort=updated
```

# Repos the authenticated user can access

`GET /user/repos` lists repositories the authenticated user has explicit access
to (owned, collaborator, or via org membership). Inherits the `github-cli` auth
from `apis/github.api.md`.

Useful query params:

- `per_page` — page size (max 100; default 30)
- `sort`     — `created` | `updated` | `pushed` | `full_name`
- `affiliation` — comma-separated: `owner,collaborator,organization_member`
- `visibility` — `all` | `public` | `private`

The token scope matters: classic PATs need `repo` to see private repos; the gh
CLI default scopes include `repo`, so this Just Works out of the box. With a
fine-grained token check the token's repository access list.
