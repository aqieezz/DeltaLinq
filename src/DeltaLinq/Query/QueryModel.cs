using System.Linq.Expressions;

namespace DeltaLinq;

/// <summary>The parsed shape of a LINQ query: source + WHERE/ORDER BY/paging/DISTINCT + projection (+ grouping).</summary>
internal sealed class QueryModel
{
    public Type SourceType { get; set; } = null!;
    public EntityModel Entity { get; set; } = null!;
    public SqlParameters Parameters { get; } = new();
    public List<string> Where { get; } = new();
    public List<string> OrderBy { get; } = new();
    public int? Limit { get; set; }
    public int? Offset { get; set; }
    public bool Distinct { get; set; }
    public Projection? Projection { get; set; }

    // Grouping
    public bool IsGrouped { get; set; }
    public List<string> GroupBy { get; } = new();
    public List<string> Having { get; } = new();
    public Dictionary<string, string> GroupedMembers { get; set; } = new();
    public LambdaExpression? KeySelector { get; set; }

    private Projection? _effective;
    public Projection EffectiveProjection => _effective ??= Projection ?? DeltaLinq.Projection.Identity(Entity);
}
