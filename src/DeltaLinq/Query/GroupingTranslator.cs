using System.Linq.Expressions;
using System.Reflection;

namespace DeltaLinq;

/// <summary>Result of translating a GroupBy + projection: GROUP BY keys, the SELECT projection, and a member→SQL map for post-group ordering.</summary>
internal sealed record GroupedResult(List<string> GroupKeys, Projection Projection, Dictionary<string, string> MemberSql);

/// <summary>
/// Translates <c>GroupBy(keySelector).Select(g =&gt; ...)</c> and the
/// <c>GroupBy(keySelector, (key, items) =&gt; ...)</c> result-selector form into a SQL GROUP BY.
/// Supports single and composite (anonymous) keys, and <c>g.Key</c>/<c>g.Count()</c>/<c>g.Sum(...)</c>
/// /<c>Min</c>/<c>Max</c>/<c>Average</c> (with arithmetic between them) in the projection.
/// </summary>
internal static class GroupingTranslator
{
    public static GroupedResult Build(EntityModel entity, LambdaExpression keySelector, LambdaExpression projector, SqlParameters parameters, bool resultSelectorForm)
    {
        // --- key info (shared by GROUP BY and g.Key references) ---
        var keyRenderer = new ExpressionToSql(new SingleScope(entity, keySelector.Parameters[0]), parameters);
        Dictionary<string, string>? keyParts = null;
        string? keyWhole = null;
        var groupKeys = new List<string>();

        if (keySelector.Body is NewExpression keyNew && keyNew.Members is not null)
        {
            keyParts = new Dictionary<string, string>();
            for (var i = 0; i < keyNew.Arguments.Count; i++)
            {
                var sql = keyRenderer.Render(keyNew.Arguments[i]);
                keyParts[keyNew.Members[i].Name] = sql;
                groupKeys.Add(sql);
            }
        }
        else
        {
            keyWhole = keyRenderer.Render(keySelector.Body);
            groupKeys.Add(keyWhole);
        }

        var renderer = new GroupRenderer(
            entity, parameters, keyParts, keyWhole,
            groupParam: resultSelectorForm ? null : projector.Parameters[0],
            keyParam: resultSelectorForm ? projector.Parameters[0] : null,
            itemsParam: resultSelectorForm ? projector.Parameters[1] : null);

        // --- projection ---
        var memberSql = new Dictionary<string, string>();

        switch (projector.Body)
        {
            case NewExpression n when n.Members is not null:
            {
                var exprs = n.Arguments.Select(renderer.Render).ToList();
                for (var i = 0; i < exprs.Count; i++) memberSql[n.Members[i].Name] = exprs[i];
                var ctor = n.Constructor!;
                var pars = ctor.GetParameters();
                var projection = new Projection
                {
                    ResultType = n.Type,
                    SelectExprs = exprs,
                    Build = values => ctor.Invoke(values.Select((v, i) => Coerce.To(v, pars[i].ParameterType)).ToArray())
                };
                return new GroupedResult(groupKeys, projection, memberSql);
            }

            case MemberInitExpression mi:
            {
                var assignments = mi.Bindings.OfType<MemberAssignment>().ToArray();
                var exprs = assignments.Select(a => renderer.Render(a.Expression)).ToList();
                for (var i = 0; i < exprs.Count; i++) memberSql[assignments[i].Member.Name] = exprs[i];
                var ctor = mi.NewExpression.Constructor!;
                var projection = new Projection
                {
                    ResultType = mi.Type,
                    SelectExprs = exprs,
                    Build = values =>
                    {
                        var obj = ctor.Invoke(Array.Empty<object>());
                        for (var i = 0; i < assignments.Length; i++)
                        {
                            var prop = (PropertyInfo)assignments[i].Member;
                            prop.SetValue(obj, Coerce.To(values[i], prop.PropertyType));
                        }
                        return obj;
                    }
                };
                return new GroupedResult(groupKeys, projection, memberSql);
            }

            default:
            {
                var expr = renderer.Render(projector.Body);
                var resultType = projector.Body.Type;
                var projection = new Projection
                {
                    ResultType = resultType,
                    SelectExprs = new[] { expr },
                    Build = values => Coerce.To(values[0], resultType)
                };
                return new GroupedResult(groupKeys, projection, memberSql);
            }
        }
    }

    /// <summary>Renders one projection element of a grouped query (key access, aggregate, or arithmetic of those).</summary>
    private sealed class GroupRenderer
    {
        private readonly EntityModel _entity;
        private readonly SqlParameters _parameters;
        private readonly Dictionary<string, string>? _keyParts;
        private readonly string? _keyWhole;
        private readonly ParameterExpression? _groupParam;   // select form: g
        private readonly ParameterExpression? _keyParam;     // result form: key
        private readonly ParameterExpression? _itemsParam;   // result form: items
        private readonly ParameterExpression _groupSource;   // whichever represents the rows

