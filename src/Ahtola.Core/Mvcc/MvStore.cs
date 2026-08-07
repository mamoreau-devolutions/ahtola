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
/// One in-flight MVCC transaction: begin timestamp, write set, and lifecycle.
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
/// Per-database MVCC store (Turso <c>MvStore</c>): logical clock, version chains,
/// concurrent transactions, and first-committer-wins write-write conflicts.
/// </summary>
internal sealed class MvStore
{
    private readonly ILogicalClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, MvccTransaction> _transactions = [];
    private readonly Dictionary<ulong, MvccTransactionState> _finalizedStates = [];
    private readonly Dictionary<ulong, ulong> _finalizedCommitTimestamps = [];
    private readonly Dictionary<MvccRowId, List<MvccRowVersion>> _rows = [];
    private readonly Dictionary<string, long> _tableIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, string> _tableNames = [];
    private long _nextTableId = -2;
    private ulong _nextTxId = 1;
    private ulong _nextVersionId = 1;
    private ulong? _exclusiveTxId;

    internal MvStore(ILogicalClock? clock = null)
    {
        _clock = clock ?? new MvccClock();
    }

    internal ILogicalClock Clock => _clock;

    /// <summary>Stable negative table id for <paramref name="tableName"/>.</summary>
    internal long GetOrCreateTableId(string tableName)
    {
        ArgumentException.ThrowIfNullOrEmpty(tableName);
        lock (_gate)
        {
            if (_tableIds.TryGetValue(tableName, out var id))
                return id;
            id = _nextTableId--;
            _tableIds[tableName] = id;
            _tableNames[id] = tableName;
            return id;
        }
    }

    internal bool TryGetTableName(long tableId, out string? name)
    {
        lock (_gate)
            return _tableNames.TryGetValue(tableId, out name);
    }

    internal MvccTransaction BeginTransaction()
    {
        lock (_gate)
        {
            if (_exclusiveTxId is not null)
                throw new EmbeddedBusyException();

            var beginTs = NextBeginTimestamp();
            var id = new MvccTxId(_nextTxId++);
            var tx = new MvccTransaction(id, beginTs);
            _transactions.Add(id.Value, tx);
            return tx;
        }
    }

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

            var beginTs = NextBeginTimestamp();
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

    /// <summary>Insert a new live version created by <paramref name="txId"/>.</summary>
    internal void Insert(MvccTxId txId, MvccRowId rowId, SqlValue[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        lock (_gate)
        {
            var tx = RequireActive(txId);
            var version = new MvccRowVersion(
                _nextVersionId++,
                begin: MvccStamp.FromTxId(txId),
                end: null,
                cells: (SqlValue[])cells.Clone());
            if (!_rows.TryGetValue(rowId, out var chain))
            {
                chain = [];
                _rows[rowId] = chain;
            }

            chain.Add(version);
            tx.RecordWrite(rowId);
        }
    }

    /// <summary>
    /// Delete the version visible to <paramref name="txId"/> by setting its end
    /// stamp. Returns false when no visible version exists.
    /// </summary>
    internal bool Delete(MvccTxId txId, MvccRowId rowId)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            if (!_rows.TryGetValue(rowId, out var chain))
                return false;

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (!IsVisibleTo(version, tx))
                    continue;
                if (IsWriteWriteConflict(tx, version))
                    throw new EmbeddedWriteWriteConflictException();

                version.End = MvccStamp.FromTxId(txId);
                tx.RecordWrite(rowId);
                return true;
            }

