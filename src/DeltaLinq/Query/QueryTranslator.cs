using System.Linq.Expressions;
using System.Text;

namespace DeltaLinq;

/// <summary>
/// Walks a LINQ method chain into a <see cref="QueryModel"/> and renders SQL from it — both
/// row-returning SELECTs and scalar aggregates.
/// </summary>
internal static class QueryTranslator
{
    public static QueryModel Parse(Expression expression)
    {
        var model = new QueryModel();
        Visit(expression, model);
        if (model.Entity is null)
            throw new NotSupportedException("Query does not originate from a DeltaLinq table source.");
        return model;
    }

    private static void Visit(Expression e, QueryModel m)
    {
        if (e is MethodCallExpression mc &&
            (mc.Method.DeclaringType == typeof(Queryable) || mc.Method.DeclaringType == typeof(Enumerable)))
        {
            Visit(mc.Arguments[0], m); // resolve the source (and entity) first

            switch (mc.Method.Name)
            {
                case "Where":
                    if (m.IsGrouped)
                    {
                        if (m.Projection is null)
                            throw new NotSupportedException(
                                "Filter grouped results after Select(...), e.g. .GroupBy(k).Select(g => new { ... }).Where(r => r.Count > N).");
                        m.Having.Add(RenderGroupedFilter(m, mc.Arguments[1]));
                    }
                    else
                    {
                        m.Where.Add(RenderPredicate(m.Entity, mc.Arguments[1], m.Parameters));
                    }
                    break;
                case "GroupBy":
                    HandleGroupBy(mc, m);
                    break;
                case "Select":
                    if (m.IsGrouped)
                        ApplyGroupedSelect(m, GetLambda(mc.Arguments[1]));
                    else
                        m.Projection = Projection.FromSelector(m.Entity, GetLambda(mc.Arguments[1]), m.Parameters);
                    break;
                case "OrderBy":
                case "ThenBy":
                    m.OrderBy.Add(m.IsGrouped
                        ? ResolveGroupedOrder(m, mc.Arguments[1], descending: false)
                        : RenderOrdering(m.Entity, mc.Arguments[1], descending: false, m.Parameters));
                    break;
                case "OrderByDescending":
                case "ThenByDescending":
                    m.OrderBy.Add(m.IsGrouped
                        ? ResolveGroupedOrder(m, mc.Arguments[1], descending: true)
                        : RenderOrdering(m.Entity, mc.Arguments[1], descending: true, m.Parameters));
                    break;
                case "Take":
                    m.Limit = Convert.ToInt32(Evaluate(mc.Arguments[1]));
                    break;
                case "Skip":
                    m.Offset = Convert.ToInt32(Evaluate(mc.Arguments[1]));
                    break;
                case "Distinct":
                    m.Distinct = true;
                    break;
                default:
                    throw new NotSupportedException(
                        $"DeltaLinq does not support the '{mc.Method.Name}' operator. " +
                        "Supported: Where, Select, GroupBy, OrderBy(Descending), ThenBy(Descending), Take, Skip, Distinct " +
                        "(plus the terminal operators Count/Any/Sum/Min/Max/Average/First/Single).");
            }
        }
        else if (e is ConstantExpression c && c.Value is IQueryable q)
        {
            m.SourceType = q.ElementType;
            m.Entity = EntityModel.For(q.ElementType);
        }
        else
        {
            throw new NotSupportedException($"Unexpected node in query expression: {e}");
        }
    }

    public static string RenderSelect(QueryModel m, string source)
    {
        if (m.IsGrouped) return RenderGrouped(m, source);

        var proj = m.EffectiveProjection;
        var sb = new StringBuilder("SELECT ");
        if (m.Distinct) sb.Append("DISTINCT ");
        sb.Append(string.Join(", ", proj.SelectExprs));
        sb.Append(" FROM ").Append(source);
        AppendTail(sb, m);
        return sb.ToString();
    }

