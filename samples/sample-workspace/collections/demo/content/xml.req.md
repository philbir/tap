---
kind: request
name: Content — XML
assertions:
- status: 200
- header: content-type
  startsWith: application/xml
- xpath: /payload/@kind
  equals: xml
- xpath: /payload/message
  equals: Hello from Demo.Api
- regex: <time>\d{4}-\d{2}-\d{2}T
tags: [demo, content, xml]
---
# XML response

XPath 1.0 over the response body, elements and attributes alike. The last assertion
drops to a plain regex — sometimes the shape you want to pin down isn't a node.

```http
GET /demo/content/xml
Accept: application/xml
```
