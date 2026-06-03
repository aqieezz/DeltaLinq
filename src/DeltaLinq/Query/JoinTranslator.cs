using System.Linq.Expressions;
using System.Text;

namespace DeltaLinq;

/// <summary>A parsed two-source inner-join query.</summary>
internal sealed class JoinedModel
{
    public List<JoinSource> Sources { get; } = new();
    public SqlParameters Parameters { get; } = new();
    public List<string> Where { get; } = new();
    public List<string> OrderBy { get; } = new();
    public int? Limit { get; set; }
    public int? Offset { get; set; }
    public bool Distinct { get; set; }
    public Projection? Projection { get; set; }

    /// <summary>Current row shape: member name → whole-source or computed-scalar binding.</summary>
    public Dictionary<string, Binding> Scope { get; set; } = new();
}

/// <summary>
/// Translates a two-source inner equijoin — <c>outer.Join(inner, ok, ik, (o, c) =&gt; ...)</c> — into a
/// SQL <c>JOIN</c>. Supports single &amp; composite keys, pre-join <c>Where</c> on each source, a final
/// projection or a tuple result selector followed by <c>Where</c>/<c>Select</c>/<c>OrderBy</c>/<c>Take</c>/<c>Skip</c>/<c>Distinct</c>.
/// </summary>
internal static class JoinTranslator
{
    public static bool IsJoin(Expression e)
    {
        while (e is MethodCallExpression mc)
        {
            if (IsJoinCall(mc)) return true;
            if (mc.Arguments.Count == 0) break;
            e = mc.Arguments[0];
        }
        return false;
    }

    private static bool IsJoinCall(MethodCallExpression mc)
        => (mc.Method.DeclaringType == typeof(Queryable) && mc.Method.Name == "Join")
        || (mc.Method.DeclaringType == typeof(DeltaQueryableExtensions) && mc.Method.Name == "LeftJoin");

    public static JoinedModel Parse(Expression e)
        => ParseChain(e) ?? throw new NotSupportedException("Query is not a supported join.");

    private static JoinedModel? ParseChain(Expression e)
    {
        if (e is MethodCallExpression mc)
        {
            if (IsJoinCall(mc)) return BuildJoin(mc);

            if (mc.Method.DeclaringType == typeof(Queryable) && mc.Arguments.Count > 0)
            {
                var model = ParseChain(mc.Arguments[0]);
                if (model is null) return null;
                ApplyPostJoin(model, mc);
                return model;
            }
        }
        return null;
    }

    private static JoinedModel BuildJoin(MethodCallExpression mc)
    {
        var isLeft = mc.Method.DeclaringType == typeof(DeltaQueryableExtensions) && mc.Method.Name == "LeftJoin";
        var outerExpr = mc.Arguments[0];
        var outerKey = Lambda(mc.Arguments[2]);
        var innerKey = Lambda(mc.Arguments[3]);
        var resultSelector = Lambda(mc.Arguments[4]);

        var outerIsJoin = IsJoin(outerExpr);
        JoinedModel model;
        JoinSource? baseOuter = null;

        if (outerIsJoin)
        {
            model = ParseChain(outerExpr)!;
        }
        else
        {
            model = new JoinedModel();
            baseOuter = ParseSource(outerExpr, "t0", model);
            model.Sources.Add(baseOuter);
        }

        IScope OuterScope(ParameterExpression p) => outerIsJoin
            ? new JoinScope(model.Scope, p)
            : new SingleScope(baseOuter!.Model, p, baseOuter.Alias);

        var innerSource = ParseSource(mc.Arguments[1], "t" + model.Sources.Count, model);
        innerSource.JoinKeyword = isLeft ? "LEFT JOIN" : "JOIN";
        innerSource.On = BuildOn(OuterScope(outerKey.Parameters[0]), outerKey, innerSource, innerKey, model.Parameters);
        model.Sources.Add(innerSource);

        var outerShape = outerIsJoin
            ? new OuterShape { Members = model.Scope }
            : new OuterShape { Whole = new SourceBinding { Source = baseOuter! } };

        ApplyResultSelector(model, outerShape, OuterScope(resultSelector.Parameters[0]), resultSelector, innerSource);
        return model;
    }

