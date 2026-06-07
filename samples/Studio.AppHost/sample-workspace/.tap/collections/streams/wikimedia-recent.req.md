---
kind: request
name: SSE (Wikimedia recent changes)
auth: none
tags: [content-type, sse, stream]
---
# SSE — live stream

Wikimedia's public `recentchange` stream emits one SSE frame for every edit landing on
any Wikimedia wiki. Use it to verify the live Events tab:

1. Click **Send**.
2. The Response panel auto-switches to the **Events** tab.
3. Frames stream in as they happen — click any row to expand the JSON payload.
4. Click × on the response panel header to abort the stream.

Notes:
- Wikimedia content-negotiates on `Accept` — we explicitly request `text/event-stream`,
  otherwise it falls back to a JSON document. It also requires a User-Agent.
- This is high-frequency (10–50 events / sec) — a good stress test for the live list.
- We don't link an `api:` file because the default `Accept: application/json` from
  `httpbin.api.md` would override the SSE content type negotiation.

```http
GET https://stream.wikimedia.org/v2/stream/recentchange
Accept: text/event-stream
User-Agent: tap-studio-demo/0.1 (https://github.com/philbir/tap; demo)
```
