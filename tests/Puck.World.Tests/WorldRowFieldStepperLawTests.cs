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
    // A row whose `ratio` (a double CLR field) carries a RAW JSON literal — used to spell a whole-numbered float as
    // the integer literal `8`, exactly as SerializeToNode renders it, so the CLR-vs-spelling typing is exercised.
    private static JsonObject RowWithRatioLiteral(string ratioJson) => new() {
        ["count"] = 3L,
        ["ratio"] = JsonNode.Parse(json: ratioJson),
        ["enabled"] = false,
        ["mode"] = TestMode.Alpha.ToString(),
        ["label"] = "fixed",
        ["nested"] = new JsonObject { ["count"] = 7L },
        ["items"] = new JsonArray(),
    };

    [Fact]
    public void IntegerField_AddsDeltaAndReportsOldNew() {
        var row = Row(count: 3L);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 4f, error: out var error, fieldPath: "count", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: error);
        Assert.Equal(actual: oldText, expected: "3");
        Assert.Equal(actual: newText, expected: "7");
        Assert.Equal(expected: 7L, actual: ((long)row["count"]!));
    }
    [Fact]
    public void IntegerField_NegativeDeltaSubtracts() {
        var row = Row(count: 3L);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: -5f, error: out _, fieldPath: "count", newText: out var newText, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Equal(actual: newText, expected: "-2");
    }
    [Fact]
    public void DoubleField_AddsFractionalDelta() {
        var row = Row(ratio: 1.5);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 0.25f, error: out var error, fieldPath: "ratio", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: error);
        Assert.Equal(actual: oldText, expected: "1.5");
        Assert.Equal(actual: newText, expected: "1.75");
    }
    [Fact]
    public void FloatField_WholeNumberValue_StepsFractionally_TypedByClrTypeNotJsonSpelling() {
        // A double field holding a whole number serializes as the integer literal `8` (SerializeToNode renders 8f as
        // `8`), so keying on the JSON kind would take an integer step (8 + 0.5 -> 9). The CLR type is authoritative.
        var row = RowWithRatioLiteral(ratioJson: "8");

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 0.5f, error: out var error, fieldPath: "ratio", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: error);
        Assert.Equal(actual: oldText, expected: "8");
        Assert.Equal(actual: newText, expected: "8.5");
    }
    [Fact]
    public void FloatField_WholeNumberValue_FractionalStep_IsNotAnIntegerNoOp() {
        // The bug's second face: an integer step rounds 8 - 0.4 back to 8 and echoes a no-op as success. Floating-point
        // typing lands the real value.
        var row = RowWithRatioLiteral(ratioJson: "8");

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: -0.4f, error: out _, fieldPath: "ratio", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Equal(actual: oldText, expected: "8");
        Assert.Equal(actual: newText, expected: "7.6");
    }
    [Fact]
    public void IntegerField_LargeValue_StepsExactlyAboveFloatPrecision() {
        // 1e8 exceeds float's 2^24 integer-exact ceiling; adding the delta in float space would round 100000001 back
        // to 100000000 (a silent no-op). Integer arithmetic keeps the current value exact.
        var row = Row(count: 100_000_000L);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var error, fieldPath: "count", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: error);
        Assert.Equal(actual: oldText, expected: "100000000");
        Assert.Equal(actual: newText, expected: "100000001");
    }
    [Fact]
    public void IntegerField_OverflowingDelta_RefusesByName_NeverThrows() {
        var row = Row(count: 3L);

        // The call itself must NOT throw (the dispatcher catches nothing) — it returns a by-name refusal.
        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: 1e19f, error: out var error, fieldPath: "count", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "out of range");
        // Control: an in-range delta on the identical field steps cleanly, and the refused step submitted nothing.
        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 5f, error: out var controlError, fieldPath: "count", newText: out var newText, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: controlError);
        Assert.Equal(actual: newText, expected: "8");
    }
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(0.001f)]
    [Theory]
    public void BooleanField_TogglesOnAnyNonzeroDelta(float delta) {
        var row = Row(enabled: false);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: delta, error: out var error, fieldPath: "enabled", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: error);
        Assert.Equal(actual: oldText, expected: "false");
        Assert.Equal(actual: newText, expected: "true");
        Assert.True(condition: ((bool)row["enabled"]!));
    }
    [Fact]
    public void BooleanField_TogglesBackOnSecondStep() {
        var row = Row(enabled: true);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out _, fieldPath: "enabled", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Equal(actual: oldText, expected: "true");
        Assert.Equal(actual: newText, expected: "false");
    }
    [Fact]
    public void EnumField_PositiveDeltaCyclesForwardAndWraps() {
        var row = Row(mode: TestMode.Gamma);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var error, fieldPath: "mode", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: error);
        Assert.Equal(actual: oldText, expected: "Gamma");
        Assert.Equal(actual: newText, expected: "Alpha");
    }
    [Fact]
    public void EnumField_NegativeDeltaCyclesBackwardAndWraps() {
        var row = Row(mode: TestMode.Alpha);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: -1f, error: out _, fieldPath: "mode", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Equal(actual: oldText, expected: "Alpha");
        Assert.Equal(actual: newText, expected: "Gamma");
    }
    [Fact]
    public void EnumField_ZeroDelta_RefusesByName() {
        var row = Row(mode: TestMode.Beta);

        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: 0f, error: out var error, fieldPath: "mode", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "delta must be nonzero to cycle an enum");
        // Control: the identical field with a nonzero delta steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var controlError, fieldPath: "mode", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void NonEnumStringField_RefusesByName() {
        var row = Row(label: "fixed");

        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var error, fieldPath: "label", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "not a steppable field");
        // Control: a genuinely steppable sibling field on the same row still steps.
        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var controlError, fieldPath: "count", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void NestedObjectField_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var error, fieldPath: "nested", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "not a steppable field");
        // Control: walking INTO the nested object to its own leaf steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var controlError, fieldPath: "nested.count", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: controlError);
        Assert.Equal(actual: oldText, expected: "7");
        Assert.Equal(actual: newText, expected: "8");
    }
    [Fact]
    public void IndexedArrayElementField_Steps() {
        var row = Row(items: [10L, 20L, 30L]);

        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 5f, error: out var error, fieldPath: "items[1].count", newText: out var newText, oldText: out var oldText, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: error);
        Assert.Equal(actual: oldText, expected: "20");
        Assert.Equal(actual: newText, expected: "25");
        // The untouched neighbors are unaffected.
        Assert.Equal(expected: 10L, actual: ((long)row["items"]![0]!["count"]!));
        Assert.Equal(expected: 30L, actual: ((long)row["items"]![2]!["count"]!));
    }
    [Fact]
    public void IndexedArrayElement_OutOfRange_RefusesByName() {
        var row = Row(items: [10L, 20L]);

        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var error, fieldPath: "items[5].count", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "out of range");
        // Control: an in-range index on the identical array steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var controlError, fieldPath: "items[1].count", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void UnknownMember_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var error, fieldPath: "notAField", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "unknown or empty member");
        // Control: the correctly spelled sibling steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var controlError, fieldPath: "count", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: controlError);
    }
    [Fact]
    public void EmptyPath_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var error, fieldPath: "", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "empty path");
    }
    [Fact]
    public void NonFiniteDelta_RefusesByName() {
        var row = Row();

        Assert.False(condition: WorldRowFieldStepper.TryStep(delta: float.NaN, error: out var error, fieldPath: "count", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Contains(actualString: error, comparisonType: StringComparison.Ordinal, expectedSubstring: "finite");
        // Control: a finite delta on the identical field steps cleanly.
        Assert.True(condition: WorldRowFieldStepper.TryStep(delta: 1f, error: out var controlError, fieldPath: "count", newText: out _, oldText: out _, root: row, rowType: typeof(TestRow)));
        Assert.Null(@object: controlError);
    }
}
