using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text.Json;
using DeltaLinq;
using DuckDB.NET.Data;

namespace DeltaLinq.Tests;

/// <summary>Test entity. Exercises [Column] rename, [NotMapped], and a bool column.</summary>
public class User
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    [Column("signup_date")] public DateTime SignupDate { get; set; }
    public double Price { get; set; }
    public bool IsActive { get; set; }
    [NotMapped] public string DisplayName => $"{Name} ({Country})";
}

/// <summary>Orders, related to <see cref="User"/> by UserId — used for join tests.</summary>
public class Order
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Product { get; set; } = "";
    public double Total { get; set; }
}

/// <summary>Country reference table, related to <see cref="User"/> by Country == Code — used for multi-table join tests.</summary>
public class Country
{
    public string Code { get; set; } = "";
    public string CountryName { get; set; } = "";
    public string Region { get; set; } = "";
}

/// <summary>
/// Builds a real, on-disk Delta table (Parquet written by DuckDB + a hand-written version-0
/// transaction log) once per test run, and exposes the known dataset for assertions.
/// </summary>
public static class DeltaTestData
{
    public sealed record Row(long Id, string Name, string Country, string SignupDate, double Price, bool IsActive);

    public static readonly Row[] Rows =
    {
        new(1,  "Alice",   "NL", "2023-05-01", 150.0, true),
        new(2,  "Bram",    "NL", "2022-11-20",  90.0, false),
        new(3,  "Carlos",  "ES", "2023-08-15", 220.0, true),
        new(4,  "Diana",   "NL", "2024-01-10", 310.0, true),
        new(5,  "Erik",    "DE", "2023-03-30",  75.0, false),
        new(6,  "Femke",   "NL", "2023-12-05", 175.0, true),
        new(7,  "Gustav",  "DE", "2024-02-18", 125.0, true),
        new(8,  "Hana",    "NL", "2021-07-22", 410.0, false),
        new(9,  "Ivan",    "ES", "2023-09-09",  60.0, true),
        new(10, "Janneke", "NL", "2024-03-01", 200.0, true),
    };

    /// <summary>True if the DuckDB delta extension can load (else integration tests are skipped).</summary>
    public static bool DeltaAvailable { get; } = DeltaEnvironment.IsDeltaExtensionAvailable();

    public static readonly (long Id, long UserId, string Product, double Total)[] OrderRows =
    {
        (101, 1,  "Book",     29.99),
        (102, 1,  "Pen",       4.50),
        (103, 4,  "Laptop",  999.00),
        (104, 6,  "Mouse",    19.99),
        (105, 10, "Monitor", 199.00),
        (106, 3,  "Book",     29.99),
        (107, 7,  "Cable",     9.99),
        (108, 1,  "Notebook", 12.00),
    };

    public static readonly (string Code, string CountryName, string Region)[] CountryRows =
    {
        ("NL", "Netherlands", "Europe"),
        ("ES", "Spain", "Europe"),
        ("DE", "Germany", "Europe"),
    };

    private static readonly Lazy<string> LazyUsers = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> LazyOrders = new(CreateOrders, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> LazyCountries = new(CreateCountries, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Path to the shared, read-only users Delta table.</summary>
    public static string UsersTable => LazyUsers.Value;

    /// <summary>Path to the shared, read-only orders Delta table.</summary>
    public static string OrdersTable => LazyOrders.Value;

    /// <summary>Path to the shared, read-only countries Delta table.</summary>
    public static string CountriesTable => LazyCountries.Value;

    private static string Create()
    {
        var tableDir = Path.Combine(Path.GetTempPath(), "deltalinq_tests", "users_v1");
        var logDir = Path.Combine(tableDir, "_delta_log");
        var commit0 = Path.Combine(logDir, "00000000000000000000.json");
        if (File.Exists(commit0)) return tableDir;

        Directory.CreateDirectory(logDir);
        var parquetPath = Path.Combine(tableDir, "part-00000.parquet");

        var values = string.Join(",", Rows.Select(r =>
            $"({r.Id},'{r.Name}','{r.Country}','{r.SignupDate}'," +
            $"{r.Price.ToString(CultureInfo.InvariantCulture)},{(r.IsActive ? "true" : "false")})"));

        var copy =
            "COPY (SELECT " +
            "CAST(c0 AS BIGINT)  AS \"Id\", " +
            "CAST(c1 AS VARCHAR) AS \"Name\", " +
            "CAST(c2 AS VARCHAR) AS \"Country\", " +
            "CAST(c3 AS DATE)    AS \"signup_date\", " +
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
                new { name = "signup_date", type = "date",    nullable = true, metadata = new { } },
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

    private static string CreateOrders()
    {
        var tableDir = Path.Combine(Path.GetTempPath(), "deltalinq_tests", "orders_v1");
        var logDir = Path.Combine(tableDir, "_delta_log");
        var commit0 = Path.Combine(logDir, "00000000000000000000.json");
        if (File.Exists(commit0)) return tableDir;

        Directory.CreateDirectory(logDir);
        var parquetPath = Path.Combine(tableDir, "part-00000.parquet");

        var values = string.Join(",", OrderRows.Select(r =>
            $"({r.Id},{r.UserId},'{r.Product}',{r.Total.ToString(CultureInfo.InvariantCulture)})"));

        var copy =
            "COPY (SELECT " +
            "CAST(c0 AS BIGINT)  AS \"Id\", " +
            "CAST(c1 AS BIGINT)  AS \"UserId\", " +
            "CAST(c2 AS VARCHAR) AS \"Product\", " +
            "CAST(c3 AS DOUBLE)  AS \"Total\" " +
            $"FROM (VALUES {values}) AS t(c0,c1,c2,c3)) " +
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
                new { name = "Id",      type = "long",   nullable = true, metadata = new { } },
                new { name = "UserId",  type = "long",   nullable = true, metadata = new { } },
                new { name = "Product", type = "string", nullable = true, metadata = new { } },
                new { name = "Total",   type = "double", nullable = true, metadata = new { } },
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

    private static string CreateCountries()
    {
        var tableDir = Path.Combine(Path.GetTempPath(), "deltalinq_tests", "countries_v1");
        var logDir = Path.Combine(tableDir, "_delta_log");
        var commit0 = Path.Combine(logDir, "00000000000000000000.json");
        if (File.Exists(commit0)) return tableDir;

        Directory.CreateDirectory(logDir);
        var parquetPath = Path.Combine(tableDir, "part-00000.parquet");

        var values = string.Join(",", CountryRows.Select(r => $"('{r.Code}','{r.CountryName}','{r.Region}')"));

        var copy =
            "COPY (SELECT " +
            "CAST(c0 AS VARCHAR) AS \"Code\", " +
            "CAST(c1 AS VARCHAR) AS \"CountryName\", " +
            "CAST(c2 AS VARCHAR) AS \"Region\" " +
            $"FROM (VALUES {values}) AS t(c0,c1,c2)) " +
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
                new { name = "Code",        type = "string", nullable = true, metadata = new { } },
                new { name = "CountryName", type = "string", nullable = true, metadata = new { } },
                new { name = "Region",      type = "string", nullable = true, metadata = new { } },
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
