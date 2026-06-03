namespace DeltaLinq;

/// <summary>Entry point for querying a Delta Lake table with LINQ.</summary>
public static class DeltaTable
{
    /// <summary>
    /// Opens a Delta table at <paramref name="path"/> (local path or <c>s3://</c>, <c>az://</c>,
    /// <c>gs://</c> URI) as an <see cref="IQueryable{T}"/>. No I/O happens until the query is run.
    /// </summary>
    public static IQueryable<T> Open<T>(string path, DeltaOptions? options = null)
        => new DeltaQueryable<T>(new DeltaQueryProvider(path, options ?? DeltaOptions.Default));

    /// <summary>
    /// Appends <paramref name="rows"/> to the Delta table at <paramref name="path"/> as a new commit,
    /// creating the table if it does not exist. Returns the number of rows written. Single-writer; on an
    /// existing table the entity's columns must match the table schema.
    /// </summary>
    public static int Append<T>(string path, IEnumerable<T> rows, DeltaOptions? options = null)
        => DeltaWriter.Append(path, EntityModel.For(typeof(T)),
            rows.Cast<object>().ToList(), new DuckDbExecutor(options ?? DeltaOptions.Default));

    /// <inheritdoc cref="Append{T}(string, IEnumerable{T}, DeltaOptions?)"/>
    public static Task<int> AppendAsync<T>(string path, IEnumerable<T> rows, DeltaOptions? options = null, CancellationToken ct = default)
        => Task.Run(() => Append(path, rows, options), ct);
}
