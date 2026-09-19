using System.Reflection;

using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="CellValue"/> carries exactly one case per <see cref="CellKind"/>, hands
/// each payload back through <see cref="CellValue.TryGetValue{T}"/> and its typed accessor, throws on every other
/// accessor, and survives the <see cref="StateCell"/> round trip for each kind under the row's own wire
/// members.</summary>
public sealed class CellValueLawTests {
    private static readonly sbyte[] Components = [0, 0, 0, 127, 0, 0, 0, 0];

    private static CellValue Sample(CellKind kind) => kind switch {
        CellKind.Int => CellValue.Int(value: -17L),
        CellKind.Fixed => CellValue.Fixed(rawBits: 0x0001_8000L),
        CellKind.Bool => CellValue.Bool(value: true),
        CellKind.Text => CellValue.Text(value: "ready"),
        CellKind.Vector => CellValue.Vector(components: Components),
        _ => throw new InvalidOperationException(message: $"Unknown cell kind '{kind}'."),
    };

    [Fact]
    public void EveryCellKindHasACaseConstructorAndReadsBackAsThatKind() {
        var kinds = Enum.GetValues<CellKind>();

        foreach (var kind in kinds) {
            var factory = typeof(CellValue).GetMethod(
                name: kind.ToString(),
                bindingAttr: BindingFlags.Public | BindingFlags.Static
            );

            Assert.True(
                condition: (factory is not null),
                userMessage: $"CellValue declares no case constructor for CellKind.{kind}."
            );
            Assert.Equal(
                expected: kind,
                actual: Sample(kind: kind).Kind
            );
            Assert.True(condition: Sample(kind: kind).HasValue);
        }
    }
    [Fact]
    public void TheDefaultCarrierHoldsNoCaseAndRefusesEveryRead() {
        var value = default(CellValue);

        Assert.False(condition: value.HasValue);
        Assert.Throws<InvalidOperationException>(testCode: () => value.Kind);
        Assert.Throws<InvalidOperationException>(testCode: () => value.AsInt);
        Assert.Throws<InvalidOperationException>(testCode: () => value.AsText);
        Assert.False(condition: value.TryGetValue<long>(value: out _));
    }
    [Fact]
    public void ATypedAccessorThrowsOnEveryOtherCase() {
        Assert.Equal(
            expected: -17L,
            actual: CellValue.Int(value: -17L).AsInt
        );
        Assert.Throws<InvalidOperationException>(testCode: () => CellValue.Int(value: 1L).AsFixed);
        Assert.Throws<InvalidOperationException>(testCode: () => CellValue.Int(value: 1L).AsBool);
        Assert.Throws<InvalidOperationException>(testCode: () => CellValue.Int(value: 1L).AsText);
        Assert.Throws<InvalidOperationException>(testCode: () => CellValue.Int(value: 1L).AsVector);
        Assert.Throws<InvalidOperationException>(testCode: () => CellValue.Text(value: "x").AsInt);
        Assert.Throws<InvalidOperationException>(testCode: () => CellValue.Vector(components: Components).AsBool);
    }
    [Fact]
    public void TryGetValueAnswersForThePayloadTypeAndRefusesEveryOther() {
        Assert.True(condition: CellValue.Int(value: 5L).TryGetValue<long>(value: out var number));
        Assert.Equal(
            actual: number,
            expected: 5L
        );
        Assert.True(condition: CellValue.Fixed(rawBits: 9L).TryGetValue<long>(value: out var bits));
        Assert.Equal(
            actual: bits,
            expected: 9L
        );
        Assert.True(condition: CellValue.Bool(value: true).TryGetValue<bool>(value: out var flag));
        Assert.True(condition: flag);
        Assert.True(condition: CellValue.Text(value: "note").TryGetValue<string>(value: out var text));
        Assert.Equal(
            actual: text,
            expected: "note"
        );
        Assert.True(condition: CellValue.Vector(components: Components).TryGetValue<ReadOnlyMemory<sbyte>>(value: out var vector));
        Assert.True(condition: vector.Span.SequenceEqual(other: Components));

        Assert.False(condition: CellValue.Int(value: 5L).TryGetValue<bool>(value: out _));
        Assert.False(condition: CellValue.Text(value: "note").TryGetValue<long>(value: out _));
    }
    [Fact]
    public void EqualityComparesTheCaseAndThePayload() {
        Assert.Equal(
            expected: CellValue.Int(value: 3L),
            actual: CellValue.Int(value: 3L)
        );
        Assert.NotEqual(
            expected: CellValue.Int(value: 3L),
            actual: CellValue.Fixed(rawBits: 3L)
        );
        Assert.NotEqual(
            expected: CellValue.Int(value: 3L),
            actual: CellValue.Int(value: 4L)
        );
        Assert.Equal(
            expected: CellValue.Text(value: "a"),
            actual: CellValue.Text(value: "a")
        );
        Assert.Equal(
            expected: CellValue.Vector(components: Components),
            actual: CellValue.Vector(components: Components.ToArray())
        );
        Assert.Equal(
            actual: default(CellValue),
            expected: default(CellValue)
        );
    }
    [InlineData(CellKind.Int)]
    [InlineData(CellKind.Fixed)]
    [InlineData(CellKind.Bool)]
    [InlineData(CellKind.Text)]
    [InlineData(CellKind.Vector)]
    [Theory]
    public void TheCellRoundTripIsTheIdentityPerKind(CellKind kind) {
        var expected = Sample(kind: kind);
        var cell = new StateCell(Key: StateRow.SlotKey, Value: expected);

        Assert.Equal(
            expected: expected,
            actual: cell.Value
        );
    }
    [InlineData(CellKind.Int)]
    [InlineData(CellKind.Fixed)]
    [InlineData(CellKind.Bool)]
    [InlineData(CellKind.Text)]
    [InlineData(CellKind.Vector)]
    [Theory]
    public void TheJsonRoundTripPreservesEachKindsValue(CellKind kind) {
        var expected = Sample(kind: kind);
        var row = new StateRow(
            Name: CellName.Parse(candidate: "slot"),
            Kind: kind,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: expected)]
        );
        var options = new System.Text.Json.JsonSerializerOptions { Converters = { new StateRowJsonConverter() } };
        var text = System.Text.Json.JsonSerializer.Serialize(
            options: options,
            value: row
        );
        var restored = System.Text.Json.JsonSerializer.Deserialize<StateRow>(
            json: text,
            options: options
        );

        Assert.NotNull(@object: restored);
        Assert.Equal(
            expected: expected,
            actual: restored!.Cells![0].Value
        );
    }

    // The authored grammar, per kind: a decimal spelling for Fixed (never its raw bits), true/false for Bool, a
    // plain integer otherwise. Text and vector carry their own payloads, so a numeric token against either is
    // refused by name rather than guessed at. `Raw` hands back the one number the row's column stores.
    [Theory]
    [InlineData(CellKind.Int, "-17", -17L)]
    [InlineData(CellKind.Bool, "true", 1L)]
    [InlineData(CellKind.Bool, "false", 0L)]
    [InlineData(CellKind.Fixed, "1.5", 98304L)]
    public void ATokenParsesIntoTheCaseItsRowKindDeclares(CellKind kind, string token, long raw) {
        Assert.True(condition: CellValue.TryParse(
            kind: kind,
            reason: out var reason,
            token: token,
            value: out var value
        ), userMessage: reason);
        Assert.Equal(
            actual: value.Kind,
            expected: kind
        );
        Assert.Equal(
            actual: value.Raw,
            expected: raw
        );
    }
    [Theory]
    [InlineData(CellKind.Int, "1.5")]
    [InlineData(CellKind.Bool, "1")]
    [InlineData(CellKind.Fixed, "yes")]
    [InlineData(CellKind.Text, "anything")]
    [InlineData(CellKind.Vector, "anything")]
    public void ATokenTheRowKindDoesNotSpellIsRefusedByName(CellKind kind, string token) {
        Assert.False(condition: CellValue.TryParse(
            kind: kind,
            reason: out var reason,
            token: token,
            value: out var value
        ));
        Assert.False(condition: value.HasValue);
        Assert.NotEqual(
            actual: reason,
            expected: string.Empty
        );
    }
}
