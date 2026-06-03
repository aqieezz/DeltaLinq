# DeltaLinq.DependencyInjection

`Microsoft.Extensions.DependencyInjection` integration for [DeltaLinq](https://www.nuget.org/packages/DeltaLinq).

```csharp
using DeltaLinq;

builder.Services.AddDeltaLake(options =>
{
    options.Storage = new S3StorageOptions { Region = "eu-west-1" };
});

// Then inject IDeltaTableFactory anywhere:
app.MapGet("/users/nl", async (IDeltaTableFactory delta) =>
    await delta.Open<User>("s3://bucket/users")
               .Where(x => x.Country == "NL")
               .Take(100)
               .ToListAsync());
```

See the [project repository](https://github.com/aqieezz/DeltaLinq) for details.
