---
kind: request
name: GraphQL — single book (variables)
vars:
  bookId: '2'
assertions:
- status: 200
- jsonpath: $.data.book.id
  equals: '{{bookId}}'
tags: [demo, graphql, query, variables]
---
# Single book by id

Exercises GraphQL variables — the query is parameterised and the JSON body
embeds `variables`.

`bookId` defaults to `2`, so this runs standalone. It is also the hook a flow grabs:
`tests/graphql-book.flow.md` adds a book, binds the new id to `bookId`, and re-runs this
same request against it. Nothing here knows about the flow — a request stays a request.

```http
POST /graphql
Content-Type: application/json

{
  "query": "query GetBook($id: Int!) { book(id: $id) { id title author year } }",
  "variables": { "id": {{bookId}} }
}
```
