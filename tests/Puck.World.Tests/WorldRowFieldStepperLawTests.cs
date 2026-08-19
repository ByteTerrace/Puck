using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <see cref="WorldRowFieldStepper"/> — the generic field-path walker <c>world.row.step</c> addresses a
/// leaf through: a number adds delta, a boolean toggles on any nonzero delta, a named enum cycles by delta's sign
/// (wrapping), and anything else (a vector/object leaf, a non-enum string, an unknown member, a malformed index)
/// refuses by name. Every claim pairs a denied case with a one-value-different passing control.
/// </summary>
public sealed class WorldRowFieldStepperLawTests {
    private enum TestMode {
        Alpha,
        Beta,
        Gamma,
    }
    private sealed record TestLeaf(long Count);
    private sealed record TestRow(long Count, double Ratio, bool Enabled, TestMode Mode, string Label, TestLeaf Nested, TestLeaf[] Items);

    private static JsonNode Row(long count = 3L, double ratio = 1.5, bool enabled = false, TestMode mode = TestMode.Alpha, string label = "fixed", long nestedCount = 7L, params long[] items) {
        var array = new JsonArray();

        foreach (var item in items) {
            array.Add(value: new JsonObject { ["count"] = item });
        }

        return new JsonObject {
            ["count"] = count,
            ["ratio"] = ratio,
            ["enabled"] = enabled,
            ["mode"] = mode.ToString(),
            ["label"] = label,
            ["nested"] = new JsonObject { ["count"] = nestedCount },
            ["items"] = array,
        };
    }

    [Fact]
    public void IntegerField_AddsDeltaAndReportsOldNew() {
        var row = Row(count: 3L);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "count", delta: 4f, oldText: out var oldText, newText: out var newText, error: out var error));
        Assert.Null(@object: error);
        Assert.Equal(expected: "3", actual: oldText);
        Assert.Equal(expected: "7", actual: newText);
        Assert.Equal(expected: 7L, actual: ((long)row["count"]!));
    }
    [Fact]
    public void IntegerField_NegativeDeltaSubtracts() {
        var row = Row(count: 3L);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "count", delta: -5f, oldText: out _, newText: out var newText, error: out _));
        Assert.Equal(expected: "-2", actual: newText);
    }
    [Fact]
    public void DoubleField_AddsFractionalDelta() {
        var row = Row(ratio: 1.5);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "ratio", delta: 0.25f, oldText: out var oldText, newText: out var newText, error: out var error));
        Assert.Null(@object: error);
        Assert.Equal(expected: "1.5", actual: oldText);
        Assert.Equal(expected: "1.75", actual: newText);
    }
    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(0.001f)]
    public void BooleanField_TogglesOnAnyNonzeroDelta(float delta) {
        var row = Row(enabled: false);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "enabled", delta: delta, oldText: out var oldText, newText: out var newText, error: out var error));
        Assert.Null(@object: error);
        Assert.Equal(expected: "false", actual: oldText);
        Assert.Equal(expected: "true", actual: newText);
        Assert.True(condition: ((bool)row["enabled"]!));
    }
    [Fact]
    public void BooleanField_TogglesBackOnSecondStep() {
        var row = Row(enabled: true);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "enabled", delta: 1f, oldText: out var oldText, newText: out var newText, error: out _));
        Assert.Equal(expected: "true", actual: oldText);
        Assert.Equal(expected: "false", actual: newText);
    }
    [Fact]
    public void EnumField_PositiveDeltaCyclesForwardAndWraps() {
        var row = Row(mode: TestMode.Gamma);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "mode", delta: 1f, oldText: out var oldText, newText: out var newText, error: out var error));
        Assert.Null(@object: error);
        Assert.Equal(expected: "Gamma", actual: oldText);
        Assert.Equal(expected: "Alpha", actual: newText);
    }
    [Fact]
    public void EnumField_NegativeDeltaCyclesBackwardAndWraps() {
        var row = Row(mode: TestMode.Alpha);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "mode", delta: -1f, oldText: out var oldText, newText: out var newText, error: out _));
        Assert.Equal(expected: "Alpha", actual: oldText);
        Assert.Equal(expected: "Gamma", actual: newText);
    }
    [Fact]
    public void EnumField_ZeroDelta_RefusesByName() {
        var row = Row(mode: TestMode.Beta);

        Assert.False(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "mode", delta: 0f, oldText: out _, newText: out _, error: out var error));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "delta must be nonzero to cycle an enum");
        // Control: the identical field with a nonzero delta steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "mode", delta: 1f, oldText: out _, newText: out _, error: out var controlError));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void NonEnumStringField_RefusesByName() {
        var row = Row(label: "fixed");

        Assert.False(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "label", delta: 1f, oldText: out _, newText: out _, error: out var error));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "not a steppable field");
        // Control: a genuinely steppable sibling field on the same row still steps.
        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "count", delta: 1f, oldText: out _, newText: out _, error: out var controlError));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void NestedObjectField_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "nested", delta: 1f, oldText: out _, newText: out _, error: out var error));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "not a steppable field");
        // Control: walking INTO the nested object to its own leaf steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "nested.count", delta: 1f, oldText: out var oldText, newText: out var newText, error: out var controlError));
        Assert.Null(@object: controlError);
        Assert.Equal(expected: "7", actual: oldText);
        Assert.Equal(expected: "8", actual: newText);
    }
    [Fact]
    public void IndexedArrayElementField_Steps() {
        var row = Row(items: [10L, 20L, 30L]);

        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "items[1].count", delta: 5f, oldText: out var oldText, newText: out var newText, error: out var error));
        Assert.Null(@object: error);
        Assert.Equal(expected: "20", actual: oldText);
        Assert.Equal(expected: "25", actual: newText);
        // The untouched neighbors are unaffected.
        Assert.Equal(expected: 10L, actual: ((long)row["items"]![0]!["count"]!));
        Assert.Equal(expected: 30L, actual: ((long)row["items"]![2]!["count"]!));
    }
    [Fact]
    public void IndexedArrayElement_OutOfRange_RefusesByName() {
        var row = Row(items: [10L, 20L]);

        Assert.False(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "items[5].count", delta: 1f, oldText: out _, newText: out _, error: out var error));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "out of range");
        // Control: an in-range index on the identical array steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "items[1].count", delta: 1f, oldText: out _, newText: out _, error: out var controlError));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void UnknownMember_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "notAField", delta: 1f, oldText: out _, newText: out _, error: out var error));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "unknown or empty member");
        // Control: the correctly spelled sibling steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "count", delta: 1f, oldText: out _, newText: out _, error: out var controlError));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void EmptyPath_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "", delta: 1f, oldText: out _, newText: out _, error: out var error));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "empty path");
    }
    [Fact]
    public void NonFiniteDelta_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "count", delta: float.NaN, oldText: out _, newText: out _, error: out var error));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "finite");
        // Control: a finite delta on the identical field steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(root: row, rowType: typeof(TestRow), fieldPath: "count", delta: 1f, oldText: out _, newText: out _, error: out var controlError));
        Assert.Null(@object: controlError);
    }
}
