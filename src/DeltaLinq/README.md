# DeltaLinq

**LINQ/EF-style querying for Delta Lake on .NET.** Write idiomatic C#; DeltaLinq translates your
`IQueryable` expression tree to SQL and pushes it down into an embedded
[DuckDB](https://duckdb.org) + `delta` engine. No Spark, no JVM, no Python.

```csharp
using DeltaLinq;

var users = await DeltaTable.Open<User>("s3://bucket/users")
    .Where(x => x.Country == "NL" && x.SignupDate > new DateTime(2023, 1, 1))
    .OrderBy(x => x.Name)
    .Take(100)
    .ToListAsync();
```

## Features

- **LINQ provider** with predicate pushdown, projection/column pruning, ordering, `Take`/`Skip`, `Distinct`.
- **GroupBy + grouped aggregates** → SQL `GROUP BY` / `HAVING` (single & composite keys; `Count`/`Sum`/`Min`/`Max`/`Average`).
- **Joins** — `Join` (inner) and `LeftJoin`, single & composite keys, **2+ tables** (chained), aggregates over joins.
- **Append writes** — `DeltaTable.AppendAsync<T>(path, rows)` commits new rows (creates the table if missing).
- **Parameterized SQL** — string/numeric/boolean values are bound as parameters, not inlined.
- **Async terminal operators**: `ToListAsync`, `ToArrayAsync`, `FirstAsync`/`FirstOrDefaultAsync`,
  `SingleAsync`/`SingleOrDefaultAsync`, `CountAsync`/`LongCountAsync`, `AnyAsync`,
  `SumAsync`/`MinAsync`/`MaxAsync`/`AverageAsync`.
- **Streaming** via `AsAsyncEnumerable()` for low-memory iteration.
- **Rich expression translation**: comparisons, boolean/`null` logic, arithmetic, `string` methods
  (`Contains`/`StartsWith`/`EndsWith`/`ToLower`/`ToUpper`/`Trim`/`Substring`/`Replace`/`Length`/`IsNullOrEmpty`),
  `DateTime` parts, `IN` via `list.Contains(x.Col)`, `??` (coalesce), and ternary (`CASE`).
- **Column mapping** via `[Column]` / `[NotMapped]` (`System.ComponentModel.DataAnnotations.Schema`).
- **Cloud storage**: local filesystem, AWS S3, Azure Data Lake Storage, Google Cloud Storage.
- **Safe by design**: anything that can't be pushed down throws `NotSupportedException` instead of
  silently dragging the whole table into memory.

## Quick start

```csharp
using DeltaLinq;
using System.ComponentModel.DataAnnotations.Schema;

public sealed class User
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
    [Column("signup_date")] public DateTime SignupDate { get; set; }
    public double Price { get; set; }
}

// Local
var q = DeltaTable.Open<User>(@"C:\data\users");

// Cloud (S3)
var opts = new DeltaOptions
{
    Storage = new S3StorageOptions { Region = "eu-west-1", AccessKeyId = "...", SecretAccessKey = "..." },
    OnSql = sql => Console.WriteLine(sql)   // see the generated SQL
};
var cloud = DeltaTable.Open<User>("s3://bucket/users", opts);

int active = await cloud.Where(x => x.Price > 100).CountAsync();
```

See the [project repository](https://github.com/aqieezz/DeltaLinq) for the full guide, the
`AddDeltaLake()` DI package, and current limitations.
