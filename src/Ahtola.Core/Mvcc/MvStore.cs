namespace Ahtola.Core.Mvcc;

/// <summary>Opaque MVCC transaction identifier (Turso <c>TxID</c>).</summary>
internal readonly record struct MvccTxId(ulong Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Row identity within an MVCC table (Turso <c>RowID</c>).</summary>
internal readonly record struct MvccRowId(long TableId, long RowId);

/// <summary>Lifecycle state of an MVCC transaction (Turso <c>TransactionState</c>).</summary>
internal enum MvccTransactionState : byte
{
    Active = 0,
    Preparing = 1,
    Committed = 2,
    Aborted = 3,
}

/// <summary>
/// One in-flight or completed MVCC transaction. Phase 1 tracks timestamps and
/// write sets so concurrent commits can detect write-write conflicts; full
/// row-version chains land as the DML path routes through the store.
/// </summary>
internal sealed class MvccTransaction
{
    private readonly object _gate = new();
    private readonly HashSet<MvccRowId> _writeSet = [];
    private MvccTransactionState _state = MvccTransactionState.Active;
    private ulong? _commitTimestamp;

    internal MvccTransaction(MvccTxId id, ulong beginTimestamp)
    {
        Id = id;
        BeginTimestamp = beginTimestamp;
    }

    internal MvccTxId Id { get; }

    internal ulong BeginTimestamp { get; }

    internal MvccTransactionState State
    {
        get { lock (_gate) return _state; }
    }

    internal ulong? CommitTimestamp
    {
        get { lock (_gate) return _commitTimestamp; }
    }

    internal void RecordWrite(MvccRowId rowId)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _writeSet.Add(rowId);
        }
    }

    internal IReadOnlyCollection<MvccRowId> SnapshotWriteSet()
    {
        lock (_gate)
            return _writeSet.ToArray();
    }

    internal void MarkPreparing(ulong commitTimestamp)
    {
        lock (_gate)
        {
            ThrowIfNotActive();
            _state = MvccTransactionState.Preparing;
            _commitTimestamp = commitTimestamp;
        }
    }

    internal void MarkCommitted()
    {
        lock (_gate)
        {
            if (_state != MvccTransactionState.Preparing)
                throw new InvalidOperationException("MVCC transaction is not preparing.");
            _state = MvccTransactionState.Committed;
        }
    }

    internal void MarkAborted()
    {
        lock (_gate)
        {
            if (_state is MvccTransactionState.Committed)
                throw new InvalidOperationException("Cannot abort a committed MVCC transaction.");
            _state = MvccTransactionState.Aborted;
        }
    }

    private void ThrowIfNotActive()
    {
        if (_state != MvccTransactionState.Active)
            throw new InvalidOperationException($"MVCC transaction is {_state}.");
    }
}

/// <summary>
/// Per-database MVCC store (Turso <c>MvStore</c>). Phase 1 provides clock-ordered
/// concurrent transactions, write-set conflict detection, and the attachment
/// point for later row-version / logical-log work. Classic catalog DML still
/// mutates <see cref="EmbeddedDatabase"/> tables; concurrent mode uses this
/// store for transaction identity and WW checks at commit.
/// </summary>
internal sealed class MvStore
{
    private readonly ILogicalClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, MvccTransaction> _transactions = [];
    private readonly List<(ulong CommitTs, HashSet<MvccRowId> Writes)> _committedWriteHistory = [];
    private ulong _nextTxId = 1;
    private ulong? _exclusiveTxId;

    internal MvStore(ILogicalClock? clock = null)
    {
        _clock = clock ?? new MvccClock();
    }

    internal ILogicalClock Clock => _clock;

    /// <summary>Begin a non-exclusive concurrent MVCC transaction.</summary>
    internal MvccTransaction BeginTransaction()
    {
        lock (_gate)
        {
            if (_exclusiveTxId is not null)
                throw new EmbeddedBusyException();

            var beginTs = _clock is MvccClock mvccClock
                ? mvccClock.GetBeginTimestamp()
                : _clock.GetTimestamp(static _ => { });
            var id = new MvccTxId(_nextTxId++);
            var tx = new MvccTransaction(id, beginTs);
            _transactions.Add(id.Value, tx);
            return tx;
        }
    }

