---
kind: request
name: GraphQL — add book (mutation)
tags: [demo, graphql, mutation]
---

```http
POST /graphql
Content-Type: application/json

{
  "query": "mutation Add($t:String!,$a:String!,$y:Int!){ \n  addBook(title:$t, author:$a, year:$y){ id title } }",
  "variables": {
    "t": "Designing Data-Intensive Applications",
    "a": "Martin Kleppmann",
    "y": 2017
  }
}
```

# Mutation — addBook

Mutation also pushes onto the `BookAdded` subscription topic, so any live
subscriber over graphql-ws gets the new book.
