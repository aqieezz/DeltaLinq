using DeltaLinq;
using Xunit;

namespace DeltaLinq.Tests;

public class StorageOptionsTests
{
    [Fact]
    public void S3_with_keys_emits_secret()
    {
        var sql = new S3StorageOptions
        {
            Region = "eu-west-1",
            AccessKeyId = "AKIA",
            SecretAccessKey = "shh"
        }.CreateSecretSql();

        Assert.Contains("TYPE S3", sql);
        Assert.Contains("KEY_ID 'AKIA'", sql);
        Assert.Contains("SECRET 'shh'", sql);
        Assert.Contains("REGION 'eu-west-1'", sql);
    }

    [Fact]
    public void S3_without_keys_uses_credential_chain()
        => Assert.Contains("PROVIDER credential_chain", new S3StorageOptions { Region = "us-east-1" }.CreateSecretSql());

    [Fact]
    public void S3_requires_httpfs()
        => Assert.Contains("httpfs", new S3StorageOptions().RequiredExtensions);

    [Fact]
    public void Azure_connection_string_secret()
    {
        var sql = new AzureStorageOptions { ConnectionString = "DefaultEndpointsProtocol=https;..." }.CreateSecretSql();
        Assert.Contains("TYPE AZURE", sql);
        Assert.Contains("CONNECTION_STRING", sql);
    }

    [Fact]
    public void Azure_requires_azure_extension()
        => Assert.Contains("azure", new AzureStorageOptions().RequiredExtensions);

    [Fact]
    public void Gcs_with_hmac_keys()
    {
        var sql = new GcsStorageOptions { KeyId = "GOOG", Secret = "s" }.CreateSecretSql();
        Assert.NotNull(sql);
        Assert.Contains("TYPE GCS", sql);
        Assert.Contains("KEY_ID 'GOOG'", sql);
    }

    [Fact]
    public void Secret_values_are_escaped()
    {
        var sql = new S3StorageOptions { AccessKeyId = "a", SecretAccessKey = "x'y" }.CreateSecretSql();
        Assert.Contains("SECRET 'x''y'", sql);
    }
}