    /// <summary>
    /// Begin or upgrade to an exclusive MVCC write transaction (Turso
    /// <c>begin_exclusive_tx</c>). Only one exclusive writer may be active.
    /// </summary>
    internal MvccTransaction BeginExclusiveTransaction(MvccTxId? existing = null)
    {
        lock (_gate)
        {
            if (_exclusiveTxId is { } held
                && (existing is null || held != existing.Value.Value))
            {
                throw new EmbeddedBusyException();
            }

            if (existing is { } existingId
                && _transactions.TryGetValue(existingId.Value, out var existingTx))
            {
                _exclusiveTxId = existingId.Value;
                return existingTx;
            }

            var beginTs = _clock is MvccClock mvccClock
                ? mvccClock.GetBeginTimestamp()
                : _clock.GetTimestamp(static _ => { });
            var id = new MvccTxId(_nextTxId++);
            var tx = new MvccTransaction(id, beginTs);
            _transactions.Add(id.Value, tx);
            _exclusiveTxId = id.Value;
            return tx;
        }
    }

    internal bool TryGetTransaction(MvccTxId id, out MvccTransaction? transaction)
    {
        lock (_gate)
            return _transactions.TryGetValue(id.Value, out transaction);
    }

    internal void RecordWrite(MvccTxId id, MvccRowId rowId)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(id.Value, out var tx))
                throw new InvalidOperationException($"Unknown MVCC transaction {id}.");
            tx.RecordWrite(rowId);
        }
    }

    /// <summary>
    /// Commit with first-committer-wins write-write conflict detection.
    /// The commit timestamp is generated and the transaction enters
    /// <see cref="MvccTransactionState.Preparing"/> atomically under the clock lock.
    /// </summary>
    internal void Commit(MvccTxId id)
    {
        MvccTransaction tx;
        HashSet<MvccRowId> writes;
        lock (_gate)
        {
            if (!_transactions.TryGetValue(id.Value, out tx!))
                throw new InvalidOperationException($"Unknown MVCC transaction {id}.");
            if (tx.State != MvccTransactionState.Active)
                throw new InvalidOperationException($"MVCC transaction {id} is {tx.State}.");
            writes = tx.SnapshotWriteSet().ToHashSet();
        }

        lock (_gate)
        {
            foreach (var (commitTs, committedWrites) in _committedWriteHistory)
            {
                if (commitTs < tx.BeginTimestamp)
                    continue;
                if (writes.Count == 0)
                    break;
                if (writes.Overlaps(committedWrites))
                    throw new EmbeddedWriteWriteConflictException();
            }
        }

        _clock.GetTimestamp(ts =>
        {
            // Publish Preparing under the clock lock (Turso SI invariant).
            tx.MarkPreparing(ts);
        });

        lock (_gate)
        {
            var commitTs = tx.CommitTimestamp
                ?? throw new InvalidOperationException("Preparing transaction missing commit timestamp.");
            foreach (var (otherTs, committedWrites) in _committedWriteHistory)
            {
                if (otherTs < tx.BeginTimestamp || otherTs >= commitTs)
                    continue;
                if (writes.Overlaps(committedWrites))
                {
                    tx.MarkAborted();
                    ClearExclusive(id);
                    _transactions.Remove(id.Value);
                    throw new EmbeddedWriteWriteConflictException();
                }
            }

            tx.MarkCommitted();
            if (writes.Count != 0)
                _committedWriteHistory.Add((commitTs, writes));
            ClearExclusive(id);
            _transactions.Remove(id.Value);
            PruneHistoryLocked(tx.BeginTimestamp);
        }
    }

    internal void Rollback(MvccTxId id)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(id.Value, out var tx))
                return;
            if (tx.State is MvccTransactionState.Committed)
                throw new InvalidOperationException("Cannot roll back a committed MVCC transaction.");
            tx.MarkAborted();
            ClearExclusive(id);
            _transactions.Remove(id.Value);
        }
    }

    private void ClearExclusive(MvccTxId id)
    {
        if (_exclusiveTxId == id.Value)
            _exclusiveTxId = null;
    }

    private void PruneHistoryLocked(ulong minBegin)
    {
        ulong lowestActiveBegin = minBegin;
        foreach (var active in _transactions.Values)
        {
            if (active.State is MvccTransactionState.Active or MvccTransactionState.Preparing)
                lowestActiveBegin = Math.Min(lowestActiveBegin, active.BeginTimestamp);
        }

        _committedWriteHistory.RemoveAll(entry => entry.CommitTs < lowestActiveBegin);
    }
}
