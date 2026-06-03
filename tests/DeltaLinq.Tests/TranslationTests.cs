using System.Linq.Expressions;
using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

/// <summary>
/// Pure translation tests — assert the generated SQL (and bound parameters) directly via the
/// internal translator. No engine or network required, so these always run.
/// </summary>
public class TranslationTests
{
    private static IQueryable<User> Q => DeltaTable.Open<User>("dummy");

    private static (string Sql, IReadOnlyList<object?> Params) Build<T>(IQueryable<T> q)
    {
        var model = QueryTranslator.Parse(q.Expression);
        var sql = QueryTranslator.RenderSelect(model, "tbl");
        return (sql, model.Parameters.Items.Select(i => i.Value).ToList());
    }

    [Fact]
    public void Where_equality_is_parameterized()
    {
        var (sql, ps) = Build(Q.Where(x => x.Country == "NL"));
        Assert.Contains("\"Country\" = $p0", sql);
        Assert.Equal("NL", ps[0]);
    }

    [Fact]
    public void Where_and_with_date_uses_column_rename_and_inlines_date()
    {
        var cutoff = new DateTime(2023, 1, 1);
        var (sql, ps) = Build(Q.Where(x => x.Country == "NL" && x.SignupDate > cutoff));
        Assert.Contains("\"signup_date\" > DATE '2023-01-01'", sql); // dates are typed literals, not params
        Assert.Contains("\"Country\" = $p0", sql);
        Assert.Equal("NL", ps[0]);
    }

    [Fact]
    public void Select_anonymous_projects_only_those_columns()
        => Assert.StartsWith("SELECT \"Id\", \"Price\" FROM tbl", Build(Q.Select(x => new { x.Id, x.Price })).Sql);

    [Fact]
    public void OrderBy_thenby_take_skip()
    {
        var (sql, _) = Build(Q.OrderBy(x => x.Country).ThenByDescending(x => x.Price).Skip(5).Take(10));
        Assert.Contains("ORDER BY \"Country\", \"Price\" DESC", sql);
        Assert.Contains("LIMIT 10", sql);
        Assert.Contains("OFFSET 5", sql);
    }

    [Fact]
    public void StartsWith_becomes_parameterized_like()
    {
        var (sql, ps) = Build(Q.Where(x => x.Name.StartsWith("J")));
        Assert.Contains("LIKE $p0 ESCAPE", sql);
        Assert.Equal("J%", ps[0]);
    }

    [Fact]
    public void String_contains_pattern_parameter()
        => Assert.Equal("%a%", Build(Q.Where(x => x.Name.Contains("a"))).Params[0]);

    [Fact]
    public void List_contains_becomes_in_with_parameters()
    {
        var countries = new List<string> { "NL", "DE" };
        var (sql, ps) = Build(Q.Where(x => countries.Contains(x.Country)));
        Assert.Contains("\"Country\" IN ($p0, $p1)", sql);
        Assert.Equal(new object?[] { "NL", "DE" }, ps);
    }

    [Fact]
    public void Array_contains_becomes_in() // arrays may bind to MemoryExtensions.Contains (span) on .NET 9
    {
        var countries = new[] { "NL", "DE" };
        Assert.Contains("\"Country\" IN ($p0, $p1)", Build(Q.Where(x => countries.Contains(x.Country))).Sql);
    }

    [Fact]
    public void Boolean_column_is_valid_predicate()
        => Assert.Contains("WHERE (\"IsActive\")", Build(Q.Where(x => x.IsActive)).Sql);

    [Fact]
    public void Not_boolean_column()
        => Assert.Contains("NOT \"IsActive\"", Build(Q.Where(x => !x.IsActive)).Sql);

    [Fact]
    public void Distinct_emits_distinct()
        => Assert.StartsWith("SELECT DISTINCT", Build(Q.Select(x => x.Country).Distinct()).Sql);

    [Fact]
    public void DateTime_year_part()
        => Assert.Contains("EXTRACT(YEAR FROM \"signup_date\")", Build(Q.Where(x => x.SignupDate.Year == 2023)).Sql);

    [Fact]
    public void Coalesce_projection_is_parameterized()
    {
        var (sql, ps) = Build(Q.Select(x => x.Country ?? "?"));
        Assert.Contains("COALESCE(\"Country\", $p0)", sql);
        Assert.Equal("?", ps[0]);
    }

    [Fact]
    public void Captured_variable_is_parameterized()
    {
        var min = 100.0;
        var (sql, ps) = Build(Q.Where(x => x.Price > min));
        Assert.Contains("\"Price\" > $p0", sql);
        Assert.Equal(100.0, ps[0]);
    }

    [Fact]
    public void String_value_is_parameterized_not_inlined()
    {
        var (sql, ps) = Build(Q.Where(x => x.Name == "O'Brien"));
        Assert.Contains("\"Name\" = $p0", sql);
        Assert.Equal("O'Brien", ps[0]); // escaping handled by the parameter binding, not string concat
    }

    [Fact]
    public void Count_renders_count_star()
    {
        var model = QueryTranslator.Parse(Q.Where(x => x.IsActive).Expression);
        var sql = QueryTranslator.RenderAggregate(model, "tbl", AggregateKind.Count, null);
        Assert.Contains("COUNT(*)", sql);
        Assert.Contains("\"IsActive\"", sql);
    }

    [Fact]
    public void Sum_renders_sum_over_selector()
    {
        Expression<Func<User, double>> selector = x => x.Price;
        var sql = QueryTranslator.RenderAggregate(QueryTranslator.Parse(Q.Expression), "tbl", AggregateKind.Sum, selector);
        Assert.Contains("sum(v)", sql);
        Assert.Contains("\"Price\" AS v", sql);
    }

    [Fact]
    public void Any_renders_exists()
        => Assert.Contains("EXISTS(", QueryTranslator.RenderAggregate(QueryTranslator.Parse(Q.Expression), "tbl", AggregateKind.Any, null));
}
