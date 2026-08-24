---
kind: request
name: Streaming — WebSocket
protocol: websocket
tags: [demo, stream, websocket]
---
# WebSocket

The linked API's `baseUrl` (`{{DEMO_API_URL}}` → `host:port`) is rendered with a
`ws://` scheme because of `protocol: websocket` above. Click **Send** to open the
connection — Studio captures each frame and shows them in the response panel.

The optional body is sent as the first text frame after the upgrade completes;
remove it to just listen for the heartbeat.

```http
GET /demo/stream/ws?interval=1000

hello
```
