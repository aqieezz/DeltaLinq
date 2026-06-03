using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

public class HavingTests
{
    private static IQueryable<User> Q => DeltaTable.Open<User>("dummy");
    private static IQueryable<User> Users => DeltaTable.Open<User>(DeltaTestData.UsersTable);

    private static string Sql<T>(IQueryable<T> q) => QueryTranslator.RenderSelect(QueryTranslator.Parse(q.Expression), "tbl");

    [Fact]
    public void Having_renders_having_clause()
    {
        var sql = Sql(Q.GroupBy(x => x.Country)
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .Where(r => r.Count > 2));
        Assert.Contains("HAVING", sql);
        Assert.Contains("COUNT(*) > $p0", sql);
    }

    [SkippableFact]
    public async Task Having_filters_groups()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var rows = await Users.GroupBy(x => x.Country)
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .Where(r => r.Count > 2)
            .ToListAsync();

        Assert.Single(rows); // only NL has 6 (> 2); ES and DE have 2 each
        Assert.Equal("NL", rows[0].Country);
    }
}
