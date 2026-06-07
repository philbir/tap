using HotChocolate.Subscriptions;

namespace Demo.Api.GraphQL;

/// <summary>
/// HotChocolate GraphQL surface for the demo: a simple in-memory book store with queries,
/// mutations, and a subscription topic for newly-created books. Subscriptions ride
/// graphql-ws so the Tap WebSocket capture can pick them up.
/// </summary>
public static class DemoGraphQL
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<BookStore>();

        builder.Services
            .AddGraphQLServer()
            .AddInMemorySubscriptions()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddSubscriptionType<Subscription>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGraphQL("/graphql");
    }
}

public sealed record Book(int Id, string Title, string Author, int Year);

/// <summary>Thread-safe in-memory store. A singleton service so the GraphQL resolvers can
/// hold long-lived state without dragging in EF Core.</summary>
public sealed class BookStore
{
    private readonly List<Book> _books = new()
    {
        new(1, "The Pragmatic Programmer", "Andy Hunt", 1999),
        new(2, "Clean Code", "Robert C. Martin", 2008),
        new(3, "Refactoring", "Martin Fowler", 1999),
    };
    private int _nextId = 4;
    private readonly Lock _gate = new();

    public IReadOnlyList<Book> All()
    {
        lock (_gate) return _books.ToArray();
    }

    public Book? Find(int id)
    {
        lock (_gate) return _books.FirstOrDefault(b => b.Id == id);
    }

    public Book Add(string title, string author, int year)
    {
        lock (_gate)
        {
            var book = new Book(_nextId++, title, author, year);
            _books.Add(book);
            return book;
        }
    }
}

public sealed class Query
{
    public IReadOnlyList<Book> GetBooks([Service] BookStore store) => store.All();
    public Book? GetBook(int id, [Service] BookStore store) => store.Find(id);
    public string Hello(string? name = null) => $"Hello, {name ?? "world"}!";
}

public sealed class Mutation
{
    public async Task<Book> AddBook(string title, string author, int year,
        [Service] BookStore store,
        [Service] ITopicEventSender sender,
        CancellationToken ct)
    {
        var book = store.Add(title, author, year);
        await sender.SendAsync(nameof(Subscription.BookAdded), book, ct);
        return book;
    }
}

public sealed class Subscription
{
    [Subscribe]
    [Topic]
    public Book BookAdded([EventMessage] Book book) => book;
}
