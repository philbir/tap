using Microsoft.EntityFrameworkCore;

namespace Demo.Api;

/// <summary>
/// EF Core context used solely by OpenIddict to persist clients and tokens. The store is
/// in-memory — the test client and user are reseeded on every boot via
/// <see cref="Demo.Api.Auth.DemoAuth.SeedAsync"/>.
/// </summary>
public sealed class DemoDbContext(DbContextOptions<DemoDbContext> options) : DbContext(options);
