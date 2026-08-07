# WAL Stages 1–6 implementation roadmap

Normative protocol details live in [`wal-interoperability-contract.md`](wal-interoperability-contract.md).
This document is the **ordered engineering plan** to finish full Turso/SQLite WAL interop.

**Hard rules**

1. Do **not** relax `SqliteManagedFileOwnership` (512-byte main-file exclusive lock) until **Stage 6**.
2. Never claim stock-SQLite concurrent interoperability before Stage 6 exit criteria pass.
3. Detached foundations already exist; each stage’s job is to **attach** them to `SqlitePager` (and only then refine locks/busy/recovery).
4. Byte-exact WAL-index layout (`SqliteWalIndex*`) is non-negotiable.
5. MVCC is out of scope for this roadmap.

---

## Current state (as of this branch)

| Piece | Status |
| --- | --- |
| Stage 0 process-exclusive ownership | **Live** on physical pager |
| Stage 1 WAL-index mapped + published from pager | **Attached** (under ownership) |
| Stage 2 read marks via `SqliteWalReadSnapshotCoordinator` | **Attached** for physical WAL readers |
| Stage 3 writer publish + CKPT_LOCK checkpoint/`nBackfill` | **Attached** (under ownership; coordinator still also usable detached) |
| Stages 4–6 | Not started |
| Foreign read-only guest (§1.9) | Live; not full interop |

**Pager gate:** Stage 3 writer/checkpointer attach, then Stages 4–6; no stock-SQLite concurrent interop until Stage 6.

---

## Stage order (do not reorder)

### Stage 1 — WAL-index format attached to pager

**Goal:** Managed physical WAL activity maintains a real SQLite WAL-index in `-shm` under exclusive ownership.

**Work**

1. On writable physical WAL open / create / `PRAGMA journal_mode=WAL`: map `db-shm` and `RebuildFromWal` (or publish empty header when `mxFrame=0`).
2. After every durable WAL commit (`Flush` + recovery boundary check): rebuild or `PublishCommittedFrames` so dual headers + `aPgno`/hash match the WAL scan.
3. After checkpoint + WAL reset: `ResetAfterDurableRestart` (or rebuild empty index) so salts/`iChange` stay coherent.
4. Dispose mapping with the pager; fail closed if mapping/publish fails (fault pager).
5. Optional internal API: `FindFrame` via index must agree with independent WAL scan (test).
6. Update Stage 0 characterization test `ManagedWalActivityNeverMaterializesASqliteWalIndex` → assert non-zero validated index instead of length 0.
7. In-memory / non-physical FS: leave index null (no mmap).

**Exit criteria**

- [x] After managed commit, `-shm` length ≥ one WAL-index region and dual headers validate against the WAL.
- [x] `FindFrame` for every page written in the commit matches `SqliteWalFile` scan (via pager hooks).
- [x] Ownership lock still held; no foreign writer allowed.
- [x] Existing WAL unit/integration suites green (`SqliteWalInteroperabilityContractTests` + related).

**Landed:** physical pager attaches mapping on create/open/journal switch; rebuilds on commit and checkpoint; `EnsureWritableBlocks` grows zero-length carriers; contract test flipped to Stage 1 publication.

**Does not include:** readers using read marks; dropping ownership; SQLite peer coexistence.

---

### Stage 2 — Read marks and reader protocol on pager

**Goal:** Managed readers pin snapshots through `aReadMark[]` + `WAL_READ_LOCK(i)` instead of process-local overlays alone.

**Work**

1. Attach `SqliteWalReadSnapshotCoordinator` (or equivalent) when acquiring a managed read transaction on physical WAL.
2. Prefer sharing an existing mark ≤ `mxFrame`; claim idle mark under exclusive then downgrade shared.
3. Use `WAL_READ_LOCK(0)` only for fully backfilled DB-only views.
4. Keep Stage 0 ownership; process-local overlay may remain as cache but boundary comes from the mark.
5. Multi-reader tests: N managed readers share marks without exclusive conflict.

**Exit criteria**

- [x] Multiple managed readers coexist on the same mark.
- [x] Snapshot boundary is the mark’s `mxFrame`, not a later writer append.
- [x] Ownership still exclusive for production opens.

**Landed:** physical pager `BeginReadTransaction` uses `SqliteWalReadSnapshotCoordinator` when a Stage 1 index is attached; overlay is pinned (or rebuilt) to the mark; busy maps to `SqlitePagerBusyException`.

**Deferred to Stage 6 tests:** true managed+SQLite reader coexistence on one mark (requires no ownership).

---

### Stage 3 — Writer and checkpointer protocol on pager

**Goal:** Commit and checkpoint use SQLite lock roles via the index, not only coarse process locks.

**Work**

