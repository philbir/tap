---
kind: request
name: Upload — Multipart (multi-file)
tags: [demo, upload, multipart, multi]
---
# multipart/form-data — multiple fields and files

Exercises the **Multipart** body editor end-to-end: two text fields plus two
file parts with distinct Content-Types. Boundary is the canonical
`tap-multipart-boundary` so the on-disk diff stays clean across edits.

```http
POST /demo/upload/multipart
Content-Type: multipart/form-data; boundary=tap-multipart-boundary

--tap-multipart-boundary
Content-Disposition: form-data; name="user"

{{user.name}}
--tap-multipart-boundary
Content-Disposition: form-data; name="email"

{{user.email}}
--tap-multipart-boundary
Content-Disposition: form-data; name="readme"; filename="README.md"
Content-Type: text/markdown

# Hello
Multipart upload from Tap Studio.
--tap-multipart-boundary
Content-Disposition: form-data; name="config"; filename="config.json"
Content-Type: application/json

{"feature":"multipart","enabled":true}
--tap-multipart-boundary--
```
