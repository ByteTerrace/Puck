using Xunit;

namespace Puck.State.Tests;

/// <summary>Guards 128-bit boundary arithmetic, saturation, and refusal in <see cref="StateRow.TryAdmitWrite"/> at
/// <see cref="long.MinValue"/> and <see cref="long.MaxValue"/>.</summary>
public sealed class StateBoundsLawTests {
    private static StateRow MakeRow(long? min = null, long? max = null, StateOverflow overflow = StateOverflow.Refuse) =>
        new(
            Name: CellName.Parse(candidate: "boundary"),
            Kind: CellKind.Int,
            Min: min,
            Max: max,
            Overflow: overflow,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 0L)]
        );

    [Fact]
    public void Add_AtMax_WithPositive_RefusesByDefault() {
        var row = MakeRow(min: null, max: null, overflow: StateOverflow.Refuse);
        var admitted = row.TryAdmitWrite(
            current: long.MaxValue,
            operand: 1L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.False(admitted);
        Assert.Equal(0L, stored);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Add_AtMax_WithPositive_SaturatesToMax() {
        var row = MakeRow(min: null, max: null, overflow: StateOverflow.Saturate);
        var admitted = row.TryAdmitWrite(
            current: long.MaxValue,
            operand: 1000L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.True(admitted);
        Assert.Equal(long.MaxValue, stored);
        Assert.Empty(reason);
    }

    [Fact]
    public void Add_AtMin_WithNegative_RefusesByDefault() {
        var row = MakeRow(min: null, max: null, overflow: StateOverflow.Refuse);
        var admitted = row.TryAdmitWrite(
            current: long.MinValue,
            operand: -1L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.False(admitted);
        Assert.Equal(0L, stored);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void Add_AtMin_WithNegative_SaturatesToMin() {
        var row = MakeRow(min: null, max: null, overflow: StateOverflow.Saturate);
        var admitted = row.TryAdmitWrite(
            current: long.MinValue,
            operand: -9999L,
            write: StateWriteKind.Add,
            stored: out var stored,
            reason: out var reason
        );

        Assert.True(admitted);
        Assert.Equal(long.MinValue, stored);
        Assert.Empty(reason);
    }

    [Fact]
    public void Add_MaxToMax_DoesNotOverflowInt128_SaturatesOrRefuses() {
        var saturateRow = MakeRow(overflow: StateOverflow.Saturate);
        var refuseRow = MakeRow(overflow: StateOverflow.Refuse);

        Assert.True(saturateRow.TryAdmitWrite(
            current: long.MaxValue,
            operand: long.MaxValue,
            write: StateWriteKind.Add,
            stored: out var satStored,
            reason: out var satReason
        ));
        Assert.Equal(long.MaxValue, satStored);
        Assert.Empty(satReason);

        Assert.False(refuseRow.TryAdmitWrite(
            current: long.MaxValue,
            operand: long.MaxValue,
            write: StateWriteKind.Add,
            stored: out var refStored,
            reason: out var refReason
        ));
        Assert.Equal(0L, refStored);
        Assert.NotEmpty(refReason);
    }

    [Fact]
    public void Add_MinToMin_DoesNotUnderflowInt128_SaturatesOrRefuses() {
        var saturateRow = MakeRow(overflow: StateOverflow.Saturate);
        var refuseRow = MakeRow(overflow: StateOverflow.Refuse);

        Assert.True(saturateRow.TryAdmitWrite(
            current: long.MinValue,
            operand: long.MinValue,
            write: StateWriteKind.Add,
            stored: out var satStored,
            reason: out var satReason
        ));
        Assert.Equal(long.MinValue, satStored);
        Assert.Empty(satReason);

        Assert.False(refuseRow.TryAdmitWrite(
            current: long.MinValue,
            operand: long.MinValue,
            write: StateWriteKind.Add,
            stored: out var refStored,
            reason: out var refReason
        ));
        Assert.Equal(0L, refStored);
        Assert.NotEmpty(refReason);
    }

    [Fact]
    public void Set_BeyondAuthoredBounds_RefusesOrSaturates() {
        var refuseRow = MakeRow(min: -100L, max: 100L, overflow: StateOverflow.Refuse);
        var saturateRow = MakeRow(min: -100L, max: 100L, overflow: StateOverflow.Saturate);

        Assert.False(refuseRow.TryAdmitWrite(
            current: 0L,
            operand: 101L,
            write: StateWriteKind.Set,
            stored: out var rStoredHigh,
            reason: out var rReasonHigh
        ));
        Assert.Equal(0L, rStoredHigh);
        Assert.NotEmpty(rReasonHigh);

        Assert.False(refuseRow.TryAdmitWrite(
            current: 0L,
            operand: -101L,
            write: StateWriteKind.Set,
            stored: out var rStoredLow,
            reason: out var rReasonLow
        ));
        Assert.Equal(0L, rStoredLow);
        Assert.NotEmpty(rReasonLow);

        Assert.True(saturateRow.TryAdmitWrite(
            current: 0L,
            operand: 500L,
            write: StateWriteKind.Set,
            stored: out var sStoredHigh,
            reason: out var sReasonHigh
        ));
        Assert.Equal(100L, sStoredHigh);
        Assert.Empty(sReasonHigh);

        Assert.True(saturateRow.TryAdmitWrite(
            current: 0L,
            operand: -500L,
            write: StateWriteKind.Set,
            stored: out var sStoredLow,
            reason: out var sReasonLow
        ));
        Assert.Equal(-100L, sStoredLow);
        Assert.Empty(sReasonLow);
    }

    [Fact]
    public void ExactAuthoredBounds_AdmitsWithoutClamping() {
        var row = MakeRow(min: -50L, max: 50L, overflow: StateOverflow.Refuse);

        Assert.True(row.TryAdmitWrite(
            current: 0L,
            operand: 50L,
            write: StateWriteKind.Set,
            stored: out var s50,
            reason: out var r50
        ));
        Assert.Equal(50L, s50);
        Assert.Empty(r50);

        Assert.True(row.TryAdmitWrite(
            current: 0L,
            operand: -50L,
            write: StateWriteKind.Set,
            stored: out var sNeg50,
            reason: out var rNeg50
        ));
        Assert.Equal(-50L, sNeg50);
        Assert.Empty(rNeg50);
    }
}
