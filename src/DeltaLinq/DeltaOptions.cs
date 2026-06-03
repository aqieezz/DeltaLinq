namespace DeltaLinq;

/// <summary>Configuration for opening and querying Delta tables.</summary>
public sealed class DeltaOptions
{
    /// <summary>Shared default (local filesystem, no logging).</summary>
    public static DeltaOptions Default { get; } = new();

    /// <summary>Object-store credentials. Null = local filesystem / ambient credentials.</summary>
    public StorageOptions? Storage { get; set; }

    /// <summary>Optional sink for the generated SQL of every executed query — great for debugging.</summary>
    public Action<string>? OnSql { get; set; }

    /// <summary>DuckDB connection string. Defaults to a private in-memory database.</summary>
    public string ConnectionString { get; set; } = "DataSource=:memory:";

    /// <summary>Install missing DuckDB extensions automatically (needs network on first use).</summary>
    public bool AutoInstallExtensions { get; set; } = true;

    /// <summary>Custom DuckDB extension directory (for air-gapped/offline deployments).</summary>
    public string? ExtensionDirectory { get; set; }

    /// <summary>Allow loading unsigned extensions (required when using a custom extension directory).</summary>
    public bool AllowUnsignedExtensions { get; set; }
}
