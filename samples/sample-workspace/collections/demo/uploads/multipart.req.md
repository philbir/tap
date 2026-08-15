---
kind: request
name: Upload — multipart
tags: [demo, upload, multipart]
---
# multipart/form-data

The server lists fields + file metadata back. Boundary is fixed so the request
body in this file is reproducible — Tap won't rewrite it.

```http
POST /demo/upload/multipart
Content-Type: multipart/form-data; boundary=tap-multipart-boundary

--tap-multipart-boundary
Content-Disposition: form-data; name="user"

{{user.name}}
--tap-multipart-boundary
Content-Disposition: form-data; name="file"; filename="hello.txt"
Content-Type: text/plain

Hello from a multipart upload.
--tap-multipart-boundary--
```
