---
kind: request
name: GraphQL — add book (mutation)
vars:
  bookTitle: Designing Data-Intensive Applications
  bookAuthor: Martin Kleppmann
  bookYear: '2017'
assertions:
- status: 200
- jsonpath: $.errors
  exists: false
- jsonpath: $.data.addBook.title
  equals: '{{bookTitle}}'
tags: [demo, graphql, mutation]
---

```http
POST /graphql
Content-Type: application/json

{
  "query": "mutation Add($t:String!,$a:String!,$y:Int!){ \n  addBook(title:$t, author:$a, year:$y){ id title } }",
  "variables": {
    "t": "{{bookTitle}}",
    "a": "{{bookAuthor}}",
    "y": {{bookYear}}
  }
}
```

# Mutation — addBook

Mutation also pushes onto the `BookAdded` subscription topic, so any live
subscriber over graphql-ws gets the new book.

The title, author, and year come from request variables so a flow can vary them per run
— see `tests/graphql-book.flow.md`, which sets a title and then checks the book it gets
back carries it.
