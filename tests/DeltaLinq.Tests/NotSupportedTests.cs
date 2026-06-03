using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

/// <summary>The "throw, never silently client-evaluate" contract.</summary>
public class NotSupportedTests
{
    private static IQueryable<User> Q => DeltaTable.Open<User>("dummy");

    private static void Translate<T>(IQueryable<T> q)
        => QueryTranslator.RenderSelect(QueryTranslator.Parse(q.Expression), "tbl");

    [Fact]
    public void NotMapped_column_throws()
        => Assert.Throws<NotSupportedException>(() => Translate(Q.Where(x => x.DisplayName == "x")));

    [Fact]
    public void Untranslatable_method_throws()
        => Assert.Throws<NotSupportedException>(() => Translate(Q.Where(x => x.Name.GetHashCode() == 0)));

    [Fact]
    public void Unsupported_operator_throws()
        => Assert.Throws<NotSupportedException>(() => Translate(Q.Reverse()));

    [Fact]
    public void Async_operators_reject_non_delta_queryables()
        => Assert.Throws<InvalidOperationException>(() => { _ = new[] { 1, 2, 3 }.AsQueryable().ToListAsync(); });
}
