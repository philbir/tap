---
kind: request
name: Upload — Raw XML
tags: [demo, upload, raw, xml]
---
# Raw XML body

The **Raw / XML** body mode ships an XML document with
`Content-Type: application/xml`. Demo.Api parses the document and echoes back
the root element name and leaf-element values.

```http
POST /demo/upload/xml
Content-Type: application/xml

<?xml version="1.0" encoding="UTF-8"?>
<order id="A-{{user.email}}">
  <customer>{{user.name}}</customer>
  <item sku="TAP-001">Tap Studio licence</item>
  <quantity>3</quantity>
</order>
```
