using DeltaLinq;
using DeltaLinq.Sample.Api;

var builder = WebApplication.CreateBuilder(args);

// Seed a demo Delta table in the temp directory (a real app would skip this and point at its own table).
var tablePath = SampleData.EnsureCreated();

// Register DeltaLinq. For cloud storage, set options.Storage = new S3StorageOptions { ... } etc.
builder.Services.AddDeltaLake(options =>
{
    options.OnSql = sql => Console.WriteLine($"[DeltaLinq SQL] {sql}");
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    message = "DeltaLinq sample API",
    table = tablePath,
    try_these = new[]
    {
        "/users",
        "/users?country=NL&minPrice=150",
        "/users/1",
        "/users/count",
        "/stats/by-country"
    }
}));

// Filter + sort + page, with optional query-string parameters pushed down to SQL.
app.MapGet("/users", async (IDeltaTableFactory delta, string? country, double? minPrice, int? take) =>
{
    var query = delta.Open<User>(tablePath);
    if (country is not null) query = query.Where(u => u.Country == country);
    if (minPrice is not null) query = query.Where(u => u.Price >= minPrice.Value);

    var users = await query.OrderBy(u => u.Name).Take(take ?? 100).ToListAsync();
    return Results.Ok(users);
});

// Append a new user (write a commit).
app.MapPost("/users", async (IDeltaTableFactory delta, User user) =>
{
    await delta.AppendAsync(tablePath, new[] { user });
    return Results.Created($"/users/{user.Id}", user);
});

// Single row.
app.MapGet("/users/{id:long}", async (IDeltaTableFactory delta, long id) =>
{
    var user = await delta.Open<User>(tablePath).FirstOrDefaultAsync(u => u.Id == id);
    return user is null ? Results.NotFound() : Results.Ok(user);
});

// Aggregate.
app.MapGet("/users/count", async (IDeltaTableFactory delta) =>
    Results.Ok(new { count = await delta.Open<User>(tablePath).CountAsync() }));

// GroupBy + grouped aggregates.
app.MapGet("/stats/by-country", async (IDeltaTableFactory delta) =>
{
    var stats = await delta.Open<User>(tablePath)
        .GroupBy(u => u.Country)
        .Select(g => new
        {
            Country = g.Key,
            Count = g.Count(),
            Total = g.Sum(x => x.Price),
            Average = g.Average(x => x.Price)
        })
        .OrderByDescending(r => r.Count)
        .ToListAsync();
    return Results.Ok(stats);
});

app.Run();
