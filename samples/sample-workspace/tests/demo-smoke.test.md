---
kind: test
name: Demo API smoke
vars:
  user.name: smoke-runner
tests:
- name: Verbs echo
  request: ../collections/demo/methods/01-get.req.md
- name: POST round-trips its body
  request: ../collections/demo/methods/02-post.req.md
- name: JSON content type
  request: ../collections/demo/content/json.req.md
- name: A missing page is a 404
  request: ../collections/demo/content/status-404.req.md
- name: Book add-and-read
  flow: ./graphql-book.flow.md
  assertions:
  - jsonpath: $.data.book.author
    equals: Fred Brooks
tags: [demo, smoke]
---

# Demo API smoke

Everything here already existed — these are the same requests the Requests tab sends,
with the assertions they already carry. A test set adds three things on top:

- **`vars:`** — `user.name` is set once for the whole run and overrides every file scope,
  so the echo requests report `smoke-runner` no matter which environment is selected.
- **A flow as a test.** The last entry runs `graphql-book.flow.md` end to end. Its own
  assertion checks the *last* step's response, which is what a caller of the flow sees.
- **`onFailure`** — left at the default `continue`, so a broken endpoint doesn't hide
  the state of the other four. Set it to `stop` for a set whose entries build on each
  other.

Run it with the ▶ button in the Testing tab. Each row expands to the request that ran,
its assertions, and anything a flow step bound along the way.
