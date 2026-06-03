using System.Collections;
using System.Linq.Expressions;

namespace DeltaLinq;

/// <summary>
/// The <see cref="IQueryable{T}"/> users chain operators on. It carries only a provider and an
/// expression tree; nothing executes until the query is enumerated or awaited.
/// </summary>
internal sealed class DeltaQueryable<T> : IOrderedQueryable<T>
{
    public DeltaQueryable(DeltaQueryProvider provider)
    {
        Provider = provider;
        Expression = Expression.Constant(this);
    }

    public DeltaQueryable(DeltaQueryProvider provider, Expression expression)
    {
        Provider = provider;
        Expression = expression;
    }

    public DeltaQueryProvider Provider { get; }
    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    IQueryProvider IQueryable.Provider => Provider;

    public IEnumerator<T> GetEnumerator() => Provider.ExecuteList<T>(Expression).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
