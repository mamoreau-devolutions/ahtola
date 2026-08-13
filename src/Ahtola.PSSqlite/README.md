# Ahtola.PSSqlite

PowerShell module clone of [synedgy.PSSqlite](https://github.com/), backed by Ahtola’s pure-managed SQLite stack (`Ahtola.Data.Sqlite`) instead of Microsoft.Data.Sqlite / SQLitePCLRaw.

## Scope

- Same public cmdlet names (`*-PSSqlite*`) and YAML config / migration model as the C# port of synedgy.PSSqlite.
- Assembly / namespace: `Ahtola.PSSqlite`.
- Targets PowerShell 7+ only (`net8.0` / `net9.0` / `net10.0`). No Windows PowerShell 5.1 / netstandard2.0 path (Ahtola engines do not multi-target Desktop CLR).

## Build / stage

```powershell
./build.ps1 pack-pssqlite
# -> artifacts/powershell-modules/Ahtola.PSSqlite
```

Or build the project (staging runs after `net8.0` build):

```powershell
dotnet build ./src/Ahtola.PSSqlite/Ahtola.PSSqlite.csproj -c Debug -f net8.0
```

## Import

```powershell
Import-Module ./artifacts/powershell-modules/Ahtola.PSSqlite
Get-Command -Module Ahtola.PSSqlite
```

No native SQLite assets are required; PreLoadTypes loads managed Ahtola + YamlDotNet assemblies from `bin/`.

## Library tests

```powershell
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 `
  -Framework net10.0 `
  -Filter "FullyQualifiedName~PSSqliteModuleTests" `
  -MinimumExecutedTests 1
```

## Notes

- `PowerShellStandard.Library` is compile-only; the PowerShell host supplies real `System.Management.Automation` at import time. Unit tests fall back to `OrderedDictionary` when SMA is absent.
- Cmdlet parameter names follow the upstream binary port (`-Path`, `-SqliteDBConfig`, `-SqliteConnection`, `-As`, …).
