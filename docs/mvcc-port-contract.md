# MVCC port contract (Ahtola ↔ Turso v0.7.2)

Companion to [`wal-interoperability-contract.md`](wal-interoperability-contract.md)
§1.4 and [`turso-gap-analysis.md`](turso-gap-analysis.md) §8.

## Upstream reference

Pinned submodule: `turso-src/` @ **v0.7.2** (`046e9cbf6`).

| Turso | Ahtola |
| --- | --- |
| `core/mvcc/clock.rs` | `src/Ahtola.Core/Mvcc/MvccClock.cs` |
| `core/mvcc/database/mod.rs` (`MvStore`) | `src/Ahtola.Core/Mvcc/MvStore.cs` |
| `core/mvcc/cursor.rs` | *(Phase 1.5+)* |
| `core/mvcc/persistent_storage/logical_log.rs` | *(Phase 2)* |
| `core/mvcc/database/checkpoint_state_machine.rs` | *(Phase 2)* |
| `LimboError::WriteWriteConflict` | `EmbeddedWriteWriteConflictException` |

## Invariants (must hold)

1. **Clock publish atomicity.** Commit timestamp generation and transition to
   `Preparing(ts)` happen under the same clock lock so a peer cannot take a
   `begin_ts` between those steps (snapshot isolation).
2. **No fake `journal_mode=mvcc`.** Reporting `mvcc` requires a live `MvStore`
   on the database. Disabling MVCC clears the store.
3. **`BEGIN CONCURRENT` gate.** Without MVCC → Turso error string
   `Concurrent transaction mode is only supported when MVCC is enabled`.
   With MVCC → open concurrent tx; nested BEGIN →
   `cannot start a transaction within a transaction`.
4. **Temp DB.** `PRAGMA temp.journal_mode=mvcc` is ignored; temp reports `wal`.
5. **Classic path default.** Without the pragma, behavior matches §1.6
   (single write reservation, WAL snapshots).
6. **Upstream anomaly TODOs.** Do not claim protection against phantoms, cursor
   lost updates, read skew, or write skew beyond Turso v0.7.2.

## Phase map

| Phase | Deliverable |
| --- | --- |
| **1 (current)** | Clock, `MvStore` tx registry + write-set WW conflicts, pragma/BEGIN surface, classic catalog DML under concurrent txs |
| **1.5** | Row-version chains + `MvccCursor` merge with base tables |
| **2** | Durable logical log, header version 255, recovery, checkpoint SM |
| **3** | GC, dual-cursor isolation, schema generation cookie |

## Testing

- Unit: `ManagedAdvancedFeatureBoundaryTests`, `ManagedTransactionModeLockingTests`
  concurrent cases, `ManagedJournalPageMigrationTests` MVCC case.
- Conformance: clear the 11 MVCC markers in
  `managed-sqltest-expected-failures.txt` as each case goes green.
- Do not greenwash: remove a failure line only when the case passes for real.
