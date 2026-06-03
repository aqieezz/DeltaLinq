using System.Linq.Expressions;
using System.Reflection;

namespace DeltaLinq;

/// <summary>
/// Asynchronous terminal operators (and <see cref="AsAsyncEnumerable{T}"/>) that give DeltaLinq its
/// EF-Core-like feel. Each one translates to SQL and executes on the embedded engine.
/// </summary>
public static class DeltaQueryableExtensions
{
    // ---- Materialization ----------------------------------------------------------

    public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).ExecuteListAsync<T>(source.Expression, ct);

    public static async Task<T[]> ToArrayAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => (await Provider(source).ExecuteListAsync<T>(source.Expression, ct).ConfigureAwait(false)).ToArray();

    /// <summary>Streams rows from the engine without buffering the whole result set.</summary>
    public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).StreamAsync<T>(source.Expression, ct);

    // ---- Left join ----------------------------------------------------------------

    private static readonly MethodInfo LeftJoinMethod =
        typeof(DeltaQueryableExtensions).GetMethods().Single(m => m.Name == nameof(LeftJoin));

    /// <summary>
    /// Inner-style call shape, but emits a SQL <c>LEFT JOIN</c>: every outer row is kept; unmatched
    /// inner columns come back as <c>NULL</c> (or default for non-nullable value types — use
    /// <c>c.X ?? fallback</c> in the projection to handle them).
    /// </summary>
    public static IQueryable<TResult> LeftJoin<TOuter, TInner, TKey, TResult>(
        this IQueryable<TOuter> outer,
        IQueryable<TInner> inner,
        Expression<Func<TOuter, TKey>> outerKeySelector,
        Expression<Func<TInner, TKey>> innerKeySelector,
        Expression<Func<TOuter, TInner?, TResult>> resultSelector)
    {
        var method = LeftJoinMethod.MakeGenericMethod(typeof(TOuter), typeof(TInner), typeof(TKey), typeof(TResult));
        return outer.Provider.CreateQuery<TResult>(Expression.Call(
            method, outer.Expression, inner.Expression,
            Expression.Quote(outerKeySelector), Expression.Quote(innerKeySelector), Expression.Quote(resultSelector)));
    }

    // ---- Counting / existence -----------------------------------------------------

    public static Task<int> CountAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<int>(source.Expression, AggregateKind.Count, null, null, ct);

    public static Task<int> CountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<int>(source.Expression, AggregateKind.Count, null, predicate, ct);

    public static Task<long> LongCountAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<long>(source.Expression, AggregateKind.LongCount, null, null, ct);

    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<bool>(source.Expression, AggregateKind.Any, null, null, ct);

    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<bool>(source.Expression, AggregateKind.Any, null, predicate, ct);

    // ---- Numeric aggregates -------------------------------------------------------

    public static Task<TResult> SumAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<TResult>(source.Expression, AggregateKind.Sum, selector, null, ct);

    public static Task<TResult> SumAsync<TResult>(this IQueryable<TResult> source, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<TResult>(source.Expression, AggregateKind.Sum, null, null, ct);

    public static Task<TResult> MinAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<TResult>(source.Expression, AggregateKind.Min, selector, null, ct);

    public static Task<TResult> MinAsync<TResult>(this IQueryable<TResult> source, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<TResult>(source.Expression, AggregateKind.Min, null, null, ct);

    public static Task<TResult> MaxAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<TResult>(source.Expression, AggregateKind.Max, selector, null, ct);

    public static Task<TResult> MaxAsync<TResult>(this IQueryable<TResult> source, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<TResult>(source.Expression, AggregateKind.Max, null, null, ct);

    public static Task<double> AverageAsync<T, TResult>(this IQueryable<T> source, Expression<Func<T, TResult>> selector, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<double>(source.Expression, AggregateKind.Average, selector, null, ct);

    public static Task<double> AverageAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).ExecuteAggregateAsync<double>(source.Expression, AggregateKind.Average, null, null, ct);

    // ---- Single element -----------------------------------------------------------

    public static Task<T> FirstAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).ExecuteFirstAsync<T>(source.Expression, single: false, orDefault: false, null, ct);

    public static Task<T> FirstAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Provider(source).ExecuteFirstAsync<T>(source.Expression, single: false, orDefault: false, predicate, ct);

    public static async Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => await Provider(source).ExecuteFirstAsync<T>(source.Expression, single: false, orDefault: true, null, ct).ConfigureAwait(false);

    public static async Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await Provider(source).ExecuteFirstAsync<T>(source.Expression, single: false, orDefault: true, predicate, ct).ConfigureAwait(false);

    public static Task<T> SingleAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => Provider(source).ExecuteFirstAsync<T>(source.Expression, single: true, orDefault: false, null, ct);

    public static Task<T> SingleAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => Provider(source).ExecuteFirstAsync<T>(source.Expression, single: true, orDefault: false, predicate, ct);

    public static async Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
        => await Provider(source).ExecuteFirstAsync<T>(source.Expression, single: true, orDefault: true, null, ct).ConfigureAwait(false);

    public static async Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await Provider(source).ExecuteFirstAsync<T>(source.Expression, single: true, orDefault: true, predicate, ct).ConfigureAwait(false);

    private static DeltaQueryProvider Provider<T>(IQueryable<T> source)
        => source.Provider as DeltaQueryProvider
           ?? throw new InvalidOperationException(
               "DeltaLinq async operators can only be used on a DeltaLinq query created via DeltaTable.Open<T>(...).");
}
