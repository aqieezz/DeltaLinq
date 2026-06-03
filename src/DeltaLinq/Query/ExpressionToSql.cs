using System.Collections;
using System.Linq.Expressions;

namespace DeltaLinq;

/// <summary>
/// Translates a single lambda body (predicate, ordering key, or projection element) into a SQL
/// fragment. Member access on the query parameter becomes a column reference; any subtree that does
/// not reference the parameter is evaluated to a literal ("partial evaluation"). Anything that can't
/// be expressed in SQL throws — DeltaLinq never silently falls back to client-side evaluation.
/// </summary>
internal sealed class ExpressionToSql
{
    private readonly IScope _scope;
    private readonly SqlParameters _parameters;

    public ExpressionToSql(IScope scope, SqlParameters parameters)
    {
        _scope = scope;
        _parameters = parameters;
    }

    public string Render(Expression e)
    {
        switch (e)
        {
            case LambdaExpression l:
                return Render(l.Body);

            case UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote:
                return Render(u.Operand);
            case UnaryExpression u when u.NodeType == ExpressionType.Not:
                return $"(NOT {Render(u.Operand)})";
            case UnaryExpression u when u.NodeType is ExpressionType.Negate or ExpressionType.NegateChecked:
                return $"(-{Render(u.Operand)})";

            case BinaryExpression b:
                return RenderBinary(b);

            case ConditionalExpression c:
                return $"(CASE WHEN {Render(c.Test)} THEN {Render(c.IfTrue)} ELSE {Render(c.IfFalse)} END)";

            case MemberExpression m:
                return RenderMember(m);

            case MethodCallExpression mc:
                return RenderMethodCall(mc);

            case ConstantExpression k:
                return _parameters.Value(k.Value);
        }

        if (!_scope.ReferencesRow(e))
            return _parameters.Value(Evaluate(e));

        throw Unsupported(e);
    }

    private string RenderMember(MemberExpression m)
    {
        // x.Column  (or x.source.Column inside a join)
        if (_scope.TryColumn(m, out var column))
            return column;

        // x.DateColumn.Year / .Month / .Day / .Hour / .Minute / .Second
        if (m.Expression is not null &&
            (m.Member.DeclaringType == typeof(DateTime) || m.Member.DeclaringType == typeof(DateOnly)) &&
            DatePart(m.Member.Name) is { } part)
            return $"EXTRACT({part} FROM {Render(m.Expression)})";

        // x.StringColumn.Length
        if (m.Member.Name == "Length" && m.Expression is { Type: var tt } && tt == typeof(string))
            return $"length({Render(m.Expression)})";

        // Nullable<T>.Value / .HasValue
        if (m.Expression is not null && Nullable.GetUnderlyingType(m.Expression.Type) is not null)
        {
            if (m.Member.Name == "Value") return Render(m.Expression);
            if (m.Member.Name == "HasValue") return $"({Render(m.Expression)} IS NOT NULL)";
        }

        if (!_scope.ReferencesRow(m))
            return _parameters.Value(Evaluate(m));

        throw Unsupported(m);
    }

