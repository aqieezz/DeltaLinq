using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace DeltaLinq;

/// <summary>
/// Translates expression trees to SQL and runs them on the embedded engine. Generic execution
/// methods are called directly by the async extension operators; the synchronous
/// <see cref="IQueryProvider"/> surface dispatches LINQ terminals (Count/First/Sum/...) by reflection.
/// </summary>
internal sealed class DeltaQueryProvider : IQueryProvider
{
    private readonly string _path;
    private readonly DeltaOptions _options;
    private readonly DuckDbExecutor _executor;

    public DeltaQueryProvider(string path, DeltaOptions options)
    {
        _path = path;
        _options = options;
        _executor = new DuckDbExecutor(options);
    }

    /// <summary>The <c>delta_scan('...')</c> SQL for this provider's table (used when building joins).</summary>
    internal string TableSource() => _executor.TableSource(_path);

    // ---- IQueryable construction -------------------------------------------------

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => new DeltaQueryable<TElement>(this, expression);

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = ElementTypeOf(expression.Type);
        return (IQueryable)Activator.CreateInstance(
            typeof(DeltaQueryable<>).MakeGenericType(elementType), this, expression)!;
    }

    // ---- Generic execution (called by the async operators) -----------------------

    public List<T> ExecuteList<T>(Expression expression)
        => JoinTranslator.IsJoin(expression)
            ? MaterializeJoin<T>(JoinTranslator.Parse(expression))
            : Materialize<T>(QueryTranslator.Parse(expression));

    public Task<List<T>> ExecuteListAsync<T>(Expression expression, CancellationToken ct)
        => Task.Run(() => ExecuteList<T>(expression), ct);

    public TResult ExecuteAggregate<TResult>(Expression source, AggregateKind kind, LambdaExpression? selector, LambdaExpression? predicate)
    {
        if (JoinTranslator.IsJoin(source))
            return ExecuteJoinAggregate<TResult>(source, kind, selector, predicate);

        var model = QueryTranslator.Parse(source);
        if (predicate is not null)
            model.Where.Add(QueryTranslator.RenderBoolean(model.Entity, predicate, model.Parameters));

        var sql = QueryTranslator.RenderAggregate(model, _executor.TableSource(_path), kind, selector);
        _options.OnSql?.Invoke(sql);
        return (TResult)Coerce.To(_executor.Scalar(sql, model.Parameters.Items), typeof(TResult))!;
    }

    private TResult ExecuteJoinAggregate<TResult>(Expression source, AggregateKind kind, LambdaExpression? selector, LambdaExpression? predicate)
    {
        var model = JoinTranslator.Parse(source);
        if (predicate is not null)
            model.Where.Add(JoinTranslator.RenderRow(model, predicate));

        var sql = kind switch
        {
            AggregateKind.Count or AggregateKind.LongCount => JoinTranslator.RenderCount(model),
            AggregateKind.Any => JoinTranslator.RenderExists(model),
            _ => JoinTranslator.RenderAggregate(model, kind, selector
                ?? throw new NotSupportedException("Sum/Min/Max/Average over a join require a selector."))
        };
        _options.OnSql?.Invoke(sql);
        return (TResult)Coerce.To(_executor.Scalar(sql, model.Parameters.Items), typeof(TResult))!;
    }

    public Task<TResult> ExecuteAggregateAsync<TResult>(Expression source, AggregateKind kind, LambdaExpression? selector, LambdaExpression? predicate, CancellationToken ct)
        => Task.Run(() => ExecuteAggregate<TResult>(source, kind, selector, predicate), ct);

    public T ExecuteFirst<T>(Expression source, bool single, bool orDefault, LambdaExpression? predicate)
    {
        List<T> list;
        if (JoinTranslator.IsJoin(source))
        {
            var joined = JoinTranslator.Parse(source);
            if (predicate is not null)
                joined.Where.Add(JoinTranslator.RenderRow(joined, predicate));
            joined.Limit = single ? 2 : 1;
            list = MaterializeJoin<T>(joined);
        }
        else
        {
            var model = QueryTranslator.Parse(source);
            if (predicate is not null)
                model.Where.Add(QueryTranslator.RenderBoolean(model.Entity, predicate, model.Parameters));
            model.Limit = single ? 2 : 1;
            list = Materialize<T>(model);
        }

        if (list.Count == 0)
            return orDefault ? default! : throw new InvalidOperationException("Sequence contains no elements.");
        if (single && list.Count > 1)
            throw new InvalidOperationException("Sequence contains more than one element.");
        return list[0];
    }

    public Task<T> ExecuteFirstAsync<T>(Expression source, bool single, bool orDefault, LambdaExpression? predicate, CancellationToken ct)
        => Task.Run(() => ExecuteFirst<T>(source, single, orDefault, predicate), ct);

    public async IAsyncEnumerable<T> StreamAsync<T>(Expression expression, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string sql;
        Projection projection;
        SqlParameters parameters;

        if (JoinTranslator.IsJoin(expression))
        {
            var joined = JoinTranslator.Parse(expression);
            sql = JoinTranslator.Render(joined);
            projection = joined.Projection!;
            parameters = joined.Parameters;
        }
        else
        {
            var model = QueryTranslator.Parse(expression);
            sql = QueryTranslator.RenderSelect(model, _executor.TableSource(_path));
            projection = model.EffectiveProjection;
            parameters = model.Parameters;
        }

        _options.OnSql?.Invoke(sql);
        await foreach (var row in _executor.StreamAsync(sql, parameters.Items, ct).ConfigureAwait(false))
            yield return (T)projection.Build(row)!;
    }

    private List<T> Materialize<T>(QueryModel model)
    {
        var sql = QueryTranslator.RenderSelect(model, _executor.TableSource(_path));
        _options.OnSql?.Invoke(sql);
        var projection = model.EffectiveProjection;

        var rows = _executor.Query(sql, model.Parameters.Items);
        var result = new List<T>(rows.Count);
        foreach (var row in rows)
            result.Add((T)projection.Build(row)!);
        return result;
    }

    private List<T> MaterializeJoin<T>(JoinedModel model)
    {
        var sql = JoinTranslator.Render(model);
        _options.OnSql?.Invoke(sql);
        var projection = model.Projection!;

        var rows = _executor.Query(sql, model.Parameters.Items);
        var result = new List<T>(rows.Count);
        foreach (var row in rows)
            result.Add((T)projection.Build(row)!);
        return result;
    }

    // ---- Synchronous IQueryProvider dispatch -------------------------------------

    object? IQueryProvider.Execute(Expression expression) => ExecuteDynamic(expression);
    TResult IQueryProvider.Execute<TResult>(Expression expression) => (TResult)ExecuteDynamic(expression)!;

    private object? ExecuteDynamic(Expression expression)
    {
        if (expression is MethodCallExpression mc &&
            (mc.Method.DeclaringType == typeof(Queryable) || mc.Method.DeclaringType == typeof(Enumerable)))
        {
            var source = mc.Arguments[0];
            var lambda = mc.Arguments.Count > 1 ? Unquote(mc.Arguments[1]) : null;

            switch (mc.Method.Name)
            {
                case "Count": return InvokeAggregate(typeof(int), source, AggregateKind.Count, null, lambda);
                case "LongCount": return InvokeAggregate(typeof(long), source, AggregateKind.LongCount, null, lambda);
                case "Any": return InvokeAggregate(typeof(bool), source, AggregateKind.Any, null, lambda);
                case "All": return AllImpl(source, lambda);
                case "Sum": return InvokeAggregate(mc.Type, source, AggregateKind.Sum, lambda, null);
                case "Min": return InvokeAggregate(mc.Type, source, AggregateKind.Min, lambda, null);
                case "Max": return InvokeAggregate(mc.Type, source, AggregateKind.Max, lambda, null);
                case "Average": return InvokeAggregate(mc.Type, source, AggregateKind.Average, lambda, null);
                case "First": return InvokeFirst(ElementTypeOf(source.Type), source, false, false, lambda);
                case "FirstOrDefault": return InvokeFirst(ElementTypeOf(source.Type), source, false, true, lambda);
                case "Single": return InvokeFirst(ElementTypeOf(source.Type), source, true, false, lambda);
                case "SingleOrDefault": return InvokeFirst(ElementTypeOf(source.Type), source, true, true, lambda);
            }
        }

        // Row-returning query: materialize as List<elementType>.
        return InvokeList(ElementTypeOf(expression.Type), expression);
    }

    private bool AllImpl(Expression source, LambdaExpression? predicate)
    {
        if (predicate is null) return true;
        var negated = Expression.Lambda(Expression.Not(predicate.Body), predicate.Parameters);
        return !ExecuteAggregate<bool>(source, AggregateKind.Any, null, negated);
    }

    private object? InvokeAggregate(Type resultType, Expression source, AggregateKind kind, LambdaExpression? selector, LambdaExpression? predicate)
        => typeof(DeltaQueryProvider).GetMethod(nameof(ExecuteAggregate))!
            .MakeGenericMethod(resultType)
            .Invoke(this, new object?[] { source, kind, selector, predicate });

    private object? InvokeFirst(Type elementType, Expression source, bool single, bool orDefault, LambdaExpression? predicate)
        => typeof(DeltaQueryProvider).GetMethod(nameof(ExecuteFirst))!
            .MakeGenericMethod(elementType)
            .Invoke(this, new object?[] { source, single, orDefault, predicate });

    private object? InvokeList(Type elementType, Expression expression)
        => typeof(DeltaQueryProvider).GetMethod(nameof(ExecuteList))!
            .MakeGenericMethod(elementType)
            .Invoke(this, new object?[] { expression });

    private static LambdaExpression? Unquote(Expression e)
    {
        while (e.NodeType == ExpressionType.Quote) e = ((UnaryExpression)e).Operand;
        return e as LambdaExpression;
    }

    private static Type ElementTypeOf(Type sequenceType)
    {
        if (sequenceType.IsGenericType && sequenceType.GetGenericArguments() is { Length: 1 } ga)
            return ga[0];
        var enumerable = sequenceType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0] ?? sequenceType;
    }
}