1. Attach `SqliteWalWriterCheckpointCoordinator` paths into `CommitTransaction` and checkpoint APIs.
2. Writer: `WAL_WRITE_LOCK` only → append → flush → publish frames/headers.
3. Checkpointer: `WAL_CKPT_LOCK`; derive `mxSafeFrame` from held marks; advance `nBackfill` only after main flush.
4. Stop demanding the entire `[120, 8)` range for every checkpoint once protocol is live (keep Stage 0 ownership separately).
5. Recovery rebuild under full recovery lock set when tails are dirty.

**Landed:** commit uses incremental `PublishCommittedFrames` when the prior dual-header is valid; physical checkpoint takes `WAL_CKPT_LOCK`, honors held read marks for `mxSafeFrame`, publishes `nBackfill`/`nBackfillAttempted`, and resets only with exclusive marks. Point reads no longer occupy legacy lock-manager mark bytes when the WAL-index protocol is live. Ownership unchanged.

**Exit criteria**

- [x] Managed writer incremental publish agrees with independent scan after commit (under Stage 0 writer lock).
- [x] Passive/full checkpoint via CKPT_LOCK + read marks + `nBackfill` on pager.
- [x] Stop coarse full-range checkpoint lock demand for physical WAL-index checkpoints.
- [x] Still no ownership relaxation.

---

### Stage 4 — Busy semantics

**Goal:** Busy errors match SQLite taxonomy and retry schedule.

**Work**

1. Map `SQLITE_BUSY`, `SQLITE_BUSY_SNAPSHOT`, `SQLITE_BUSY_RECOVERY` onto managed exceptions (`SqlitePagerBusyException` + new snapshot-invalidated type).
2. Replace flat 10 ms poll with SQLite-like backoff.
3. Preserve `Operation` for existing callers.

**Exit criteria**

- [ ] Tests for each busy class under contended locks.
- [ ] Reader whose mark was reset gets a distinct result.

---

### Stage 5 — Recovery, handoff, shared cache invalidation

**Goal:** Crash recovery and multi-connection (same process / future multi-process) cache invalidation use shared header fields.

**Work**

1. Recovery under `WAL_RECOVER_LOCK` + exclusive read marks; rebuild index; bump `iChange`.
2. Replace process-local `LockManager.Generation` invalidation with `iChange` / `mxFrame` / salts comparisons for physical WAL.
3. Handle last-connection `-shm` unlink, exclusive locking mode, heap WAL-index fallback where required.

**Exit criteria**

- [ ] Torn/corrupt publication fail-closed; clean WAL rebuilds index.
- [ ] Cache does not serve stale pages across `iChange` bumps.

---

### Stage 6 — Retire process-exclusive ownership

**Goal:** Full multi-process interop with ordinary SQLite and Turso.

**Work**

1. Only after Stages 1–5 exit criteria are green.
2. Replace 512-byte main-file ownership with SQLite `PENDING`/`RESERVED`/`SHARED` (and DELETE-mode journal locks).
3. Delete `SqliteManagedFileOwnership`.
4. Differential stress: SQLite writer ↔ managed reader and reverse; `PRAGMA wal_checkpoint` agreement; Turso open of managed-produced artifacts and reverse.
5. Update contract status banner from “Stage 0” to “Stage 6 complete”.

**Exit criteria**

- [ ] Stock SQLite and managed open the same live DB without exclusive ownership.
- [ ] No silent downgrade on unsupported platforms.
- [ ] Characterization suite rewritten for concurrent interop, not Stage 0 exclusion.

---

## Suggested implementation slices (PRs)

| PR | Scope |
| --- | --- |
| A | This roadmap + Stage 1 pager publish/rebuild + contract test flip |
| B | Stage 1 FindFrame path optional dual-read (overlay vs index) parity tests |
| C | Stage 2 attach read marks |
| D | Stage 3 attach writer/checkpoint |
| E | Stage 4 busy |
| F | Stage 5 recovery/invalidation |
| G | Stage 6 ownership retirement + interop harness |

---

## Non-goals

- MVCC journal mode / concurrent writers inside one process via MVCC.
- Virtual tables, vector, super-journal.
- Faking `journal_mode=mvcc` in conformance.

## References

- `docs/wal-interoperability-contract.md`
- `src/Ahtola.Core/Storage/SqliteWalIndex.cs`
- `src/Ahtola.Core/Storage/SqliteWalReadSnapshotCoordinator.cs`
- `src/Ahtola.Core/Storage/SqliteWalWriterCheckpointCoordinator.cs`
- `src/Ahtola.Core/Storage/SqlitePager.cs`
- `src/Ahtola.Tests/SqliteWalIndex*.cs`, `SqliteWalProcessIsolationHarnessTests.cs`