    private string RenderBinary(BinaryExpression b)
    {
        if (b.NodeType == ExpressionType.Coalesce)
            return $"COALESCE({Render(b.Left)}, {Render(b.Right)})";

        if (b.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            var not = b.NodeType == ExpressionType.NotEqual;
            if (IsNull(b.Right)) return $"({Render(b.Left)} IS {(not ? "NOT " : "")}NULL)";
            if (IsNull(b.Left)) return $"({Render(b.Right)} IS {(not ? "NOT " : "")}NULL)";
        }

        // string "+" is concatenation
        if (b.NodeType == ExpressionType.Add && (b.Left.Type == typeof(string) || b.Right.Type == typeof(string)))
            return $"({Render(b.Left)} || {Render(b.Right)})";

        var op = b.NodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.AndAlso or ExpressionType.And => "AND",
            ExpressionType.OrElse or ExpressionType.Or => "OR",
            ExpressionType.Add => "+",
            ExpressionType.Subtract => "-",
            ExpressionType.Multiply => "*",
            ExpressionType.Divide => "/",
            ExpressionType.Modulo => "%",
            _ => throw Unsupported(b)
        };
        return $"({Render(b.Left)} {op} {Render(b.Right)})";
    }

    private string RenderMethodCall(MethodCallExpression mc)
    {
        // Instance string methods on a column/expression.
        if (mc.Object is { } obj && obj.Type == typeof(string))
        {
            switch (mc.Method.Name)
            {
                case "Contains": return Like(obj, mc.Arguments[0], leading: true, trailing: true);
                case "StartsWith": return Like(obj, mc.Arguments[0], leading: false, trailing: true);
                case "EndsWith": return Like(obj, mc.Arguments[0], leading: true, trailing: false);
                case "ToLower": return $"lower({Render(obj)})";
                case "ToUpper": return $"upper({Render(obj)})";
                case "Trim": return $"trim({Render(obj)})";
                case "TrimStart": return $"ltrim({Render(obj)})";
                case "TrimEnd": return $"rtrim({Render(obj)})";
                case "Replace": return $"replace({Render(obj)}, {Render(mc.Arguments[0])}, {Render(mc.Arguments[1])})";
                case "Substring":
                    var start = $"({Render(mc.Arguments[0])} + 1)"; // SQL substring is 1-based
                    return mc.Arguments.Count == 1
                        ? $"substring({Render(obj)}, {start})"
                        : $"substring({Render(obj)}, {start}, {Render(mc.Arguments[1])})";
            }
        }

        // Static string helpers.
        if (mc.Method.DeclaringType == typeof(string))
        {
            switch (mc.Method.Name)
            {
                case "IsNullOrEmpty":
                {
                    var a = Render(mc.Arguments[0]);
                    return $"({a} IS NULL OR {a} = '')";
                }
                case "IsNullOrWhiteSpace":
                {
                    var a = Render(mc.Arguments[0]);
                    return $"({a} IS NULL OR trim({a}) = '')";
                }
                case "Concat":
                    return "(" + string.Join(" || ", mc.Arguments.Select(Render)) + ")";
            }
        }

        // collection.Contains(x.Col) / Enumerable.Contains(collection, x.Col) / array.Contains (span overload) -> IN (...)
        if (mc.Method.Name == "Contains")
        {
            Expression? collection = null, item = null;
            if (mc.Object is { } o && o.Type != typeof(string)) { collection = o; item = mc.Arguments[0]; }
            else if (mc.Arguments.Count == 2) { collection = mc.Arguments[0]; item = mc.Arguments[1]; }

            if (collection is not null && item is not null)
            {
                collection = UnwrapConversions(collection); // e.g. string[] -> ReadOnlySpan<string> for MemoryExtensions.Contains
                if (!_scope.ReferencesRow(collection) && _scope.ReferencesRow(item))
                    return RenderIn(item, collection);
            }
        }

        // Math.* over columns.
        if (mc.Method.DeclaringType == typeof(Math))
        {
            var fn = mc.Method.Name switch
            {
                "Abs" => "abs", "Ceiling" => "ceil", "Floor" => "floor", "Round" => "round",
                "Sqrt" => "sqrt", "Pow" => "pow", "Exp" => "exp", "Log" => "ln",
                _ => null
            };
            if (fn is not null)
                return $"{fn}({string.Join(", ", mc.Arguments.Select(Render))})";
        }

        if (!_scope.ReferencesRow(mc))
            return _parameters.Value(Evaluate(mc));

        throw Unsupported(mc);
    }

    private string Like(Expression column, Expression pattern, bool leading, bool trailing)
    {
        var col = Render(column);
        if (pattern is ConstantExpression { Value: string s })
        {
            var escaped = s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var like = (leading ? "%" : "") + escaped + (trailing ? "%" : "");
            return $"({col} LIKE {_parameters.Value(like)} ESCAPE '\\')";
        }

        var expr = Render(pattern);
        if (leading) expr = "'%' || " + expr;
        if (trailing) expr = expr + " || '%'";
        return $"({col} LIKE {expr})";
    }

    private string RenderIn(Expression item, Expression collection)
    {
        var col = Render(item);
        var values = Evaluate(collection) as IEnumerable;
        var placeholders = values?.Cast<object?>().Select(_parameters.Value).ToList() ?? new List<string>();
        return placeholders.Count == 0 ? "(1 = 0)" : $"({col} IN ({string.Join(", ", placeholders)}))";
    }

    private static string? DatePart(string memberName) => memberName switch
    {
        "Year" => "YEAR",
        "Month" => "MONTH",
        "Day" => "DAY",
        "Hour" => "HOUR",
        "Minute" => "MINUTE",
        "Second" => "SECOND",
        _ => null
    };

    private static bool IsNull(Expression e)
        => e is ConstantExpression { Value: null }
        || (e is UnaryExpression { NodeType: ExpressionType.Convert } u && u.Operand is ConstantExpression { Value: null });

    private static object? Evaluate(Expression e) => ExpressionEval.Eval(e);

    private static Expression UnwrapConversions(Expression e)
    {
        while (true)
        {
            switch (e)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u:
                    e = u.Operand;
                    break;
                case MethodCallExpression { Method.Name: "op_Implicit" or "op_Explicit", Arguments.Count: 1 } mc:
                    e = mc.Arguments[0];
                    break;
                default:
                    return e;
            }
        }
    }

    private NotSupportedException Unsupported(Expression e) => new(
        $"DeltaLinq cannot translate '{e}' to SQL. " +
        "Rewrite it into a pushdown-able form, or materialize first with ToListAsync() and continue " +
        "with LINQ-to-Objects in memory. DeltaLinq deliberately does not client-evaluate silently.");
}
