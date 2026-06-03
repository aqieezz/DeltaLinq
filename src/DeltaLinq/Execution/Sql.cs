using System.Globalization;

namespace DeltaLinq;

/// <summary>
/// SQL string helpers: identifier quoting and safe literal/path formatting.
/// All values destined for SQL go through <see cref="Literal"/> so single quotes are escaped
/// (the only injection defense needed when inlining literals into DuckDB SQL).
/// </summary>
internal static class Sql
{
    public static string Quote(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string Str(string value)
        => "'" + value.Replace("'", "''") + "'";

    public static string Path(string path)
        => "'" + path.Replace('\\', '/').Replace("'", "''") + "'";

    public static string Literal(object? value) => value switch
    {
        null => "NULL",
        string s => Str(s),
        char c => Str(c.ToString()),
        bool b => b ? "TRUE" : "FALSE",
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        DateTime dt when dt.TimeOfDay == TimeSpan.Zero => $"DATE '{dt:yyyy-MM-dd}'",
        DateTime dt => $"TIMESTAMP '{dt:yyyy-MM-dd HH:mm:ss.fff}'",
        DateOnly d => $"DATE '{d:yyyy-MM-dd}'",
        TimeOnly t => $"TIME '{t:HH:mm:ss.fff}'",
        DateTimeOffset dto => $"TIMESTAMPTZ '{dto:yyyy-MM-dd HH:mm:ss.fffzzz}'",
        Guid g => Str(g.ToString()),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double db => db.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture), // int, long, short, byte, ...
        _ => Str(value.ToString() ?? "")
    };
}
