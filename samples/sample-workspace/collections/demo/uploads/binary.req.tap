---
kind: request
name: Upload — Binary
tags: [demo, upload, binary]
---
# Binary upload

The **Binary** body mode posts file bytes with a chosen `Content-Type`. The file
lives in a sideband `.files/` directory next to this request — the body just
holds a ref string. At send time, Tap Studio resolves the ref, reads the file
off disk, and ships the bytes verbatim as `ByteArrayContent`. Demo.Api echoes
the byte length and a SHA-256 so byte-perfect proxying through a tap can be
verified end-to-end.

```http
POST /demo/upload/raw
Content-Type: application/octet-stream

< ./.files/hello.bin
```