    private static JoinSource ParseSource(Expression expr, string alias, JoinedModel model)
    {
        var wheres = new List<LambdaExpression>();
        while (expr is MethodCallExpression mc && mc.Method.DeclaringType == typeof(Queryable))
        {
            if (mc.Method.Name == "Where") { wheres.Add(Lambda(mc.Arguments[1])); expr = mc.Arguments[0]; }
            else throw new NotSupportedException(
                $"A join source may only be pre-filtered with Where (found '{mc.Method.Name}'). " +
                "Order, group, or paginate after the join instead.");
        }

        if (expr is ConstantExpression c && c.Value is IQueryable q && q.Provider is DeltaQueryProvider provider)
        {
            var source = new JoinSource
            {
                Alias = alias,
                Model = EntityModel.For(q.ElementType),
                TableSql = $"{provider.TableSource()} AS {alias}"
            };

            wheres.Reverse(); // restore original order
            foreach (var w in wheres)
                model.Where.Add(new ExpressionToSql(new SingleScope(source.Model, w.Parameters[0], alias), model.Parameters).Render(w.Body));

            return source;
        }

        throw new NotSupportedException("A join source must be a DeltaTable.Open<T>(...) query (optionally filtered with Where).");
    }

    private static string BuildOn(IScope outerScope, LambdaExpression outerKey, JoinSource inner, LambdaExpression innerKey, SqlParameters parameters)
    {
        var ok = new ExpressionToSql(outerScope, parameters);
        var ik = new ExpressionToSql(new SingleScope(inner.Model, innerKey.Parameters[0], inner.Alias), parameters);

        if (outerKey.Body is NewExpression on && innerKey.Body is NewExpression inn)
        {
            if (on.Arguments.Count != inn.Arguments.Count)
                throw new NotSupportedException("Composite join keys must have the same number of members on both sides.");
            var conditions = new List<string>();
            for (var i = 0; i < on.Arguments.Count; i++)
                conditions.Add($"{ok.Render(on.Arguments[i])} = {ik.Render(inn.Arguments[i])}");
            return string.Join(" AND ", conditions);
        }

        return $"{ok.Render(outerKey.Body)} = {ik.Render(innerKey.Body)}";
    }

    private static void ApplyResultSelector(JoinedModel model, OuterShape outerShape, IScope outerScope, LambdaExpression resultSelector, JoinSource inner)
    {
        var outerParam = resultSelector.Parameters[0];
        var innerParam = resultSelector.Parameters[1];

        // Tuple of whole sources, e.g. (o, c) => new { o, c } — keep sources for a later Select.
        if (resultSelector.Body is NewExpression n && n.Members is not null
            && n.Arguments.All(a => IsSourceRef(a, outerParam, innerParam, outerShape)))
        {
            var scope = new Dictionary<string, Binding>();
            for (var i = 0; i < n.Arguments.Count; i++)
                scope[n.Members[i].Name] = ResolveSourceRef(n.Arguments[i], outerParam, innerParam, outerShape, inner);
            model.Scope = scope;
            model.Projection = null; // projection deferred to a following Select
            return;
        }

        // Otherwise the result selector IS the final (scalar) projection.
        var combined = new CombinedScope(outerScope, inner, innerParam);
        model.Projection = Projection.FromSelector(combined, resultSelector, model.Parameters, out var memberSql);
        model.Scope = ToScalarScope(memberSql);
    }

    private static bool IsSourceRef(Expression arg, ParameterExpression outerParam, ParameterExpression innerParam, OuterShape outerShape)
    {
        if (arg == innerParam) return true;
        if (arg == outerParam) return outerShape.Whole is not null;
        return arg is MemberExpression m && m.Expression == outerParam && outerShape.Members is not null
            && outerShape.Members.TryGetValue(m.Member.Name, out var b) && b is SourceBinding;
    }

    private static Binding ResolveSourceRef(Expression arg, ParameterExpression outerParam, ParameterExpression innerParam, OuterShape outerShape, JoinSource inner)
    {
        if (arg == innerParam) return new SourceBinding { Source = inner };
        if (arg == outerParam) return outerShape.Whole!;
        return outerShape.Members![((MemberExpression)arg).Member.Name];
    }

