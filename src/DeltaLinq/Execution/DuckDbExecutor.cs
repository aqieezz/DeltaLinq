using System.Data.Common;
using System.Runtime.CompilerServices;
using DuckDB.NET.Data;

namespace DeltaLinq;

/// <summary>
/// Thin DuckDB access layer. Opens connections, loads the <c>delta</c> extension (plus any storage
/// extensions), applies credentials, and runs SQL. The Delta protocol is handled entirely by the
/// engine — DeltaLinq never parses the transaction log itself.
/// </summary>
internal sealed class DuckDbExecutor
{
    private static readonly object InstallLock = new();
    private static readonly HashSet<string> Installed = new();

    private readonly DeltaOptions _options;

    public DuckDbExecutor(DeltaOptions options) => _options = options;

    public string TableSource(string path) => $"delta_scan({Sql.Path(path)})";

    public List<object?[]> Query(string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, parameters);
            using var reader = cmd.ExecuteReader();

            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                reader.GetValues(values!);
                rows.Add(values);
            }
            return rows;
        }
        catch (Exception ex) when (ex is not DeltaLinqException)
        {
            throw Wrap(ex, sql);
        }
    }

    public object? Scalar(string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, parameters);
            return cmd.ExecuteScalar();
        }
        catch (Exception ex) when (ex is not DeltaLinqException)
        {
            throw Wrap(ex, sql);
        }
    }

    public async IAsyncEnumerable<object?[]> StreamAsync(string sql, IReadOnlyList<KeyValuePair<string, object?>> parameters, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Bind(cmd, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            yield return values;
        }
    }

    /// <summary>Writes rows to a single Parquet file via DuckDB (VALUES + CAST). Returns the row count.</summary>
    public int WriteParquet(string parquetPath, IReadOnlyList<ColumnMapping> columns, IReadOnlyList<object> rows)
    {
        if (rows.Count == 0) return 0;

        var selects = columns.Select((c, i) => $"CAST(c{i} AS {WriteTypes.DuckDbType(c.Property.PropertyType)}) AS {Sql.Quote(c.ColumnName)}");
        var aliases = columns.Select((_, i) => $"c{i}");
        var values = string.Join(", ", rows.Select(o =>
            "(" + string.Join(", ", columns.Select(c => Sql.Literal(c.Property.GetValue(o)))) + ")"));

        var sql =
            $"COPY (SELECT {string.Join(", ", selects)} " +
            $"FROM (VALUES {values}) AS t({string.Join(", ", aliases)})) " +
            $"TO {Sql.Path(parquetPath)} (FORMAT parquet)";

        try
        {
            using var conn = Open();
            Exec(conn, sql);
            return rows.Count;
        }
        catch (Exception ex) when (ex is not DeltaLinqException)
        {
            throw new DeltaLinqException($"DeltaLinq write failed: {ex.Message}", ex) { Sql = sql };
        }
    }

    public bool ProbeDeltaExtension()
    {
        try
        {
            using var conn = new DuckDBConnection(_options.ConnectionString);
            conn.Open();
            ApplySettings(conn);
            LoadExtension(conn, "delta");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private DuckDBConnection Open()
    {
        var conn = new DuckDBConnection(_options.ConnectionString);
        conn.Open();
        try
        {
            ApplySettings(conn);
            LoadExtension(conn, "delta");
            if (_options.Storage is { } storage)
            {
                foreach (var ext in storage.RequiredExtensions)
                    LoadExtension(conn, ext);
                if (storage.CreateSecretSql() is { } secret)
                    Exec(conn, secret);
            }
        }
        catch
        {
            conn.Dispose();
            throw;
        }
        return conn;
    }

    private void ApplySettings(DuckDBConnection conn)
    {
        if (_options.ExtensionDirectory is { } dir)
            Exec(conn, $"SET extension_directory = {Sql.Str(dir)}");
        if (_options.AllowUnsignedExtensions)
            Exec(conn, "SET allow_unsigned_extensions = true");

        // Pin the session to UTC so TIMESTAMP values round-trip without a local-offset shift.
        try
        {
            LoadExtension(conn, "icu");
            Exec(conn, "SET TimeZone='UTC'");
        }
        catch
        {
            // icu unavailable (e.g. offline): timestamps then use the engine's default time zone.
        }
    }

    private void LoadExtension(DuckDBConnection conn, string name)
    {
        if (_options.AutoInstallExtensions)
        {
            bool firstTime;
            lock (InstallLock) firstTime = Installed.Add(name);
            if (firstTime)
            {
                try { Exec(conn, $"INSTALL {name}"); }
                catch { lock (InstallLock) Installed.Remove(name); throw; }
            }
        }
        Exec(conn, $"LOAD {name}");
    }

    private static void Exec(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(DbCommand cmd, IReadOnlyList<KeyValuePair<string, object?>> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
    }

    private static DeltaLinqException Wrap(Exception ex, string sql)
        => new($"DeltaLinq query failed: {ex.Message}{Environment.NewLine}SQL: {sql}", ex) { Sql = sql };
}
