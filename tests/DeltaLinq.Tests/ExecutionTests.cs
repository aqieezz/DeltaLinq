using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

/// <summary>End-to-end tests against a real Delta table via delta_scan. Skipped if the extension can't load.</summary>
public class ExecutionTests
{
    private static IQueryable<User> Users => DeltaTable.Open<User>(DeltaTestData.UsersTable);

    [SkippableFact]
    public async Task Where_orderby_returns_expected_rows()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var users = await Users
            .Where(x => x.Country == "NL" && x.SignupDate > new DateTime(2023, 1, 1))
            .OrderBy(x => x.Name)
            .ToListAsync();

        Assert.Equal(new[] { "Alice", "Diana", "Femke", "Janneke" }, users.Select(u => u.Name).ToArray());
    }

    [SkippableFact]
    public async Task Column_rename_is_materialized()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var alice = await Users.Where(x => x.Id == 1).FirstAsync();
        Assert.Equal(new DateTime(2023, 5, 1), alice.SignupDate);
        Assert.True(alice.IsActive);
    }

    [SkippableFact]
    public async Task Projection_to_anonymous_type()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var priced = await Users
            .Where(x => x.Price > 100)
            .OrderByDescending(x => x.Price)
            .Select(x => new { x.Id, x.Price })
            .ToListAsync();

        Assert.Equal(7, priced.Count);
        Assert.Equal(410.0, priced[0].Price);
        Assert.Equal(8, priced[0].Id);
    }

    [SkippableFact]
    public async Task Take_limits_rows()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");
        var top3 = await Users.OrderByDescending(x => x.Price).Take(3).ToListAsync();
        Assert.Equal(3, top3.Count);
        Assert.Equal(new[] { 410.0, 310.0, 220.0 }, top3.Select(u => u.Price).ToArray());
    }

    [SkippableFact]
    public async Task AsAsyncEnumerable_streams_all_rows()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var count = 0;
        await foreach (var _ in Users.AsAsyncEnumerable())
            count++;

        Assert.Equal(DeltaTestData.Rows.Length, count);
    }

    [SkippableFact]
    public void Synchronous_enumeration_works()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");
        var names = Users.Where(x => x.Country == "ES").OrderBy(x => x.Name).Select(x => x.Name).ToList();
        Assert.Equal(new[] { "Carlos", "Ivan" }, names);
    }
}