        public GroupRenderer(EntityModel entity, SqlParameters parameters, Dictionary<string, string>? keyParts, string? keyWhole,
            ParameterExpression? groupParam, ParameterExpression? keyParam, ParameterExpression? itemsParam)
        {
            _entity = entity;
            _parameters = parameters;
            _keyParts = keyParts;
            _keyWhole = keyWhole;
            _groupParam = groupParam;
            _keyParam = keyParam;
            _itemsParam = itemsParam;
            _groupSource = (groupParam ?? itemsParam)!;
        }

        public string Render(Expression e)
        {
            switch (e)
            {
                case UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote:
                    return Render(u.Operand);

                case ConstantExpression c:
                    return _parameters.Value(c.Value);

                case BinaryExpression b:
                    return $"({Render(b.Left)} {Op(b.NodeType)} {Render(b.Right)})";

                case ParameterExpression p when p == _keyParam:
                    return KeyWhole();

                // g.Key (select form)
                case MemberExpression m when m.Member.Name == "Key" && m.Expression == _groupParam:
                    return KeyWhole();

                // g.Key.Member (select form)
                case MemberExpression m when m.Expression is MemberExpression km && km.Member.Name == "Key" && km.Expression == _groupParam:
                    return KeyPart(m.Member.Name);

                // key.Member (result form)
                case MemberExpression m when m.Expression == _keyParam:
                    return KeyPart(m.Member.Name);

                case MethodCallExpression mc when IsGroupAggregate(mc):
                    return Aggregate(mc);
            }

            if (!ReferencesGroup(e))
                return _parameters.Value(Evaluate(e));

            throw new NotSupportedException(
                $"DeltaLinq cannot translate the grouped projection element '{e}'. " +
                "Use g.Key (or g.Key.Member), aggregates (g.Count()/g.Sum(x => ...)/Min/Max/Average), " +
                "and arithmetic between them.");
        }

        private string KeyWhole()
            => _keyWhole ?? throw new NotSupportedException(
                "Selecting the whole composite key is not supported; project its members instead (e.g. g.Key.Country).");

        private string KeyPart(string member)
            => _keyParts is not null && _keyParts.TryGetValue(member, out var sql)
                ? sql
                : throw new NotSupportedException($"'{member}' is not part of the grouping key.");

        private bool IsGroupAggregate(MethodCallExpression mc)
            => mc.Method.DeclaringType == typeof(Enumerable)
               && mc.Arguments.Count > 0
               && mc.Arguments[0] == _groupSource;

        private string Aggregate(MethodCallExpression mc)
        {
            switch (mc.Method.Name)
            {
                case "Count":
                case "LongCount":
                    if (mc.Arguments.Count == 2)
                        return $"COUNT(*) FILTER (WHERE {RenderInner(mc.Arguments[1])})";
                    return "COUNT(*)";
                case "Sum": return $"sum({RenderInner(mc.Arguments[1])})";
                case "Min": return $"min({RenderInner(mc.Arguments[1])})";
                case "Max": return $"max({RenderInner(mc.Arguments[1])})";
                case "Average": return $"avg({RenderInner(mc.Arguments[1])})";
                default:
                    throw new NotSupportedException($"Aggregate '{mc.Method.Name}' is not supported in a grouped projection.");
            }
        }

        private string RenderInner(Expression quotedLambda)
        {
            var lambda = StripQuotes(quotedLambda);
            return new ExpressionToSql(new SingleScope(_entity, lambda.Parameters[0]), _parameters).Render(lambda.Body);
        }

        private bool ReferencesGroup(Expression e)
        {
            var finder = new ParamFinder(_groupParam, _keyParam, _itemsParam);
            finder.Visit(e);
            return finder.Found;
        }

        private static LambdaExpression StripQuotes(Expression e)
        {
            while (e.NodeType == ExpressionType.Quote) e = ((UnaryExpression)e).Operand;
            return (LambdaExpression)e;
        }

        private static object? Evaluate(Expression e) => ExpressionEval.Eval(e);

        private static string Op(ExpressionType t) => t switch
        {
            ExpressionType.Add => "+",
            ExpressionType.Subtract => "-",
            ExpressionType.Multiply => "*",
            ExpressionType.Divide => "/",
            ExpressionType.Modulo => "%",
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.AndAlso or ExpressionType.And => "AND",
            ExpressionType.OrElse or ExpressionType.Or => "OR",
            _ => throw new NotSupportedException($"Operator '{t}' is not supported in a grouped projection.")
        };

        private sealed class ParamFinder : ExpressionVisitor
        {
            private readonly ParameterExpression?[] _targets;
            public bool Found;
            public ParamFinder(params ParameterExpression?[] targets) => _targets = targets;

            public override Expression? Visit(Expression? node) => Found ? node : base.Visit(node);

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (Array.IndexOf(_targets, node) >= 0) Found = true;
                return node;
            }
        }
    }
}
