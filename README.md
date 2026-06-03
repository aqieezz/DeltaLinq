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

```
C# LINQ  →  expression-tree translator  →  SQL  →  DuckDB + delta_scan  →  typed C# objects
```

> **Status:** v1.0.0. Read + append-write pipeline, covered by 72 tests (including end-to-end queries
> and writes against actual Delta tables). See [Limitations](#limitations).

---

## Why

Delta Lake is a lakehouse standard, but outside Spark/Python/Databricks the .NET experience is thin.
The low-level binding ([`delta-dotnet`](https://github.com/delta-incubator/delta-dotnet)) and engines
([DuckDB's delta extension](https://duckdb.org/docs/stable/core_extensions/delta)) already exist — what's
missing is an **ergonomic, EF-Core-like query layer** on top. That's DeltaLinq.

## Install

```sh
dotnet add package DeltaLinq
dotnet add package DeltaLinq.DependencyInjection   # optional: ASP.NET Core / DI integration
```

> The first query downloads the DuckDB `delta` extension (one-time, needs network). For air-gapped
> deployments, pre-stage the extension and set `DeltaOptions.ExtensionDirectory`.

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
    public bool IsActive { get; set; }
}

var table = DeltaTable.Open<User>(@"C:\data\users");

// Filter + sort + page, materialize async
List<User> recent = await table
    .Where(x => x.IsActive && x.SignupDate.Year >= 2023)
    .OrderByDescending(x => x.Price)
    .Take(50)
    .ToListAsync();

// Projection (column pruning)
var summary = await table.Select(x => new { x.Id, x.Price }).ToListAsync();

// Aggregates push down to SQL
int nl       = await table.CountAsync(x => x.Country == "NL");
double total = await table.SumAsync(x => x.Price);
bool any     = await table.AnyAsync(x => x.Price > 1000);

// Grouping + grouped aggregates → SQL GROUP BY
var byCountry = await table
    .GroupBy(x => x.Country)
    .Select(g => new { Country = g.Key, Count = g.Count(), Total = g.Sum(x => x.Price) })
    .OrderByDescending(r => r.Count)
    .ToListAsync();

// Join two Delta tables (inner equijoin) → SQL JOIN
var orders = DeltaTable.Open<Order>(@"C:\data\orders");
var lines = await orders
    .Join(table, o => o.UserId, u => u.Id, (o, u) => new { o.Id, o.Product, u.Name, u.Country })
    .Where(x => x.Country == "NL")
    .ToListAsync();

// Left join — keep every order, even those whose user is missing (u is null → NULL columns)
var withUser = await orders
    .LeftJoin(table, o => o.UserId, u => u.Id, (o, u) => new { o.Id, u!.Name })
    .ToListAsync();

// Append a new commit (creates the table on first write)
await DeltaTable.AppendAsync(@"C:\data\orders",
    new[] { new Order { Id = 999, UserId = 1, Product = "Desk", Total = 250 } });

// Streaming for low-memory iteration
await foreach (var u in table.Where(x => x.Country == "NL").AsAsyncEnumerable())
    Console.WriteLine(u.Name);
```

## Supported LINQ

| Category | Operators |
|---|---|
| Filtering | `Where` (`==` `!=` `<` `<=` `>` `>=`, `&&` `\|\|` `!`, `null` checks, `??`, ternary) |
| Projection | `Select` → entity, anonymous type, DTO (`MemberInit`), or scalar |
| Ordering | `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` |
| Paging | `Take`, `Skip`, `Distinct` |
| Grouping | `GroupBy(key).Select(g => …)` (and `GroupBy(key, (k, items) => …)`) — single & composite keys; `g.Key`, `g.Count()`, `g.Sum`/`Min`/`Max`/`Average`; post-group `OrderBy`/`Take`/`Skip`; **HAVING** via `.Where(r => r.Count > N)` after the projection |
| Joins | `Join` (inner) and `LeftJoin` — single & composite keys, **2 or more tables** (chained), pre-join `Where` on each side, post-join `Where`/`Select`/`OrderBy`/`Take`/`Skip`/`Distinct`, and `Count`/`Any`/`Sum`/`Min`/`Max`/`Average`/`First`/`Single` over a join |
| Writes | `DeltaTable.Append`/`AppendAsync<T>(path, rows)` — append a new commit (creates the table if missing) |
| Strings | `Contains`/`StartsWith`/`EndsWith` (→ `LIKE`), `ToLower`/`ToUpper`/`Trim`/`Substring`/`Replace`/`Length`, `IsNullOrEmpty`/`IsNullOrWhiteSpace` |
| Collections | `list.Contains(x.Col)` → `IN (...)` |
| Dates | `.Year`/`.Month`/`.Day`/`.Hour`/`.Minute`/`.Second` → `EXTRACT` |
| Math | `Math.Abs/Ceiling/Floor/Round/Sqrt/Pow/...` |
| Terminals (async) | `ToListAsync`, `ToArrayAsync`, `FirstAsync`, `FirstOrDefaultAsync`, `SingleAsync`, `SingleOrDefaultAsync`, `CountAsync`, `LongCountAsync`, `AnyAsync`, `SumAsync`, `MinAsync`, `MaxAsync`, `AverageAsync`, `AsAsyncEnumerable` |
| Terminals (sync) | `ToList`, `Count`, `Any`, `First`, `Single`, `Sum`, `Min`, `Max`, `Average`, … |

Captured variables and other parameter-independent subtrees are evaluated automatically and bound as
named SQL parameters (`$p0`, `$p1`, …) — strings, numbers, and booleans — for safety and plan caching;
dates, GUIDs, and enums are emitted as typed SQL literals.

**Anything that can't be translated throws `NotSupportedException`** — DeltaLinq never silently pulls a
whole table into memory to client-evaluate (the foot-gun EF Core removed in 3.0). Materialize with
`ToListAsync()` first, then continue with LINQ-to-Objects.

## Cloud storage

```csharp
var opts = new DeltaOptions
{
    Storage = new S3StorageOptions { Region = "eu-west-1", AccessKeyId = "...", SecretAccessKey = "..." },
    OnSql = sql => logger.LogDebug("DeltaLinq SQL: {Sql}", sql)
};
var table = DeltaTable.Open<User>("s3://bucket/users", opts);
```

Backends: local filesystem, **AWS S3** (`S3StorageOptions`, also MinIO/S3-compatible via `UrlStyle="path"`),
**Azure** (`AzureStorageOptions`), **Google Cloud Storage** (`GcsStorageOptions`). Omit credentials to use
the ambient credential chain.

## ASP.NET Core / DI

```csharp
using DeltaLinq;

builder.Services.AddDeltaLake(o => o.Storage = new S3StorageOptions { Region = "eu-west-1" });

app.MapGet("/users/nl", async (IDeltaTableFactory delta) =>
    await delta.Open<User>("s3://bucket/users")
               .Where(x => x.Country == "NL")
               .Take(100)
               .ToListAsync());
```

A complete runnable example is in [samples/DeltaLinq.Sample.Api](samples/DeltaLinq.Sample.Api) —
`dotnet run --project samples/DeltaLinq.Sample.Api` and hit `/stats/by-country`.

## How it works

1. `DeltaTable.Open<T>` returns an `IQueryable<T>` backed by a custom `IQueryProvider`.
2. On execution, the LINQ method chain is parsed into a query model; each lambda's expression tree is
   translated to SQL ([`Query/ExpressionToSql.cs`](src/DeltaLinq/Query/ExpressionToSql.cs)).
3. The SQL runs on an embedded DuckDB connection with the `delta` extension, which reads the Delta
   transaction log + Parquet (predicate/projection pushdown happen in the engine).
4. Result rows are materialized back into your CLR types.

DeltaLinq does **not** implement the Delta protocol — that's the engine's job.

## Limitations

- **Single-node** (DuckDB). No distributed execution — aimed at backend analytics, ETL microservices, APIs.
- **Writes are append-only and single-writer.** No update/delete/`MERGE`, no optimistic-concurrency
  conflict retry, no partitioned writes. On append to an existing table the entity must match its schema.
- **No time travel.** The bundled DuckDB delta extension doesn't expose snapshot/version pinning on
  `delta_scan`, and replaying the log by hand would be unsafe for tables with removes/checkpoints/deletion
  vectors — so reads always see the latest snapshot.
- **Joins** are equijoins (`Join` inner, `LeftJoin`). No right/full/cross joins or `GroupJoin`.
- **Async is engine-bound**: `AsAsyncEnumerable` streams; the other `…Async` operators wrap synchronous
  DuckDB calls on a thread (honest, not fake — but not true async I/O).
- Property name = column name unless `[Column]`. Enums map to their numeric value.
- **Timestamps are read/written in UTC**: the session pins `TimeZone='UTC'` (via the `icu` extension) so
  `DateTime` round-trips exactly. If `icu` can't load (offline), timestamps use the engine's default zone.

## Build & test

```sh
dotnet build DeltaLinq.sln -c Release
dotnet test                                  # 72 tests; engine-backed ones self-skip if offline
dotnet pack src/DeltaLinq/DeltaLinq.csproj -c Release -o artifacts
```

Requires the .NET 8 SDK (or newer). Tests target net9.0; the library targets net8.0.

## Publishing

See **[PUBLISHING.md](PUBLISHING.md)** for the full NuGet release checklist and the GitHub Actions workflow.

## Repository layout

```
src/DeltaLinq/                     the library
src/DeltaLinq.DependencyInjection/ AddDeltaLake() DI integration
samples/DeltaLinq.Sample.Api/      runnable ASP.NET Core minimal-API sample
tests/DeltaLinq.Tests/             xUnit suite
```

## License

[MIT](LICENSE) © 2026 Aqil Abbas Khan. "Delta Lake" is a trademark of the LF Projects, LLC / Databricks;
this is an independent community project and is not affiliated with or endorsed by them.
