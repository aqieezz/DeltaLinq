using System.Linq.Expressions;

namespace DeltaLinq;

/// <summary>
/// Resolves member access in a lambda to SQL. Implementations differ by query shape: a single table,
/// a join's two-entity result selector, or a post-join composite row.
/// </summary>
internal interface IScope
{
    /// <summary>True (with <paramref name="sql"/> set) if the member access is a column reference.</summary>
    bool TryColumn(MemberExpression member, out string sql);

    /// <summary>True if the expression references a row parameter of this scope.</summary>
    bool ReferencesRow(Expression expression);
}

/// <summary>Single table: <c>x.Col</c> → <c>"Col"</c> (or <c>alias."Col"</c> inside a join).</summary>
internal sealed class SingleScope : IScope
{
    private readonly EntityModel _entity;
    private readonly ParameterExpression _row;
    private readonly string? _alias;

    public SingleScope(EntityModel entity, ParameterExpression row, string? alias = null)
    {
        _entity = entity;
        _row = row;
        _alias = alias;
    }

    public bool TryColumn(MemberExpression member, out string sql)
    {
        if (member.Expression == _row && _entity.TryColumn(member.Member, out var column))
        {
            sql = Qualify(_alias, column);
            return true;
        }
        sql = null!;
        return false;
    }

    public bool ReferencesRow(Expression expression) => ScopeUtil.References(expression, _row);

    public static string Qualify(string? alias, string column)
        => alias is null ? Sql.Quote(column) : $"{alias}.{Sql.Quote(column)}";
}

/// <summary>
/// A post-join composite row bound to named members. A member is either a whole source (so
/// <c>x.src.Col</c> navigates to <c>alias."Col"</c>) or a precomputed scalar SQL expression
/// (so <c>x.scalar</c> → that SQL).
/// </summary>
internal sealed class JoinScope : IScope
{
    private readonly IReadOnlyDictionary<string, Binding> _members;
    private readonly ParameterExpression _row;

    public JoinScope(IReadOnlyDictionary<string, Binding> members, ParameterExpression row)
    {
        _members = members;
        _row = row;
    }

    public bool TryColumn(MemberExpression member, out string sql)
    {
        // x.scalar
        if (member.Expression == _row && _members.TryGetValue(member.Member.Name, out var b) && b is SqlBinding scalar)
        {
            sql = scalar.Sql;
            return true;
        }

        // x.source.Col
        if (member.Expression is MemberExpression inner && inner.Expression == _row
            && _members.TryGetValue(inner.Member.Name, out var b2) && b2 is SourceBinding source
            && source.Source.Model.TryColumn(member.Member, out var column))
        {
            sql = $"{source.Source.Alias}.{Sql.Quote(column)}";
            return true;
        }

        sql = null!;
        return false;
    }

    public bool ReferencesRow(Expression expression) => ScopeUtil.References(expression, _row);
}

/// <summary>One table participating in a join: its alias, <c>delta_scan(...) AS alias</c> SQL, model, join keyword, and ON clause.</summary>
internal sealed class JoinSource
{
    public required string Alias { get; init; }
    public required string TableSql { get; init; }
    public required EntityModel Model { get; init; }
    public string JoinKeyword { get; set; } = "JOIN"; // or "LEFT JOIN"
    public string? On { get; set; }
}

/// <summary>
/// Resolves a join result selector whose outer parameter is a prior (possibly multi-table) row and
/// whose inner parameter is a freshly added source. Inner member access wins; everything else
/// delegates to the outer scope.
/// </summary>
internal sealed class CombinedScope : IScope
{
    private readonly IScope _outer;
    private readonly JoinSource _inner;
    private readonly ParameterExpression _innerParam;

    public CombinedScope(IScope outer, JoinSource inner, ParameterExpression innerParam)
    {
        _outer = outer;
        _inner = inner;
        _innerParam = innerParam;
    }

    public bool TryColumn(MemberExpression member, out string sql)
    {
        if (member.Expression == _innerParam && _inner.Model.TryColumn(member.Member, out var column))
        {
            sql = $"{_inner.Alias}.{Sql.Quote(column)}";
            return true;
        }
        return _outer.TryColumn(member, out sql);
    }

    public bool ReferencesRow(Expression expression)
        => _outer.ReferencesRow(expression) || ScopeUtil.References(expression, _innerParam);
}

/// <summary>The outer side of a join, used to detect/forward whole-source references when flattening tuples.</summary>
internal sealed class OuterShape
{
    /// <summary>Set when the outer parameter itself is a single source (a base, non-chained join).</summary>
    public Binding? Whole { get; init; }

    /// <summary>Set when the outer is a prior join: member name → binding (source or scalar).</summary>
    public IReadOnlyDictionary<string, Binding>? Members { get; init; }
}

/// <summary>How a member of a post-join composite row is bound.</summary>
internal abstract class Binding;

internal sealed class SourceBinding : Binding
{
    public required JoinSource Source { get; init; }
}

internal sealed class SqlBinding : Binding
{
    public required string Sql { get; init; }
}

internal static class ScopeUtil
{
    public static bool References(Expression expression, params ParameterExpression[] targets)
    {
        var finder = new Finder(targets);
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class Finder : ExpressionVisitor
    {
        private readonly ParameterExpression[] _targets;
        public bool Found;
        public Finder(ParameterExpression[] targets) => _targets = targets;

        public override Expression? Visit(Expression? node) => Found ? node : base.Visit(node);

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (Array.IndexOf(_targets, node) >= 0) Found = true;
            return node;
        }
    }
}
