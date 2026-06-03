using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

public class JoinTests
{
    // dummy paths for translation-only tests
    private static IQueryable<Order> Orders => DeltaTable.Open<Order>("dummy_orders");
    private static IQueryable<User> Users => DeltaTable.Open<User>("dummy_users");
    private static IQueryable<Country> Countries => DeltaTable.Open<Country>("dummy_countries");

    // real tables for execution tests
    private static IQueryable<Order> OrdersTable => DeltaTable.Open<Order>(DeltaTestData.OrdersTable);
    private static IQueryable<User> UsersTable => DeltaTable.Open<User>(DeltaTestData.UsersTable);
    private static IQueryable<Country> CountriesTable => DeltaTable.Open<Country>(DeltaTestData.CountriesTable);

    private static string Sql<T>(IQueryable<T> q) => JoinTranslator.Render(JoinTranslator.Parse(q.Expression));

    // ---- translation (always run) ----

    [Fact]
    public void Inner_join_renders_join_on()
    {
        var sql = Sql(Orders.Join(Users, o => o.UserId, u => u.Id, (o, u) => new { o.Id, o.Product, u.Name }));
        Assert.Contains(" JOIN ", sql);
        Assert.Contains("ON t0.\"UserId\" = t1.\"Id\"", sql);
        Assert.Contains("SELECT t0.\"Id\", t0.\"Product\", t1.\"Name\"", sql);
    }

    [Fact]
    public void Composite_key_join_renders_anded_conditions()
    {
        var sql = Sql(Orders.Join(Users,
            o => new { A = o.UserId, B = o.UserId },
            u => new { A = u.Id, B = u.Id },
            (o, u) => new { o.Id }));
        Assert.Contains("ON t0.\"UserId\" = t1.\"Id\" AND t0.\"UserId\" = t1.\"Id\"", sql);
    }

    [Fact]
    public void Tuple_result_then_where_and_select()
    {
        var sql = Sql(Orders
            .Join(Users, o => o.UserId, u => u.Id, (o, u) => new { o, u })
            .Where(x => x.u.Country == "NL")
            .Select(x => new { x.o.Id, x.u.Name }));
        Assert.Contains("SELECT t0.\"Id\", t1.\"Name\"", sql);
        Assert.Contains("t1.\"Country\" = $p0", sql);
    }

    [Fact]
    public void Order_and_take_after_join()
    {
        var sql = Sql(Orders
            .Join(Users, o => o.UserId, u => u.Id, (o, u) => new { o.Id, o.Total, u.Name })
            .OrderByDescending(r => r.Total)
            .Take(3));
        Assert.Contains("ORDER BY t0.\"Total\" DESC", sql);
        Assert.Contains("LIMIT 3", sql);
    }

    // ---- execution (skipped if the engine is unavailable) ----

