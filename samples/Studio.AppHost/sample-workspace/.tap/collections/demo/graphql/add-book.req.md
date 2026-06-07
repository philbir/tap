---
kind: request
name: GraphQL — add book (mutation)
tags: [demo, graphql, mutation]
---
# Mutation — addBook

Mutation also pushes onto the `BookAdded` subscription topic, so any live
subscriber over graphql-ws gets the new book.

```http
POST /graphql
Content-Type: application/json

{
  "query": "mutation Add($t:String!,$a:String!,$y:Int!){ addBook(title:$t, author:$a, year:$y){ id title } }",
  "variables": { "t": "Designing Data-Intensive Applications", "a": "Martin Kleppmann", "y": 2017 }
}
```
