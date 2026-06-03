# Publishing DeltaLinq to NuGet

This guide walks through publishing the `DeltaLinq` and `DeltaLinq.DependencyInjection` packages to
[nuget.org](https://www.nuget.org). You can publish manually (a few CLI commands) or automatically via
the included GitHub Actions workflow.

---

## 0. Before your first publish — choose and reserve a package name

> ⚠️ **Important.** The package id `DeltaLake` is already taken by the official
> [delta-dotnet](https://www.nuget.org/packages/DeltaLake) binding, and **"Delta Lake" is a trademark**
> (LF Projects, LLC / Databricks). Pick an id that is clearly your own community project and is free on
> nuget.org. This repo defaults to **`DeltaLinq`** — verify it's available, or change it.

Check availability by visiting `https://www.nuget.org/packages/<Id>`. A 404 means it's free.

If you change the id, update it in:
- [`src/DeltaLinq/DeltaLinq.csproj`](src/DeltaLinq/DeltaLinq.csproj) → `<PackageId>`
- [`src/DeltaLinq.DependencyInjection/DeltaLinq.DependencyInjection.csproj`](src/DeltaLinq.DependencyInjection/DeltaLinq.DependencyInjection.csproj) → `<PackageId>`
- (Optional) the assembly/namespace if you want them to match.

Also update the placeholders in [`Directory.Build.props`](Directory.Build.props):
`<Authors>`, `<PackageProjectUrl>`, `<RepositoryUrl>` (replace `aqieezz/DeltaLinq` with your real repo),
and confirm `<PackageLicenseExpression>` (currently `MIT`, matching [LICENSE](LICENSE)).

> A GitHub repo can't contain `#` in its name — name the remote repo something like `deltalinq`
> even though this local folder is `delta-lake-c#`.

---

## 1. Prerequisites

- .NET 8 SDK or newer (`dotnet --version`).
- A free nuget.org account: <https://www.nuget.org/users/account/LogOn>.
- A NuGet API key: **Account → API Keys → Create**. Scope it to **Push** and, ideally, restrict the
  **Glob pattern** to your package id(s) (e.g. `DeltaLinq*`). Copy the key — it's shown once.

---

## 2. Set the version

Versioning follows [SemVer](https://semver.org). Set `<Version>` in each library `.csproj`
(currently `1.0.0`). For pre-releases use a suffix, e.g. `1.0.0-beta.1` or `1.0.0-rc.1`.

Keep [CHANGELOG.md](CHANGELOG.md) up to date with each release.

---

## 3. Build, test, and pack

```sh
dotnet build DeltaLinq.sln -c Release
dotnet test  DeltaLinq.sln -c Release          # make sure it's green
dotnet pack  src/DeltaLinq/DeltaLinq.csproj -c Release -o artifacts
dotnet pack  src/DeltaLinq.DependencyInjection/DeltaLinq.DependencyInjection.csproj -c Release -o artifacts
```

This produces in `artifacts/`:
- `DeltaLinq.<version>.nupkg` + `DeltaLinq.<version>.snupkg` (symbols)
- `DeltaLinq.DependencyInjection.<version>.nupkg` + `.snupkg`

**Inspect before pushing** (optional but recommended): open the `.nupkg` (it's a zip) or use
[NuGetPackageExplorer](https://github.com/NuGetPackageExplorer/NuGetPackageExplorer) to confirm the
README, license, icon, and metadata look right.

---

## 4. Push to nuget.org

Publish the **core package first**, since the DI package depends on it.

```sh
dotnet nuget push artifacts/DeltaLinq.1.0.0.nupkg \
    --api-key YOUR_API_KEY \
    --source https://api.nuget.org/v3/index.json

dotnet nuget push artifacts/DeltaLinq.DependencyInjection.1.0.0.nupkg \
    --api-key YOUR_API_KEY \
    --source https://api.nuget.org/v3/index.json
```

- The matching `.snupkg` symbol packages are pushed automatically alongside each `.nupkg`.
- Don't put the API key in source control. Use an env var: `--api-key $NUGET_API_KEY`.
- Packages take a few minutes to be indexed and become installable.

To unlist a bad version (you can't truly delete): nuget.org → your package → **Manage → Unlist**, or
`dotnet nuget delete DeltaLinq 1.0.0 --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json`.

---

## 5. Automated publishing (GitHub Actions)

A workflow is included at [`.github/workflows/release.yml`](.github/workflows/release.yml). It builds,
tests, packs, and pushes to nuget.org whenever you push a `v*` tag.

Setup:
1. In your GitHub repo: **Settings → Secrets and variables → Actions → New repository secret**.
   Name it `NUGET_API_KEY`, paste your key.
2. Make sure the `<Version>` in the csproj matches the tag you push (or have the workflow derive the
   version from the tag — see the comment in the workflow).
3. Release:
   ```sh
   git tag v1.0.0
   git push origin v1.0.0
   ```
   The workflow runs and publishes both packages.

---

## 6. Post-publish checklist

- [ ] `https://www.nuget.org/packages/DeltaLinq` renders the README correctly.
- [ ] `dotnet add package DeltaLinq` works from a clean project.
- [ ] Create a GitHub Release for the tag with notes from the CHANGELOG.
- [ ] Source Link works: consumers can step into the source while debugging (enabled via
      `PublishRepositoryUrl`/`EmbedUntrackedSources` once `RepositoryUrl` points at your real repo).

---

## Versioning policy (suggested)

- `0.x` while the API is still moving — minor bumps may break.
- `1.0.0` once the public API is stable.
- Patch = bug fixes, Minor = backwards-compatible features, Major = breaking changes.
