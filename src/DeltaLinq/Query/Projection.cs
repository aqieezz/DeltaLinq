using System.Linq.Expressions;
using System.Reflection;

namespace DeltaLinq;

/// <summary>
/// The SELECT list plus a builder that reconstructs a CLR object from a result row. Rows are read
/// positionally, so the SQL expressions and the builder stay in lockstep regardless of column names.
/// </summary>
internal sealed class Projection
{
    public required Type ResultType { get; init; }
    public required IReadOnlyList<string> SelectExprs { get; init; }
    public required Func<object?[], object?> Build { get; init; }

    /// <summary>No Select(...): return whole rows mapped onto the entity by column.</summary>
    public static Projection Identity(EntityModel entity)
    {
        var columns = entity.Columns;
        return new Projection
        {
            ResultType = entity.Type,
            SelectExprs = columns.Select(c => Sql.Quote(c.ColumnName)).ToList(),
            Build = values =>
            {
                var obj = Activator.CreateInstance(entity.Type)!;
                for (var i = 0; i < columns.Count; i++)
                    columns[i].Property.SetValue(obj, Coerce.To(values[i], columns[i].Property.PropertyType));
                return obj;
            }
        };
    }

    public static Projection FromSelector(EntityModel entity, LambdaExpression selector, SqlParameters parameters)
        => FromSelector(new SingleScope(entity, selector.Parameters[0]), selector, parameters, out _);

    /// <summary>
    /// Builds a projection from a selector rendered against <paramref name="scope"/> (single-table,
    /// join result selector, or post-join row). <paramref name="memberSql"/> maps each projected
    /// member name to its SQL — used to resolve post-projection ordering/filtering.
    /// </summary>
    public static Projection FromSelector(IScope scope, LambdaExpression selector, SqlParameters parameters, out Dictionary<string, string> memberSql)
    {
        var sql = new ExpressionToSql(scope, parameters);
        memberSql = new Dictionary<string, string>();

        switch (selector.Body)
        {
            // x => new { x.Id, x.Price }  (anonymous type: single positional constructor)
            case NewExpression n when n.Members is not null:
            {
                var ctor = n.Constructor!;
                var pars = ctor.GetParameters();
                var exprs = n.Arguments.Select(sql.Render).ToList();
                for (var i = 0; i < exprs.Count; i++) memberSql[n.Members[i].Name] = exprs[i];
                return new Projection
                {
                    ResultType = n.Type,
                    SelectExprs = exprs,
                    Build = values => ctor.Invoke(values.Select((v, i) => Coerce.To(v, pars[i].ParameterType)).ToArray())
                };
            }

            // x => new Dto { A = x.B, ... }
            case MemberInitExpression mi:
            {
                var assignments = mi.Bindings.OfType<MemberAssignment>().ToArray();
                var ctor = mi.NewExpression.Constructor!;
                var exprs = assignments.Select(a => sql.Render(a.Expression)).ToList();
                for (var i = 0; i < exprs.Count; i++) memberSql[assignments[i].Member.Name] = exprs[i];
                return new Projection
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
            }

            // x => x.Name  /  x => x.Price * 2  (scalar)
            default:
            {
                var resultType = selector.Body.Type;
                return new Projection
                {
                    ResultType = resultType,
                    SelectExprs = new[] { sql.Render(selector.Body) },
                    Build = values => Coerce.To(values[0], resultType)
                };
            }
        }
    }
}