            return false;
        }
    }

    /// <summary>Delete-then-insert update (Turso <c>update</c>).</summary>
    internal bool Update(MvccTxId txId, MvccRowId rowId, SqlValue[] cells)
    {
        if (!Delete(txId, rowId))
            return false;
        Insert(txId, rowId, cells);
        return true;
    }

    /// <summary>Read the version visible to <paramref name="txId"/>, if any.</summary>
    internal bool TryRead(MvccTxId txId, MvccRowId rowId, out SqlValue[]? cells)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            cells = null;
            if (!_rows.TryGetValue(rowId, out var chain))
                return false;

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (!IsVisibleTo(version, tx))
                    continue;
                if (version.IsTombstone)
                    return false;
                cells = (SqlValue[])version.Cells.Clone();
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Scan every row id that has at least one version visible to
    /// <paramref name="txId"/> (newest visible non-tombstone wins).
    /// </summary>
    internal IReadOnlyList<(MvccRowId RowId, SqlValue[] Cells)> ScanVisible(MvccTxId txId)
    {
        lock (_gate)
        {
            var tx = RequireActive(txId);
            var results = new List<(MvccRowId, SqlValue[])>();
            foreach (var (rowId, chain) in _rows)
            {
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    var version = chain[i];
                    if (!IsVisibleTo(version, tx))
                        continue;
                    if (!version.IsTombstone)
                        results.Add((rowId, (SqlValue[])version.Cells.Clone()));
                    break;
                }
            }

            return results;
        }
    }

    /// <summary>Record a write-set entry without mutating version chains (catalog-path DML).</summary>
    internal void RecordWrite(MvccTxId id, MvccRowId rowId)
    {
        lock (_gate)
        {
            var tx = RequireActive(id);
            tx.RecordWrite(rowId);
        }
    }

    /// <summary>
    /// Commit with first-committer-wins WW detection. Rewrites in-flight TxID
    /// stamps on version chains to the commit timestamp (Turso rewrite step).
    /// </summary>
    internal void Commit(MvccTxId id)
    {
        MvccTransaction tx;
        HashSet<MvccRowId> writes;
        lock (_gate)
        {
            tx = RequireActive(id);
            writes = tx.SnapshotWriteSet().ToHashSet();

            foreach (var rowId in writes)
            {
                if (!_rows.TryGetValue(rowId, out var chain))
                    continue;
                foreach (var version in chain)
                {
                    if (IsWriteWriteConflict(tx, version))
                        throw new EmbeddedWriteWriteConflictException();
                }
            }
        }

        _clock.GetTimestamp(ts => tx.MarkPreparing(ts));

        lock (_gate)
        {
            var commitTs = tx.CommitTimestamp
                ?? throw new InvalidOperationException("Preparing transaction missing commit timestamp.");

            foreach (var rowId in writes)
            {
                if (!_rows.TryGetValue(rowId, out var chain))
                    continue;
                foreach (var version in chain)
                {
                    if (IsWriteWriteConflict(tx, version))
                    {
                        AbortLocked(id, tx);
                        throw new EmbeddedWriteWriteConflictException();
                    }
                }
            }

            RewriteStampsLocked(id, commitTs);
            tx.MarkCommitted();
            _finalizedStates[id.Value] = MvccTransactionState.Committed;
            _finalizedCommitTimestamps[id.Value] = commitTs;
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
            AbortLocked(id, tx);
        }
    }

    private void AbortLocked(MvccTxId id, MvccTransaction tx)
    {
        foreach (var (rowId, chain) in _rows.ToArray())
        {
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var version = chain[i];
                if (version.Begin is { IsTimestamp: false, Value: var beginTx } && beginTx == id.Value)
                {
                    chain.RemoveAt(i);
                    continue;
                }

                if (version.End is { IsTimestamp: false, Value: var endTx } && endTx == id.Value)
                    version.End = null;
            }

            if (chain.Count == 0)
                _rows.Remove(rowId);
        }

        tx.MarkAborted();
        _finalizedStates[id.Value] = MvccTransactionState.Aborted;
        ClearExclusive(id);
        _transactions.Remove(id.Value);
    }

    private void RewriteStampsLocked(MvccTxId id, ulong commitTs)
    {
        var stamp = MvccStamp.FromTimestamp(commitTs);
        foreach (var chain in _rows.Values)
        {
            foreach (var version in chain)
            {
                if (version.Begin is { IsTimestamp: false, Value: var beginTx } && beginTx == id.Value)
                    version.Begin = stamp;
                if (version.End is { IsTimestamp: false, Value: var endTx } && endTx == id.Value)
                    version.End = stamp;
            }
        }
    }

    private MvccTransaction RequireActive(MvccTxId id)
    {
        if (!_transactions.TryGetValue(id.Value, out var tx))
            throw new InvalidOperationException($"Unknown MVCC transaction {id}.");
        if (tx.State != MvccTransactionState.Active)
            throw new InvalidOperationException($"MVCC transaction {id} is {tx.State}.");
        return tx;
    }

    private ulong NextBeginTimestamp()
        => _clock is MvccClock mvccClock
            ? mvccClock.GetBeginTimestamp()
            : _clock.GetTimestamp(static _ => { });

    private bool IsVisibleTo(MvccRowVersion version, MvccTransaction tx)
        => IsBeginVisible(version, tx) && IsEndVisible(version, tx);

    private bool IsBeginVisible(MvccRowVersion version, MvccTransaction tx)
    {
        if (version.Begin is null)
            return true;

        var begin = version.Begin.Value;
        if (begin.IsTimestamp)
            return begin.Value <= tx.BeginTimestamp;

        if (begin.Value == tx.Id.Value)
            return true;

        return LookupCreatorVisibility(begin.Value, tx);
    }

    private bool IsEndVisible(MvccRowVersion version, MvccTransaction tx)
    {
        // True means the version is still live for this reader (deletion not yet visible).
        if (version.End is null)
            return true;

        var end = version.End.Value;
        if (end.IsTimestamp)
            return end.Value > tx.BeginTimestamp;

        if (end.Value == tx.Id.Value)
            return false;

        return !LookupCreatorVisibility(end.Value, tx);
    }

    private bool LookupCreatorVisibility(ulong otherTxId, MvccTransaction reader)
    {
        if (_transactions.TryGetValue(otherTxId, out var other))
        {
            return other.State switch
            {
                MvccTransactionState.Committed =>
                    other.CommitTimestamp is { } cts && cts <= reader.BeginTimestamp,
                MvccTransactionState.Preparing =>
                    other.CommitTimestamp is { } pts && pts <= reader.BeginTimestamp,
                MvccTransactionState.Active => false,
                MvccTransactionState.Aborted => false,
                _ => false,
            };
        }

        if (_finalizedStates.TryGetValue(otherTxId, out var finalized))
        {
            if (finalized != MvccTransactionState.Committed)
                return false;
            return _finalizedCommitTimestamps.TryGetValue(otherTxId, out var cts)
                && cts <= reader.BeginTimestamp;
        }

        return false;
    }

    private bool IsWriteWriteConflict(MvccTransaction tx, MvccRowVersion version)
    {
        if (version.End is null)
            return false;

        var end = version.End.Value;
        if (end.IsTimestamp)
            return end.Value > tx.BeginTimestamp;

        if (end.Value == tx.Id.Value)
            return false;

        if (_transactions.TryGetValue(end.Value, out var other))
        {
            return other.State is MvccTransactionState.Active
                or MvccTransactionState.Preparing
                or MvccTransactionState.Committed;
        }

        if (_finalizedStates.TryGetValue(end.Value, out var finalized)
            && finalized == MvccTransactionState.Committed
            && _finalizedCommitTimestamps.TryGetValue(end.Value, out var cts))
        {
            return cts > tx.BeginTimestamp;
        }

        return false;
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

        foreach (var (rowId, chain) in _rows.ToArray())
        {
            chain.RemoveAll(version =>
                version.End is { IsTimestamp: true, Value: var endTs }
                && endTs < lowestActiveBegin);

            if (chain.Count == 0)
                _rows.Remove(rowId);
        }

        if (_finalizedStates.Count > 4096)
        {
            var stale = _finalizedCommitTimestamps
                .Where(pair => pair.Value < lowestActiveBegin)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in stale)
            {
                _finalizedStates.Remove(key);
                _finalizedCommitTimestamps.Remove(key);
            }
        }
    }
}
