namespace DeltaLinq;

/// <summary>
/// Factory for opening Delta tables with preconfigured options. Registered by the
/// DeltaLinq.DependencyInjection package and injected into services/endpoints.
/// </summary>
public interface IDeltaTableFactory
{
    IQueryable<T> Open<T>(string path);
    IQueryable<T> Open<T>(string path, DeltaOptions options);

    /// <summary>Appends rows to a Delta table using this factory's options.</summary>
    Task<int> AppendAsync<T>(string path, IEnumerable<T> rows, CancellationToken ct = default);
}

/// <summary>Default <see cref="IDeltaTableFactory"/> bound to a single <see cref="DeltaOptions"/>.</summary>
public sealed class DeltaTableFactory : IDeltaTableFactory
{
    private readonly DeltaOptions _options;

    public DeltaTableFactory(DeltaOptions options) => _options = options;

    public IQueryable<T> Open<T>(string path) => DeltaTable.Open<T>(path, _options);
    public IQueryable<T> Open<T>(string path, DeltaOptions options) => DeltaTable.Open<T>(path, options);

    public Task<int> AppendAsync<T>(string path, IEnumerable<T> rows, CancellationToken ct = default)
        => DeltaTable.AppendAsync(path, rows, _options, ct);
}
