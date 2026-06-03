namespace DeltaLinq;

/// <summary>Thrown when a DeltaLinq query fails to execute against the engine.</summary>
public sealed class DeltaLinqException : Exception
{
    public DeltaLinqException(string message) : base(message) { }
    public DeltaLinqException(string message, Exception inner) : base(message, inner) { }

    /// <summary>The generated SQL that failed, when available.</summary>
    public string? Sql { get; init; }
}
