---
kind: request
name: GraphQL — single book (variables)
tags: [demo, graphql, query, variables]
---
# Single book by id

Exercises GraphQL variables — the query is parameterised and the JSON body
embeds `variables`.

```http
POST /graphql
Content-Type: application/json

{
  "query": "query GetBook($id: Int!) { book(id: $id) { id title author year } }",
  "variables": { "id": 2 }
}
```
