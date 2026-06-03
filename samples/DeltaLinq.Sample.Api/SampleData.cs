using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;

namespace DeltaLinq.Sample.Api;

/// <summary>
/// Seeds a small demo Delta table (Parquet written by DuckDB + a hand-written version-0 transaction
/// log) into the temp directory so the sample runs with no external setup. A real app would point
/// DeltaLinq at an existing Delta table on disk or in object storage instead.
/// </summary>
public static class SampleData
{
    private static readonly (long Id, string Name, string Country, string Date, double Price, bool Active)[] Rows =
    {
        (1,  "Alice",   "NL", "2023-05-01", 150.0, true),
        (2,  "Bram",    "NL", "2022-11-20",  90.0, false),
        (3,  "Carlos",  "ES", "2023-08-15", 220.0, true),
        (4,  "Diana",   "NL", "2024-01-10", 310.0, true),
        (5,  "Erik",    "DE", "2023-03-30",  75.0, false),
        (6,  "Femke",   "NL", "2023-12-05", 175.0, true),
        (7,  "Gustav",  "DE", "2024-02-18", 125.0, true),
        (8,  "Hana",    "NL", "2021-07-22", 410.0, false),
        (9,  "Ivan",    "ES", "2023-09-09",  60.0, true),
        (10, "Janneke", "NL", "2024-03-01", 200.0, true),
    };

    public static string EnsureCreated()
    {
        var tableDir = Path.Combine(Path.GetTempPath(), "deltalinq_sample", "users");
        var logDir = Path.Combine(tableDir, "_delta_log");
        var commit0 = Path.Combine(logDir, "00000000000000000000.json");
        if (File.Exists(commit0)) return tableDir;

        Directory.CreateDirectory(logDir);
        var parquetPath = Path.Combine(tableDir, "part-00000.parquet");

        var values = string.Join(",", Rows.Select(r =>
            $"({r.Id},'{r.Name}','{r.Country}','{r.Date}'," +
            $"{r.Price.ToString(CultureInfo.InvariantCulture)},{(r.Active ? "true" : "false")})"));

        var copy =
            "COPY (SELECT " +
            "CAST(c0 AS BIGINT)  AS \"Id\", " +
            "CAST(c1 AS VARCHAR) AS \"Name\", " +
            "CAST(c2 AS VARCHAR) AS \"Country\", " +
            "CAST(c3 AS TIMESTAMP) AS \"signup_date\", " +
            "CAST(c4 AS DOUBLE)  AS \"Price\", " +
            "CAST(c5 AS BOOLEAN) AS \"IsActive\" " +
            $"FROM (VALUES {values}) AS t(c0,c1,c2,c3,c4,c5)) " +
            $"TO '{parquetPath.Replace('\\', '/')}' (FORMAT parquet);";

        using (var conn = new DuckDBConnection("DataSource=:memory:"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = copy;
            cmd.ExecuteNonQuery();
        }

        var size = new FileInfo(parquetPath).Length;
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var schemaString = JsonSerializer.Serialize(new
        {
            type = "struct",
            fields = new object[]
            {
                new { name = "Id",          type = "long",    nullable = true, metadata = new { } },
                new { name = "Name",        type = "string",  nullable = true, metadata = new { } },
                new { name = "Country",     type = "string",  nullable = true, metadata = new { } },
                new { name = "signup_date", type = "timestamp", nullable = true, metadata = new { } },
                new { name = "Price",       type = "double",  nullable = true, metadata = new { } },
                new { name = "IsActive",    type = "boolean", nullable = true, metadata = new { } },
            }
        });

        var protocol = JsonSerializer.Serialize(new { protocol = new { minReaderVersion = 1, minWriterVersion = 2 } });
        var metaData = JsonSerializer.Serialize(new
        {
            metaData = new
            {
                id = Guid.NewGuid().ToString(),
                format = new { provider = "parquet", options = new Dictionary<string, string>() },
                schemaString,
                partitionColumns = Array.Empty<string>(),
                configuration = new Dictionary<string, string>(),
                createdTime = ms
            }
        });
        var add = JsonSerializer.Serialize(new
        {
            add = new
            {
                path = "part-00000.parquet",
                partitionValues = new Dictionary<string, string>(),
                size,
                modificationTime = ms,
                dataChange = true
            }
        });

        File.WriteAllLines(commit0, new[] { protocol, metaData, add });
        return tableDir;
    }
}
