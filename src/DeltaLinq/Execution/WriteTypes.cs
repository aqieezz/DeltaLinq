namespace DeltaLinq;

/// <summary>
/// Maps CLR property types to the DuckDB SQL type used when writing Parquet and the matching Delta
/// logical type written into the transaction log. The two must agree so the Parquet physical type
/// lines up with the Delta schema.
/// </summary>
internal static class WriteTypes
{
    public static string DuckDbType(Type type) => Map(type).Duck;

    public static string DeltaType(Type type) => Map(type).Delta;

    private static (string Duck, string Delta) Map(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsEnum) return ("BIGINT", "long");

        if (t == typeof(long)) return ("BIGINT", "long");
        if (t == typeof(int)) return ("INTEGER", "integer");
        if (t == typeof(short)) return ("SMALLINT", "short");
        if (t == typeof(byte)) return ("SMALLINT", "short");
        if (t == typeof(sbyte)) return ("TINYINT", "byte");
        if (t == typeof(double)) return ("DOUBLE", "double");
        if (t == typeof(float)) return ("FLOAT", "float");
        if (t == typeof(decimal)) return ("DECIMAL(38,10)", "decimal(38,10)");
        if (t == typeof(bool)) return ("BOOLEAN", "boolean");
        if (t == typeof(string)) return ("VARCHAR", "string");
        if (t == typeof(Guid)) return ("VARCHAR", "string");
        if (t == typeof(DateTime)) return ("TIMESTAMP", "timestamp");
        if (t == typeof(DateOnly)) return ("DATE", "date");

        throw new NotSupportedException(
            $"DeltaLinq cannot write columns of type '{t.Name}'. Supported write types: " +
            "integer/floating/decimal numerics, bool, string, Guid, DateTime, DateOnly, and enums.");
    }
}