    [SkippableFact]
    public async Task Inner_join_returns_matches()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var rows = await OrdersTable
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o.Id, o.Product, u.Name, u.Country })
            .ToListAsync();

        Assert.Equal(DeltaTestData.OrderRows.Length, rows.Count); // every order has a matching user
        var first = rows.Single(r => r.Id == 101);
        Assert.Equal("Alice", first.Name);
        Assert.Equal("NL", first.Country);
    }

    [SkippableFact]
    public async Task Join_with_inner_filter()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var nlOrders = await OrdersTable
            .Join(UsersTable.Where(u => u.Country == "NL"), o => o.UserId, u => u.Id, (o, u) => new { o.Id })
            .ToListAsync();

        Assert.Equal(6, nlOrders.Count); // orders by Alice(3) + Diana + Femke + Janneke
    }

    [SkippableFact]
    public async Task Join_order_by_total_descending_take()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var top = await OrdersTable
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o.Total, u.Name })
            .OrderByDescending(r => r.Total)
            .Take(2)
            .ToListAsync();

        Assert.Equal(new[] { 999.0, 199.0 }, top.Select(r => r.Total).ToArray());
    }

    [SkippableFact]
    public async Task Tuple_join_then_filter_and_project()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var rows = await OrdersTable
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o, u })
            .Where(x => x.u.Country == "NL")
            .Select(x => new { x.o.Product, x.u.Name })
            .ToListAsync();

        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Contains(r.Name, new[] { "Alice", "Diana", "Femke", "Janneke" }));
    }

    [SkippableFact]
    public async Task Count_and_first_over_join()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var count = await OrdersTable
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o.Id })
            .CountAsync();
        Assert.Equal(8, count);

        var first = await OrdersTable
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o.Id, u.Name })
            .OrderBy(r => r.Id)
            .FirstAsync();
        Assert.Equal(101, first.Id);
        Assert.Equal("Alice", first.Name);
    }

    [SkippableFact]
    public async Task Prejoin_filter_on_outer()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var big = await OrdersTable
            .Where(o => o.Total > 50)
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o.Id, o.Total })
            .ToListAsync();

        Assert.Equal(2, big.Count); // Laptop 999 + Monitor 199
    }

    // ---- aggregates over joins ----

    [SkippableFact]
    public async Task Sum_and_max_over_join()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var nlTotal = await OrdersTable
            .Join(UsersTable.Where(u => u.Country == "NL"), o => o.UserId, u => u.Id, (o, u) => new { o.Total })
            .SumAsync(x => x.Total);
        Assert.Equal(1264.48, nlTotal, 2);

        var max = await OrdersTable
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o.Total })
            .MaxAsync(x => x.Total);
        Assert.Equal(999.0, max, 2);
    }

    // ---- left join ----

    [Fact]
    public void Left_join_renders_left_join()
    {
        var sql = Sql(Users.LeftJoin(Orders, u => u.Id, o => o.UserId, (u, o) => new { u.Name, o!.Product }));
        Assert.Contains("LEFT JOIN", sql);
        Assert.Contains("ON t0.\"Id\" = t1.\"UserId\"", sql);
    }

    [SkippableFact]
    public async Task Left_join_keeps_unmatched_outer_rows()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var rows = await UsersTable
            .LeftJoin(OrdersTable, u => u.Id, o => o.UserId, (u, o) => new { u.Name, o!.Product })
            .ToListAsync();

        Assert.Equal(12, rows.Count);                       // 8 order rows + 4 users with no orders
        Assert.Equal(4, rows.Count(r => r.Product is null)); // Bram, Erik, Hana, Ivan have no orders
    }

    // ---- multi-table (3-way) join ----

    [Fact]
    public void Multi_table_join_renders_both_joins()
    {
        var sql = Sql(Orders
            .Join(Users, o => o.UserId, u => u.Id, (o, u) => new { o, u })
            .Join(Countries, ou => ou.u.Country, c => c.Code, (ou, c) => new { ou.o.Id, ou.u.Name, c.CountryName }));

        Assert.Contains("ON t0.\"UserId\" = t1.\"Id\"", sql);
        Assert.Contains("ON t1.\"Country\" = t2.\"Code\"", sql);
        Assert.Contains("SELECT t0.\"Id\", t1.\"Name\", t2.\"CountryName\"", sql);
    }

    [SkippableFact]
    public async Task Multi_table_join_execution()
    {
        Skip.IfNot(DeltaTestData.DeltaAvailable, "DuckDB delta extension unavailable.");

        var rows = await OrdersTable
            .Join(UsersTable, o => o.UserId, u => u.Id, (o, u) => new { o, u })
            .Join(CountriesTable, ou => ou.u.Country, c => c.Code, (ou, c) => new { ou.o.Id, ou.u.Name, c.CountryName, c.Region })
            .ToListAsync();

        Assert.Equal(8, rows.Count);
        var r = rows.Single(x => x.Id == 101);
        Assert.Equal("Alice", r.Name);
        Assert.Equal("Netherlands", r.CountryName);
        Assert.Equal("Europe", r.Region);
    }
}
