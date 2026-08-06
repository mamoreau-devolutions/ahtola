using AwesomeAssertions;
using Ahtola.Core;
using Ahtola.Core.Execution;

namespace Ahtola.Tests;

/// <summary>
/// Covers VdbeInsertFlags on Insert/Update (inventory: vdbe-insert-update-flag-semantics).
/// </summary>
public sealed class VdbeInsertFlagSemanticsTests
{
    [Test]
    public void RequireSeekRejectsUnpositionedCursor()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 0,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(1)], 1),
            Commit = () => 1,
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new InsertInstruction(new Cursor(0), VdbeInsertFlags.RequireSeek),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        var error = Assert.Throws<InvalidOperationException>(() => statement.StepResumable());
        error!.Message.Should().Contain("RequireSeek");
    }

    [Test]
    public void SkipStatementChangeCountDoesNotIncrementRowsAffected()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(2)], 1),
            Commit = () => null,
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new UpdateInstruction(new Cursor(0), VdbeInsertFlags.SkipStatementChangeCount),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.RowsAffected.Should().Be(0);
    }

    [Test]
    public void DefaultInsertStillCountsRowsAffected()
    {
        var writeTarget = new VdbeWriteTarget
        {
            TableName = "t",
            RowCount = 1,
            GetRow = _ => [SqlValue.Integer(1)],
            GetRowId = _ => 1,
            MutateRow = _ => new VdbeRowMutation([SqlValue.Integer(2)], 1),
            Commit = () => null,
        };

        var program = new VdbeProgram(
            registerCount: 0,
            cursorCount: 1,
            [
                new OpenWriteCursorInstruction(new Cursor(0), "t", 1),
                new RewindCursorInstruction(new Cursor(0), new ProgramCounter(4)),
                new UpdateInstruction(new Cursor(0)),
                new NextInstruction(new Cursor(0), new ProgramCounter(2)),
                new HaltInstruction(),
            ]);

        using var statement = new ResumableStatement(program, writeTargets: [writeTarget]);
        statement.StepResumable().Should().Be(ResumableStatementStepResult.Done);
        statement.RowsAffected.Should().Be(1);
    }

    [Test]
    public void ExplainRendersInsertFlags()
    {
        var insert = new InsertInstruction(
            new Cursor(2),
            VdbeInsertFlags.RequireSeek | VdbeInsertFlags.SkipLastRowid);
        var (_, p2, _, p4, comment) = VdbeExplain.Describe(insert);
        p2.Should().Be((long)(VdbeInsertFlags.RequireSeek | VdbeInsertFlags.SkipLastRowid));
        p4.Should().Contain("RequireSeek");
        comment.Should().Contain("flags=");
    }
}
