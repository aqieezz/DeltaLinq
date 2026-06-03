using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

public class GroupByTests
{
    private static IQueryable<User> Q => DeltaTable.Open<User>("dummy");
    private static IQueryable<User> Users => DeltaTable.Open<User>(DeltaTestData.UsersTable);

    private static string Sql<T>(IQueryable<T> q)
        => QueryTranslator.RenderSelect(QueryTranslator.Parse(q.Expression), "tbl");

    // ---- translation (always run) ----

    [Fact]
    public void GroupBy_count_renders_group_by()
    {
        var sql = Sql(Q.GroupBy(x => x.Country).Select(g => new { Country = g.Key, Count = g.Count() }));
        Assert.StartsWith("SELECT \"Country\", COUNT(*)", sql);
        Assert.Contains("GROUP BY \"Country\"", sql);
    }

    [Fact]
    public void GroupBy_sum_and_average()
    {
        var sql = Sql(Q.GroupBy(x => x.Country)
            .Select(g => new { g.Key, Total = g.Sum(x => x.Price), Avg = g.Average(x => x.Price) }));
        Assert.Contains("sum(\"Price\")", sql);
        Assert.Contains("avg(\"Price\")", sql);
        Assert.Contains("GROUP BY \"Country\"", sql);
    }

    [Fact]
    public void GroupBy_composite_key()
    {
        var sql = Sql(Q.GroupBy(x => new { x.Country, x.IsActive })
            .Select(g => new { g.Key.Country, g.Key.IsActive, Count = g.Count() }));
        Assert.Contains("GROUP BY \"Country\", \"IsActive\"", sql);
    }

    [Fact]
    public void GroupBy_result_selector_form()
    {
        var sql = Sql(Q.GroupBy(x => x.Country, (key, items) => new { Country = key, Count = items.Count() }));
        Assert.StartsWith("SELECT \"Country\", COUNT(*)", sql);
        Assert.Contains("GROUP BY \"Country\"", sql);
    }

    [Fact]
    public void GroupBy_order_by_aggregate_then_take()
    {
        var sql = Sql(Q.GroupBy(x => x.Country)
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .Take(2));
        Assert.Contains("ORDER BY COUNT(*) DESC", sql);
        Assert.Contains("LIMIT 2", sql);
    }

    [Fact]
    public void Filter_on_grouping_before_select_is_rejected()
        => Assert.Throws<NotSupportedException>(() => Sql(
            Q.GroupBy(x => x.Country).Where(g => g.Count() > 1)));

    // ---- execution (skipped if the engine is unavailable) ----

    [SkippableFact]
    public async Task GroupBy_counts_per_country()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var rows = await Users.GroupBy(x => x.Country)
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .ToListAsync();

        var byCountry = rows.ToDictionary(r => r.Country, r => r.Count);
        Assert.Equal(6, byCountry["NL"]);
        Assert.Equal(2, byCountry["ES"]);
        Assert.Equal(2, byCountry["DE"]);
    }

    [SkippableFact]
    public async Task GroupBy_sum_and_average_per_country()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var nl = (await Users.GroupBy(x => x.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(x => x.Price), Avg = g.Average(x => x.Price) })
                .ToListAsync())
            .Single(r => r.Country == "NL");

        Assert.Equal(1335.0, nl.Total, 3);
        Assert.Equal(222.5, nl.Avg, 3);
    }

    [SkippableFact]
    public async Task GroupBy_composite_key_execution()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var rows = await Users.GroupBy(x => new { x.Country, x.IsActive })
            .Select(g => new { g.Key.Country, g.Key.IsActive, Count = g.Count() })
            .ToListAsync();

        Assert.Equal(5, rows.Count);
        Assert.Equal(4, rows.Single(r => r is { Country: "NL", IsActive: true }).Count);
    }

    [SkippableFact]
    public async Task GroupBy_with_prefilter_and_order()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var top = await Users.Where(x => x.IsActive)
            .GroupBy(x => x.Country)
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .Take(1)
            .ToListAsync();

        Assert.Equal("NL", top[0].Country); // 4 active NL users, the most of any country
        Assert.Equal(4, top[0].Count);
    }
}
