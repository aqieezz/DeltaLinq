namespace DeltaLinq;

/// <summary>
/// Collects bound parameters for one query. Strings (the injection-prone case) plus numeric and
/// boolean values become named placeholders ($p0, $p1, …); <c>null</c> and less common types
/// (dates, GUIDs, enums) are emitted as typed SQL literals so their column type stays unambiguous.
/// </summary>
internal sealed class SqlParameters
{
    private readonly List<KeyValuePair<string, object?>> _items = new();

    public IReadOnlyList<KeyValuePair<string, object?>> Items => _items;

    /// <summary>Returns the SQL fragment for a value — a <c>$pN</c> placeholder or an inline literal.</summary>
    public string Value(object? value)
    {
        if (value is null) return "NULL";
        if (IsParameterizable(value))
        {
            var name = "p" + _items.Count;
            _items.Add(new KeyValuePair<string, object?>(name, value));
            return "$" + name;
        }
        return Sql.Literal(value);
    }

    private static bool IsParameterizable(object value) => value
        is string or bool
        or sbyte or byte or short or ushort or int or uint or long or ulong
        or float or double or decimal;
}
