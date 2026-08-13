# Devolutions.Ahtola.PowerShell

Binary PowerShell project that publishes the **Devolutions.Ahtola.Sqlite** module,
backed by Ahtola’s pure-managed SQLite stack (`Ahtola.Data.Sqlite`) instead of
Microsoft.Data.Sqlite / SQLitePCLRaw.

| Layer | Name |
| --- | --- |
| Project / assembly | `Devolutions.Ahtola.PowerShell` |
| Published module | `Devolutions.Ahtola.Sqlite` |
| CLR type namespace | `Ahtola.PSSqlite` (cmdlet surface remains `*-PSSqlite*`) |

## Scope

- Same public cmdlet names (`*-PSSqlite*`) and YAML config / migration model as the C# port of synedgy.PSSqlite.
- Targets PowerShell 7+ only (`net8.0` / `net9.0` / `net10.0`). No Windows PowerShell 5.1 / netstandard2.0 path.

## Build / stage

```powershell
./build.ps1 pack-powershell
# -> artifacts/powershell-modules/Devolutions.Ahtola.Sqlite
```

Or build the project (staging runs after `net8.0` build):

```powershell
dotnet build ./src/Devolutions.Ahtola.PowerShell/Devolutions.Ahtola.PowerShell.csproj -c Debug -f net8.0
```

## Import

```powershell
Import-Module ./artifacts/powershell-modules/Devolutions.Ahtola.Sqlite
Get-Command -Module Devolutions.Ahtola.Sqlite
```

No native SQLite assets are required; PreLoadTypes loads managed Ahtola + YamlDotNet assemblies from `bin/`.

## Tests

Library (NUnit):

```powershell
pwsh ./scripts/Invoke-ManagedTestSuite.ps1 `
  -Framework net10.0 `
  -Filter "FullyQualifiedName~PSSqliteModuleTests" `
  -MinimumExecutedTests 1
```

Module (Pester 6):

```powershell
./build.ps1 test-powershell
# or:
pwsh ./scripts/Invoke-PowerShellModuleTests.ps1
```

## Notes

- `PowerShellStandard.Library` is compile-only; the PowerShell host supplies real `System.Management.Automation` at import time. Unit tests fall back to `OrderedDictionary` when SMA is absent.
- Cmdlet parameter names follow the upstream binary port (`-Path`, `-SqliteDBConfig`, `-SqliteConnection`, `-As`, …).
