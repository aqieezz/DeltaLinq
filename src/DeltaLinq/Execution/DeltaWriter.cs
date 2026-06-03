using System.Text.Json;

namespace DeltaLinq;

/// <summary>
/// Appends rows to a Delta table by writing a new Parquet file and a new commit to the transaction
/// log — engine-independent, so it works even though the bundled DuckDB delta extension is read-only.
/// First write creates the table (protocol + metaData + add); later writes add a new <c>add</c> commit.
/// Single-writer only (no optimistic-concurrency retry); on append the entity must match the table schema.
/// </summary>
internal static class DeltaWriter
{
    public static int Append(string tablePath, EntityModel model, IReadOnlyList<object> rows, DuckDbExecutor executor)
    {
        if (rows.Count == 0) return 0;

        var logDir = Path.Combine(tablePath, "_delta_log");
        Directory.CreateDirectory(logDir);

        var version = NextVersion(logDir, out var firstCommit);
        var fileName = $"part-{Guid.NewGuid():N}.parquet";
        var parquetPath = Path.Combine(tablePath, fileName);

        var count = executor.WriteParquet(parquetPath, model.Columns, rows);
        if (count == 0) return 0;

        var size = new FileInfo(parquetPath).Length;
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var actions = new List<string>();
        if (firstCommit)
        {
            actions.Add(ProtocolJson());
            actions.Add(MetaDataJson(model, ms));
        }
        actions.Add(AddJson(fileName, size, ms));

        File.WriteAllLines(Path.Combine(logDir, $"{version:D20}.json"), actions);
        return count;
    }

    private static long NextVersion(string logDir, out bool firstCommit)
    {
        long max = -1;
        foreach (var file in Directory.GetFiles(logDir, "*.json"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(file), out var v))
                max = Math.Max(max, v);
        firstCommit = max < 0;
        return max + 1;
    }

    private static string ProtocolJson()
        => JsonSerializer.Serialize(new { protocol = new { minReaderVersion = 1, minWriterVersion = 2 } });

    private static string MetaDataJson(EntityModel model, long ms)
    {
        var fields = model.Columns
            .Select(c => new { name = c.ColumnName, type = WriteTypes.DeltaType(c.Property.PropertyType), nullable = true, metadata = new { } })
            .Cast<object>()
            .ToArray();

        var schemaString = JsonSerializer.Serialize(new { type = "struct", fields });

        return JsonSerializer.Serialize(new
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
    }

    private static string AddJson(string fileName, long size, long ms)
        => JsonSerializer.Serialize(new
        {
            add = new
            {
                path = fileName,
                partitionValues = new Dictionary<string, string>(),
                size,
                modificationTime = ms,
                dataChange = true
            }
        });
}
