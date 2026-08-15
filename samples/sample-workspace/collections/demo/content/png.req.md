---
kind: request
name: Content — PNG
tags: [demo, content, image]
---
# PNG response

Tiny 3×3 red PNG. Verifies binary body capture and the inline `<img>` preview
(via a base64 `data:` URL) in the Body tab.

```http
GET /demo/content/png
```
