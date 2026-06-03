using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

/// <summary>Terminal aggregate / single-element operators, async and sync.</summary>
public class AggregateTests
{
    private static IQueryable<User> Users => DeltaTable.Open<User>(DeltaTestData.UsersTable);

    [SkippableFact]
    public async Task CountAsync_total_and_filtered()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");
        Assert.Equal(10, await Users.CountAsync());
        Assert.Equal(6, await Users.CountAsync(x => x.Country == "NL"));
        Assert.Equal(6, await Users.Where(x => x.Country == "NL").CountAsync());
    }

    [SkippableFact]
    public async Task AnyAsync()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");
        Assert.True(await Users.AnyAsync(x => x.Price > 400));
        Assert.False(await Users.AnyAsync(x => x.Price > 1000));
    }

    [SkippableFact]
    public async Task Numeric_aggregates()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");
        Assert.Equal(1815.0, await Users.SumAsync(x => x.Price), 3);
        Assert.Equal(410.0, await Users.MaxAsync(x => x.Price), 3);
        Assert.Equal(60.0, await Users.MinAsync(x => x.Price), 3);
        Assert.Equal(181.5, await Users.AverageAsync(x => x.Price), 3);
    }

    [SkippableFact]
    public async Task First_and_Single()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var cheapest = await Users.OrderBy(x => x.Price).FirstAsync();
        Assert.Equal("Ivan", cheapest.Name);

        var single = await Users.SingleAsync(x => x.Id == 1);
        Assert.Equal("Alice", single.Name);

        Assert.Null(await Users.FirstOrDefaultAsync(x => x.Country == "FR"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Users.SingleAsync(x => x.Country == "NL"));
    }

    [SkippableFact]
    public void Synchronous_terminals()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");
        Assert.Equal(10, Users.Count());
        Assert.True(Users.Any(x => x.Price > 400));
        Assert.Equal("Ivan", Users.OrderBy(x => x.Price).First().Name);
    }
}
