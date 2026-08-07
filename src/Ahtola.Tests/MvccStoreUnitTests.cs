using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Mvcc;

namespace Ahtola.Tests;

public sealed class MvccStoreUnitTests
{
    [Test]
    public void ClockPublishesTimestampAtomicallyBeforeReturning()
    {
        var clock = new MvccClock();
        ulong? published = null;
        var ts = clock.GetCommitTimestamp(value => published = value);
        published.Should().Be(ts);
        clock.GetBeginTimestamp().Should().Be(ts + 1);
    }

    [Test]
    public void ConcurrentCommitsWithDisjointWritesSucceed()
    {
        var store = new MvStore();
        var a = store.BeginTransaction();
        var b = store.BeginTransaction();
        store.RecordWrite(a.Id, new MvccRowId(TableId: -1, RowId: 1));
        store.RecordWrite(b.Id, new MvccRowId(TableId: -1, RowId: 2));
        store.Commit(a.Id);
        store.Commit(b.Id);
    }

    [Test]
    public void ConcurrentCommitsWithOverlappingWritesRaiseWriteWriteConflict()
    {
        var store = new MvStore();
        var a = store.BeginTransaction();
        var b = store.BeginTransaction();
        store.RecordWrite(a.Id, new MvccRowId(TableId: -1, RowId: 7));
        store.RecordWrite(b.Id, new MvccRowId(TableId: -1, RowId: 7));
        store.Commit(a.Id);
        var act = () => store.Commit(b.Id);
        act.Should().Throw<EmbeddedWriteWriteConflictException>();
    }

    [Test]
    public void ExclusiveTransactionBlocksPeerBegin()
    {
        var store = new MvStore();
        _ = store.BeginExclusiveTransaction();
        var act = () => store.BeginTransaction();
        act.Should().Throw<EmbeddedBusyException>();
    }
}
