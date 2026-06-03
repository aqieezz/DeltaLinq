# DeltaLinq.Sample.Api

A minimal ASP.NET Core API showing DeltaLinq with dependency injection. On startup it seeds a small
demo Delta table in your temp directory, then serves it through `IDeltaTableFactory`.

## Run

```sh
dotnet run --project samples/DeltaLinq.Sample.Api
```

Then try:

| Endpoint | Shows |
|---|---|
| `GET /users` | filter + sort + page (`?country=NL&minPrice=150&take=20`) |
| `GET /users/1` | single row (`FirstOrDefaultAsync`) |
| `GET /users/count` | scalar aggregate (`CountAsync`) |
| `GET /stats/by-country` | `GroupBy` + `Count`/`Sum`/`Average` |
| `POST /users` | append a row (`AppendAsync`) — e.g. `curl -X POST .../users -H "content-type: application/json" -d '{"id":11,"name":"Zoe","country":"NL","price":99,"isActive":true}'` |

```sh
curl "http://localhost:5000/stats/by-country"
```

Generated SQL is logged to the console (via `DeltaOptions.OnSql`), so you can watch each LINQ query
become a pushed-down, parameterized SQL statement.

The interesting wiring is in [Program.cs](Program.cs): `builder.Services.AddDeltaLake(...)` and then
injecting `IDeltaTableFactory` into each endpoint.
