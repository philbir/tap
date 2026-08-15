---
kind: request
name: Upload — Raw text
tags: [demo, upload, raw, text]
---

```http
POST /demo/upload/text
Content-Type: image/jpeg

< ./.files/rega_logo.jpeg
```

# Raw text body

The Studio's **Raw / Text** body mode sends the textarea contents verbatim with
`Content-Type: text/plain`. Useful for log lines, prose, hand-rolled formats.