    private static string RenderGrouped(QueryModel m, string source)
    {
        if (m.Projection is null)
            throw new NotSupportedException(
                "GroupBy must be followed by Select(...), or use the GroupBy(key, (key, items) => ...) form.");

        var sb = new StringBuilder("SELECT ");
        sb.Append(string.Join(", ", m.Projection.SelectExprs));
        sb.Append(" FROM ").Append(source);
        if (m.Where.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", m.Where.Select(w => $"({w})")));
        sb.Append(" GROUP BY ").Append(string.Join(", ", m.GroupBy));
        if (m.Having.Count > 0)
            sb.Append(" HAVING ").Append(string.Join(" AND ", m.Having.Select(h => $"({h})")));
        if (m.OrderBy.Count > 0)
            sb.Append(" ORDER BY ").Append(string.Join(", ", m.OrderBy));
        if (m.Limit is { } limit) sb.Append(" LIMIT ").Append(limit);
        if (m.Offset is { } offset) sb.Append(" OFFSET ").Append(offset);
        return sb.ToString();
    }

    private static void HandleGroupBy(MethodCallExpression mc, QueryModel m)
    {
        var keySelector = GetLambda(mc.Arguments[1]);
        m.IsGrouped = true;
        m.KeySelector = keySelector;

        if (mc.Arguments.Count == 2) return; // GroupBy(key) — projection comes from a following Select

        if (mc.Arguments.Count == 3 && AsLambda(mc.Arguments[2]) is { Parameters.Count: 2 } resultSelector)
        {
            var grouped = GroupingTranslator.Build(m.Entity, keySelector, resultSelector, m.Parameters, resultSelectorForm: true);
            m.GroupBy.AddRange(grouped.GroupKeys);
            m.Projection = grouped.Projection;
            m.GroupedMembers = grouped.MemberSql;
            return;
        }

        throw new NotSupportedException(
            "Only GroupBy(keySelector)[.Select(...)] and GroupBy(keySelector, (key, items) => result) are supported.");
    }

    private static void ApplyGroupedSelect(QueryModel m, LambdaExpression projector)
    {
        var grouped = GroupingTranslator.Build(m.Entity, m.KeySelector!, projector, m.Parameters, resultSelectorForm: false);
        m.GroupBy.Clear();
        m.GroupBy.AddRange(grouped.GroupKeys);
        m.Projection = grouped.Projection;
        m.GroupedMembers = grouped.MemberSql;
    }

    private static string RenderGroupedFilter(QueryModel m, Expression quotedLambda)
    {
        var lambda = GetLambda(quotedLambda);
        var bindings = m.GroupedMembers.ToDictionary(kv => kv.Key, kv => (Binding)new SqlBinding { Sql = kv.Value });
        return new ExpressionToSql(new JoinScope(bindings, lambda.Parameters[0]), m.Parameters).Render(lambda.Body);
    }

    private static string ResolveGroupedOrder(QueryModel m, Expression quotedLambda, bool descending)
    {
        var body = GetLambda(quotedLambda).Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert } u) body = u.Operand;

        if (body is MemberExpression me && m.GroupedMembers.TryGetValue(me.Member.Name, out var sql))
            return descending ? $"{sql} DESC" : sql;

        throw new NotSupportedException(
            "Ordering after GroupBy must reference a projected member, e.g. OrderByDescending(r => r.Count).");
    }

    private static LambdaExpression? AsLambda(Expression e)
    {
        while (e.NodeType == ExpressionType.Quote) e = ((UnaryExpression)e).Operand;
        return e as LambdaExpression;
    }

    public static string RenderAggregate(QueryModel m, string source, AggregateKind kind, LambdaExpression? selector)
    {
        switch (kind)
        {
            case AggregateKind.Count:
            case AggregateKind.LongCount:
                return $"SELECT COUNT(*) FROM ({RenderSelect(m, source)}) AS sub";

            case AggregateKind.Any:
                return $"SELECT EXISTS({RenderSelect(m, source)})";

            default:
            {
                var value = ValueExpression(m, selector);
                var fn = kind switch
                {
                    AggregateKind.Sum => "sum",
                    AggregateKind.Min => "min",
                    AggregateKind.Max => "max",
                    AggregateKind.Average => "avg",
                    _ => throw new ArgumentOutOfRangeException(nameof(kind))
                };
                var inner = new StringBuilder("SELECT ");
                if (m.Distinct) inner.Append("DISTINCT ");
                inner.Append(value).Append(" AS v FROM ").Append(source);
                AppendTail(inner, m);
                return $"SELECT {fn}(v) FROM ({inner}) AS sub";
            }
        }
    }

    private static string ValueExpression(QueryModel m, LambdaExpression? selector)
    {
        if (selector is not null)
            return new ExpressionToSql(new SingleScope(m.Entity, selector.Parameters[0]), m.Parameters).Render(selector.Body);

        var proj = m.EffectiveProjection;
        if (proj.SelectExprs.Count != 1)
            throw new NotSupportedException("Sum/Min/Max/Average require a selector or a single-column projection.");
        return proj.SelectExprs[0];
    }

    private static void AppendTail(StringBuilder sb, QueryModel m)
    {
        if (m.Where.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", m.Where.Select(w => $"({w})")));
        if (m.OrderBy.Count > 0)
            sb.Append(" ORDER BY ").Append(string.Join(", ", m.OrderBy));
        if (m.Limit is { } limit)
            sb.Append(" LIMIT ").Append(limit);
        if (m.Offset is { } offset)
            sb.Append(" OFFSET ").Append(offset);
    }

    private static string RenderPredicate(EntityModel entity, Expression quotedLambda, SqlParameters parameters)
        => RenderBoolean(entity, GetLambda(quotedLambda), parameters);

    /// <summary>Renders a boolean lambda (e.g. a predicate appended by a terminal operator) to SQL.</summary>
    public static string RenderBoolean(EntityModel entity, LambdaExpression lambda, SqlParameters parameters)
        => new ExpressionToSql(new SingleScope(entity, lambda.Parameters[0]), parameters).Render(lambda.Body);

    private static string RenderOrdering(EntityModel entity, Expression quotedLambda, bool descending, SqlParameters parameters)
    {
        var lambda = GetLambda(quotedLambda);
        var sql = new ExpressionToSql(new SingleScope(entity, lambda.Parameters[0]), parameters).Render(lambda.Body);
        return descending ? $"{sql} DESC" : sql;
    }

    private static LambdaExpression GetLambda(Expression e)
    {
        while (e.NodeType == ExpressionType.Quote) e = ((UnaryExpression)e).Operand;
        return (LambdaExpression)e;
    }

    private static object? Evaluate(Expression e) => ExpressionEval.Eval(e);
}
