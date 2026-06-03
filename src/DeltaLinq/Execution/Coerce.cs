using System.Globalization;

namespace DeltaLinq;

/// <summary>Coerces a value returned by DuckDB into the target CLR property/parameter type.</summary>
internal static class Coerce
{
    public static object? To(object? value, Type target)
    {
        if (value is null or DBNull)
            return target.IsValueType && Nullable.GetUnderlyingType(target) is null
                ? Activator.CreateInstance(target)
                : null;

        var t = Nullable.GetUnderlyingType(target) ?? target;

        if (t.IsInstanceOfType(value)) return value;

        if (t.IsEnum)
            return value is string es
                ? Enum.Parse(t, es, ignoreCase: true)
                : Enum.ToObject(t, Convert.ToInt64(value, CultureInfo.InvariantCulture));

        if (t == typeof(DateTime))
            return value switch
            {
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                DateTime dt => dt,
                DateTimeOffset dto => dto.DateTime,
                _ => DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture)
            };

        if (t == typeof(DateOnly))
            return value switch
            {
                DateOnly d => d,
                DateTime dt => DateOnly.FromDateTime(dt),
                _ => DateOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture)
            };

        if (t == typeof(Guid))
            return value is Guid g ? g : Guid.Parse(value.ToString()!);

        if (t == typeof(string))
            return value.ToString();

        try
        {
            return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
        }
        catch (InvalidCastException)
        {
            // DuckDB may return non-IConvertible numerics (e.g. Int128/BigInteger from SUM over a
            // HUGEINT). Round-trip through decimal, which parses their string form.
            if (IsNumeric(t))
                return Convert.ChangeType(decimal.Parse(value.ToString()!, CultureInfo.InvariantCulture), t, CultureInfo.InvariantCulture);
            throw;
        }
    }

    private static bool IsNumeric(Type t)
        => t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
        || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
}