    private static void ApplyPostJoin(JoinedModel model, MethodCallExpression mc)
    {
        switch (mc.Method.Name)
        {
            case "Where":
                model.Where.Add(RenderRow(model, Lambda(mc.Arguments[1])));
                break;
            case "Select":
            {
                var lambda = Lambda(mc.Arguments[1]);
                var scope = new JoinScope(model.Scope, lambda.Parameters[0]);
                model.Projection = Projection.FromSelector(scope, lambda, model.Parameters, out var memberSql);
                model.Scope = ToScalarScope(memberSql);
                break;
            }
            case "OrderBy":
            case "ThenBy":
                model.OrderBy.Add(RenderRow(model, Lambda(mc.Arguments[1])));
                break;
            case "OrderByDescending":
            case "ThenByDescending":
                model.OrderBy.Add(RenderRow(model, Lambda(mc.Arguments[1])) + " DESC");
                break;
            case "Take":
                model.Limit = Convert.ToInt32(Evaluate(mc.Arguments[1]));
                break;
            case "Skip":
                model.Offset = Convert.ToInt32(Evaluate(mc.Arguments[1]));
                break;
            case "Distinct":
                model.Distinct = true;
                break;
            default:
                throw new NotSupportedException(
                    $"DeltaLinq does not support '{mc.Method.Name}' after a join. " +
                    "Supported after a join: Where, Select, OrderBy(Descending), ThenBy(Descending), Take, Skip, Distinct.");
        }
    }

    public static string RenderRow(JoinedModel model, LambdaExpression lambda)
        => new ExpressionToSql(new JoinScope(model.Scope, lambda.Parameters[0]), model.Parameters).Render(lambda.Body);

    public static string Render(JoinedModel model)
    {
        var projection = model.Projection ?? throw new NotSupportedException(
            "Project the joined rows with Select(...) into scalar columns (or use a result selector that does) before materializing.");

        var sb = new StringBuilder("SELECT ");
        if (model.Distinct) sb.Append("DISTINCT ");
        sb.Append(string.Join(", ", projection.SelectExprs));
        AppendFromWhere(sb, model);
        if (model.OrderBy.Count > 0) sb.Append(" ORDER BY ").Append(string.Join(", ", model.OrderBy));
        if (model.Limit is { } limit) sb.Append(" LIMIT ").Append(limit);
        if (model.Offset is { } offset) sb.Append(" OFFSET ").Append(offset);
        return sb.ToString();
    }

    public static string RenderCount(JoinedModel model)
    {
        var inner = new StringBuilder("SELECT 1");
        AppendFromWhere(inner, model);
        if (model.Limit is { } limit) inner.Append(" LIMIT ").Append(limit);
        if (model.Offset is { } offset) inner.Append(" OFFSET ").Append(offset);
        return $"SELECT COUNT(*) FROM ({inner}) AS sub";
    }

    public static string RenderExists(JoinedModel model)
    {
        var inner = new StringBuilder("SELECT 1");
        AppendFromWhere(inner, model);
        return $"SELECT EXISTS({inner})";
    }

    public static string RenderAggregate(JoinedModel model, AggregateKind kind, LambdaExpression selector)
    {
        var fn = kind switch
        {
            AggregateKind.Sum => "sum",
            AggregateKind.Min => "min",
            AggregateKind.Max => "max",
            AggregateKind.Average => "avg",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var inner = new StringBuilder("SELECT ");
        if (model.Distinct) inner.Append("DISTINCT ");
        inner.Append(RenderRow(model, selector)).Append(" AS v");
        AppendFromWhere(inner, model);
        if (model.Limit is { } limit) inner.Append(" LIMIT ").Append(limit);
        if (model.Offset is { } offset) inner.Append(" OFFSET ").Append(offset);
        return $"SELECT {fn}(v) FROM ({inner}) AS sub";
    }

    private static void AppendFromWhere(StringBuilder sb, JoinedModel model)
    {
        sb.Append(" FROM ").Append(model.Sources[0].TableSql);
        for (var i = 1; i < model.Sources.Count; i++)
            sb.Append(' ').Append(model.Sources[i].JoinKeyword).Append(' ')
              .Append(model.Sources[i].TableSql).Append(" ON ").Append(model.Sources[i].On);
        if (model.Where.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", model.Where.Select(w => $"({w})")));
    }

    private static Dictionary<string, Binding> ToScalarScope(Dictionary<string, string> memberSql)
        => memberSql.ToDictionary(kv => kv.Key, kv => (Binding)new SqlBinding { Sql = kv.Value });

    private static LambdaExpression Lambda(Expression e)
    {
        while (e.NodeType == ExpressionType.Quote) e = ((UnaryExpression)e).Operand;
        return (LambdaExpression)e;
    }

    private static object? Evaluate(Expression e) => ExpressionEval.Eval(e);
}
