# Ahtola .NET

An experimental pure managed (C#) port of [Turso](https://turso.tech)’s
SQLite-compatible database engine, with [ADO.NET](https://learn.microsoft.com/dotnet/framework/data/adonet/) and [EF Core](https://learn.microsoft.com/efcore/) providers.

> ⚠️ **Experimental project.** Ahtola is a research / prototype engine and is
> **not** production-ready. For production .NET workloads, use the official
> bindings to the original Turso Rust core at
> [tursodatabase/turso](https://github.com/tursodatabase/turso).

Ahtola is a C# engine that reads and writes SQLite’s on-disk format directly —
automatically vibe-ported from Turso’s Rust core, as a fun experiment. It is
**not** a binding over native SQLite or over any Rust core — no native
companion, P/Invoke SDK, or Rust toolchain is required to restore, build, pack,
or run.

## Install

```bash
dotnet add package Devolutions.Ahtola.Data.Sqlite
# optional EF Core 9.x provider:
dotnet add package Devolutions.Ahtola.EntityFrameworkCore.Sqlite
```

Targets: `net8.0`, `net9.0`, `net10.0`. No `net48` / .NET Framework assets.

| Package | Role |
| --- | --- |
| `Devolutions.Ahtola.Core` | Managed engine |
| `Devolutions.Ahtola.Data.Sqlite` | ADO.NET provider + `Microsoft.Data.Sqlite`-compatible facade; embeds `Ahtola.Data` |
| `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` | EF Core provider (`UseAhtola`) |

| Layer | Name |
| --- | --- |
| NuGet PackageId | `Devolutions.Ahtola.*` |
| Namespaces / assemblies / types | `Ahtola.*` (`AhtolaConnection`, `UseAhtola`, …) |
| Project folders | `src/Ahtola.*` |

## Quick start

**SQLite-compatible facade** (drop-in `using` swap from Microsoft.Data.Sqlite):

```csharp
using Ahtola.Data.Sqlite;

using var connection = new SqliteConnection("Data Source=app.db");
connection.Open();
connection.ExecuteNonQuery("CREATE TABLE t(a INTEGER, b TEXT)");
connection.ExecuteNonQuery("INSERT INTO t VALUES (1, 'hello')");

using var command = connection.CreateCommand();
command.CommandText = "SELECT a, b FROM t";
using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
```

**Ahtola types** (same package):

```csharp
using Ahtola;

using var connection = new AhtolaConnection("Data Source=:memory:");
connection.Open();
connection.ExecuteNonQuery("CREATE TABLE t(a, b)");
// AhtolaConnection, AhtolaCommand, AhtolaParameter, AhtolaFactory.Instance, …
```

**EF Core:**

```csharp
options.UseAhtola("Data Source=app.db");
```

Common connection-string keywords: `Data Source`, `Mode`, `Cache`, `Pooling`,
`Foreign Keys`, `Default Timeout` / `Command Timeout`, `Foreign Read Only`,
`Encryption Cipher` + `Encryption Key` (AES-128/256-GCM). Default local provider
is managed-only.

## What this is good for

- Fully managed local SQLite-format databases with **no native assets**
- Small-to-moderate workloads, in-process embedding, constrained deployment
- A familiar ADO.NET / MDS-shaped API and an EF Core provider

## Important limits

Treat Ahtola as SQLite-*compatible*, not a full SQLite replacement:

- **In-memory working set** — tables and intermediate results stay in the process
  heap; nothing spills to disk. Prefer modest databases and explicit transactions
  for writes (managed writes are slower than native SQLite and the gap grows with
  table size).
- **Planner** — `ANALYZE` / `sqlite_stat1` feed index scoring and limited join
  cost gates (selective outer for two-table INNER nested loops; equijoin hash
  build side). Full System-R DP join reordering and multi-index AND intersection
  are still deferred; OUTER JOIN order stays correctness-preserving. Prefer
  `ORDER BY` when order matters (`GROUP BY` is first-encounter order).
- **File-backed platforms** — Windows, 64-bit Linux, and macOS. In-memory works
  everywhere; other platforms (e.g. 32-bit Linux) throw
  `PlatformNotSupportedException` on physical open. macOS uses POSIX
  `fcntl(F_SETLK)` (process-associated locks, not Linux OFD); multi-engine
  claims on macOS need host verification.
- **Multi-engine files (Stage 6)** — physical opens use SQLite main-file SHARED
  locking (Windows / 64-bit Linux / macOS). Managed and stock SQLite can share
  the same live WAL database on Windows/Linux (`-shm` DMS + peer WAL visibility
  on new statements). Pooling may retain managed handles until `Pooling=False`
  or `SqliteConnection.ClearAllPools()`. PENDING/RESERVED DELETE-mode polish and
  a Turso binary differential remain optional depth. See
  [docs/wal-interoperability-contract.md](docs/wal-interoperability-contract.md).
- **Foreign read-only** — `Mode=ReadOnly;Foreign Read Only=True;Pooling=False`
  can read a DB still held by native SQLite/Turso (e.g. winget `index.db`) without
  taking main-file locks.
- **MVCC** — process-local `PRAGMA journal_mode=mvcc` + `BEGIN CONCURRENT` with
  dual-cursor SELECT/DML routing, logical log, and a checkpoint SM skeleton
  (`PRAGMA wal_checkpoint` in MVCC mode). Not cross-process; residual schema-
  cookie polish and full per-page b-tree checkpoint SM remain open — see
  [docs/mvcc-port-contract.md](docs/mvcc-port-contract.md).
- **Not implemented** — virtual tables / FTS / R-Tree, loadable extensions, raw
  `sqlite3*` handles (`Handle` is null), AEGIS encryption ciphers, sync engine /
  CDC, CREATE SEQUENCE, typed-value extensions.
- **Native / Sync companions** — not shipped. Connection-string paths that need
  them fail closed. OS P/Invoke in the pager for locks/WAL is intentional engine
  code, not a Rust SDK binding.
- **Remote Hrana** — optional pure-managed HTTP `/v2/pipeline` on
  `AhtolaConnection` (tests use a canned server). Not a cloud product surface.

Encryption format v0 uses a fixed 5-byte magic `AHTLA`, then version and cipher
id (AES-GCM page AEAD).

## Build and test

PowerShell entrypoint (preferred):

```powershell
./build.ps1 restore
./build.ps1 build
./build.ps1 test              # packaged consumer gate + managed suite
./build.ps1 pack
./build.ps1 validate-package
./build.ps1 format-check
```

Optional parameters: `-Configuration Debug|Release`, `-Framework net10.0`,
`-PackageVersion …`, `-PackageOutput ./artifacts/managed-packages`,
`-MinimumExecutedTests 2500`.

Or with the .NET SDK only:

```bash
dotnet build Ahtola.slnx -c Release
dotnet test src/Ahtola.Tests/Ahtola.Tests.csproj -c Release -f net10.0
pwsh ./scripts/Validate-ManagedPackageClosure.ps1
```

`scripts/Invoke-ManagedTestSuite.ps1` runs tests and fails the job if too few
tests executed (guards against silent empty runs). Conformance gaps for the
embedded `sqlite-sqltests` corpus live in
`src/Ahtola.Tests/Conformance/managed-sqltest-expected-failures.txt`.

## Layout

```text
ahtola/
├── src/Ahtola.Core/                      # engine
├── src/Ahtola.Data/                      # embedded ADO core (not a separate nupkg)
├── src/Ahtola.Data.Sqlite/               # provider + MDS facade
├── src/Ahtola.EntityFrameworkCore.Sqlite/
├── src/Ahtola.Tests/
├── samples/                              # ManagedPackageConsumer, PSSqlite.Managed
├── docs/                                 # WAL interop contract (Turso target)
├── scripts/                              # test + package closure validators
├── build.ps1                             # restore / build / test / pack
├── NuGet.config
├── LICENSE
└── Ahtola.slnx
```

## License

MIT — see [LICENSE](LICENSE).
