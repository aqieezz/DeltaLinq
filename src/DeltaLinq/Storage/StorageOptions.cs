namespace DeltaLinq;

/// <summary>
/// Base for object-store credentials. Each concrete option set declares which DuckDB extensions it
/// needs and the <c>CREATE SECRET</c> statement that authenticates the underlying engine.
/// </summary>
public abstract class StorageOptions
{
    internal const string SecretName = "deltalinq_secret";

    /// <summary>DuckDB extensions that must be loaded for this storage backend (e.g. httpfs, azure).</summary>
    public abstract IReadOnlyList<string> RequiredExtensions { get; }

    /// <summary>The <c>CREATE SECRET</c> statement, or null to rely on ambient/default credentials.</summary>
    public abstract string? CreateSecretSql();
}

/// <summary>AWS S3 (and S3-compatible) storage. Omit keys to use the ambient credential chain.</summary>
public sealed class S3StorageOptions : StorageOptions
{
    public string? Region { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? SessionToken { get; set; }
    public string? Endpoint { get; set; }
    public bool? UseSsl { get; set; }
    /// <summary>"vhost" (default) or "path" — set "path" for MinIO and most S3-compatible stores.</summary>
    public string? UrlStyle { get; set; }

    public override IReadOnlyList<string> RequiredExtensions { get; } = new[] { "httpfs" };

    public override string CreateSecretSql()
    {
        var parts = new List<string> { "TYPE S3" };
        if (AccessKeyId is not null)
        {
            parts.Add($"KEY_ID {Sql.Str(AccessKeyId)}");
            parts.Add($"SECRET {Sql.Str(SecretAccessKey ?? "")}");
        }
        else
        {
            parts.Add("PROVIDER credential_chain");
        }
        if (Region is not null) parts.Add($"REGION {Sql.Str(Region)}");
        if (SessionToken is not null) parts.Add($"SESSION_TOKEN {Sql.Str(SessionToken)}");
        if (Endpoint is not null) parts.Add($"ENDPOINT {Sql.Str(Endpoint)}");
        if (UseSsl is not null) parts.Add($"USE_SSL {(UseSsl.Value ? "true" : "false")}");
        if (UrlStyle is not null) parts.Add($"URL_STYLE {Sql.Str(UrlStyle)}");
        return $"CREATE OR REPLACE SECRET {SecretName} ({string.Join(", ", parts)})";
    }
}

/// <summary>Azure Data Lake Storage / Blob Storage.</summary>
public sealed class AzureStorageOptions : StorageOptions
{
    public string? ConnectionString { get; set; }
    public string? AccountName { get; set; }

    public override IReadOnlyList<string> RequiredExtensions { get; } = new[] { "azure" };

    public override string CreateSecretSql()
    {
        var parts = new List<string> { "TYPE AZURE" };
        if (ConnectionString is not null)
        {
            parts.Add($"CONNECTION_STRING {Sql.Str(ConnectionString)}");
        }
        else
        {
            parts.Add("PROVIDER credential_chain");
            if (AccountName is not null) parts.Add($"ACCOUNT_NAME {Sql.Str(AccountName)}");
        }
        return $"CREATE OR REPLACE SECRET {SecretName} ({string.Join(", ", parts)})";
    }
}

/// <summary>Google Cloud Storage via HMAC interoperability keys.</summary>
public sealed class GcsStorageOptions : StorageOptions
{
    public string? KeyId { get; set; }
    public string? Secret { get; set; }

    public override IReadOnlyList<string> RequiredExtensions { get; } = new[] { "httpfs" };

    public override string? CreateSecretSql()
    {
        if (KeyId is null) return null;
        return $"CREATE OR REPLACE SECRET {SecretName} (TYPE GCS, KEY_ID {Sql.Str(KeyId)}, SECRET {Sql.Str(Secret ?? "")})";
    }
}
