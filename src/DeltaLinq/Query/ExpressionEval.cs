using System.Linq.Expressions;
using System.Reflection;

namespace DeltaLinq;

/// <summary>
/// Evaluates a parameter-independent subtree (captured variables, constants, <c>new DateTime(...)</c>, etc.)
/// to its runtime value. Constants, field/property access, and constructors are evaluated directly via
/// reflection — no <c>Expression.Compile()</c> at all — which covers the overwhelmingly common cases.
/// Anything else falls back to the expression <em>interpreter</em> (no IL emission), sidestepping
/// runtime codegen quirks (e.g. the .NET 9.0.16 DynamicInvoke / InvalidProgram regressions).
/// </summary>
internal static class ExpressionEval
{
    public static object? Eval(Expression e)
    {
        switch (e)
        {
            case ConstantExpression c:
                return c.Value;
            case MemberExpression { Member: FieldInfo field } m:
                return field.GetValue(m.Expression is null ? null : Eval(m.Expression));
            case MemberExpression { Member: PropertyInfo prop } m:
                return prop.GetValue(m.Expression is null ? null : Eval(m.Expression));
            case NewExpression n:
                return n.Constructor is null
                    ? Activator.CreateInstance(n.Type)
                    : n.Constructor.Invoke(n.Arguments.Select(Eval).ToArray());
            default:
                return Expression.Lambda<Func<object?>>(Expression.Convert(e, typeof(object)))
                    .Compile(preferInterpretation: true)();
        }
    }
}
