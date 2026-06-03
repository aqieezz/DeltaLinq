namespace DeltaLinq;

/// <summary>Environment checks for the embedded DuckDB engine.</summary>
public static class DeltaEnvironment
{
    /// <summary>
    /// True if the DuckDB <c>delta</c> extension can be installed/loaded with the given options.
    /// First call may download the extension (needs network) unless a custom extension directory is set.
    /// </summary>
    public static bool IsDeltaExtensionAvailable(DeltaOptions? options = null)
        => new DuckDbExecutor(options ?? DeltaOptions.Default).ProbeDeltaExtension();
}
