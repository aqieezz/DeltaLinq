namespace DeltaLinq;

/// <summary>
/// A lightweight, EF-Core-flavoured context holding shared <see cref="DeltaOptions"/> for opening
/// multiple tables. Read-only: there is no change tracking or SaveChanges.
/// </summary>
public class DeltaContext
{
    private readonly DeltaOptions _options;

    public DeltaContext(DeltaOptions? options = null) => _options = options ?? DeltaOptions.Default;

    /// <summary>Opens a table using this context's options.</summary>
    public IQueryable<T> Table<T>(string path) => DeltaTable.Open<T>(path, _options);
}
