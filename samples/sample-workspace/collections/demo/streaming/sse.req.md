---
kind: request
name: Streaming — SSE
tags: [demo, stream, sse]
vars:
  count:
    default: "10"
    description: How many events to emit
  interval:
    default: "500"
    description: Milliseconds between events
---
# SSE — 10 events at 500 ms

The response panel auto-switches to the **Events** tab. Click × on the response
header to abort mid-stream.

```http
GET /demo/stream/sse?count={{count}}&interval={{interval}}
Accept: text/event-stream
```
