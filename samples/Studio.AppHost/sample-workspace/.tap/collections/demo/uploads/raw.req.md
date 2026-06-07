---
kind: request
name: Upload — raw bytes
tags: [demo, upload, raw, binary]
---
# Raw byte upload

The server reports length + SHA-256. Useful for verifying byte-perfect proxying
through a Tap tunnel.

```http
POST /demo/upload/raw
Content-Type: application/octet-stream

SGVsbG8gZnJvbSBhIHJhdyB1cGxvYWQu
```
