# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-06-03

First stable release.

### Added
- **Append writes**: `DeltaTable.Append`/`AppendAsync<T>(path, rows)` (and `IDeltaTableFactory.AppendAsync`)
  create the table on first write and add a new commit on each subsequent write. Engine-independent
  (writes Parquet via DuckDB + a hand-written transaction log), since the bundled DuckDB delta
  extension is read-only. Single-writer; append-only.
- **HAVING**: filter grouped results after the projection — `.GroupBy(k).Select(g => …).Where(r => r.Count > N)`.
- **Aggregates over joins**: `Sum`/`Min`/`Max`/`Average` (plus the existing `Count`/`Any`/`First`) over a join.
- **Left joins**: `outer.LeftJoin(inner, ok, ik, (o, c) => …)` → SQL `LEFT JOIN` (unmatched inner columns come back `NULL`).
- **Multi-table joins**: chained `a.Join(b, …).Join(c, …)` (3+ tables) via tuple result selectors.

### Not supported (documented)
- **Time travel** — the bundled DuckDB delta extension does not expose snapshot/version pinning on
  `delta_scan`, and replaying the log by hand would be unsafe for tables with removes/checkpoints/deletion
  vectors. Reads always see the latest snapshot.
- Updates/deletes/`MERGE`, optimistic-concurrency conflict retries, and partitioned writes.
- Right/full/cross joins and `GroupJoin`.

## [0.3.0] - 2026-06-03

### Added
- **Joins** — two-source inner equijoins: `outer.Join(inner, ok, ik, (o, c) => ...)`. Single and
  composite (anonymous) keys; pre-join `Where` on either source; a final projection *or* a tuple
  result selector (`(o, c) => new { o, c }`) followed by `Where`/`Select`/`OrderBy`/`Take`/`Skip`/
  `Distinct`; `Count`/`LongCount`/`Any`/`First`/`Single` over a join.
- Internal: the translator is now scope-based (`IScope`), enabling multi-source member resolution.

### Notes
- Join limitations: inner equijoin only (no left/outer/`GroupJoin`), two tables, no `HAVING`, and no
  `Sum`/`Min`/`Max`/`Average` directly over a join yet (project the value first, or aggregate in memory).

## [0.2.0] - 2026-06-03

### Added
- **GroupBy + grouped aggregates**: `GroupBy(key).Select(g => new { g.Key, g.Count(), g.Sum(x => ...) })`
  and the `GroupBy(key, (key, items) => ...)` result-selector form. Single and composite (anonymous)
  keys; `g.Key`/`g.Key.Member`, `Count`/`Sum`/`Min`/`Max`/`Average` (with arithmetic between them);
  post-group `OrderBy`/`OrderByDescending`/`Take`/`Skip`.
- **SQL parameterization**: string, numeric, and boolean values are bound as named parameters
  (`$p0`, `$p1`, …) instead of being inlined — better security and engine plan caching. Dates, GUIDs,
  and enums remain typed SQL literals so their column type stays unambiguous.
- **NuGet package icon**.
- **Sample**: `samples/DeltaLinq.Sample.Api`, a runnable ASP.NET Core minimal API using
  `AddDeltaLake()` + `IDeltaTableFactory`.

### Notes
- Filtering after `GroupBy` (HAVING) is not supported yet; filter before grouping.

## [0.1.0] - 2026-06-03

Initial release.

### Added
- `DeltaTable.Open<T>(path, options)` and `DeltaContext` entry points returning `IQueryable<T>`.
- LINQ-to-SQL translation: `Where`, `Select` (entity / anonymous / DTO / scalar), `OrderBy`/`ThenBy`
  (+ descending), `Take`, `Skip`, `Distinct`.
- Expression support: comparisons, boolean/`null` logic, arithmetic, `??`, ternary (`CASE`), string
  methods (`Contains`/`StartsWith`/`EndsWith`/`ToLower`/`ToUpper`/`Trim`/`Substring`/`Replace`/`Length`/
  `IsNullOrEmpty`/`IsNullOrWhiteSpace`), `IN` via `list.Contains`, `DateTime` parts, `Math.*`, and
  partial evaluation of parameter-independent subtrees.
- Async terminal operators: `ToListAsync`, `ToArrayAsync`, `First/FirstOrDefault/Single/SingleOrDefault`,
  `Count/LongCount`, `Any`, `Sum/Min/Max/Average`, plus `AsAsyncEnumerable` streaming. Synchronous
  LINQ terminals supported via the query provider.
- Column mapping via `[Column]` / `[NotMapped]`.
- Cloud storage: AWS S3, Azure, Google Cloud Storage, local filesystem.
- `AddDeltaLake()` dependency-injection integration (`DeltaLinq.DependencyInjection`).
- `DeltaOptions.OnSql` logging hook and offline extension support
  (`ExtensionDirectory` / `AllowUnsignedExtensions`).
- `NotSupportedException` for any untranslatable expression (no silent client-side evaluation).

### Known limitations
- Single-node, read-only. No joins, `GroupBy`, writes, or time travel yet. Values are inlined (escaped)
  rather than parameterized.

[1.0.0]: https://github.com/aqieezz/DeltaLinq/releases/tag/v1.0.0
[0.3.0]: https://github.com/aqieezz/DeltaLinq/releases/tag/v0.3.0
[0.2.0]: https://github.com/aqieezz/DeltaLinq/releases/tag/v0.2.0
[0.1.0]: https://github.com/aqieezz/DeltaLinq/releases/tag/v0.1.0
