using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <see cref="WorldLookLaneEvaluator.Evaluate"/> reads a render lane expression —
/// literal or state-backed — into an anonymous component of
/// <c>Puck.SignedDistance.DynamicTransform.Lanes</c>. A plain numeric literal (the common authored case) reads
/// back exactly; a null expression, an unsupported token, or a malformed stack reads 0 — never throws on this
/// per-frame render path.
/// </summary>
public sealed class WorldLookLaneEvaluatorLawTests {
    [Theory]
    [InlineData("clamp(0.5, 1, 0)")]
    [InlineData("sign((9999999999999999999999999999 * 9999999999999999999999999999) - (9999999999999999999999999999 * 9999999999999999999999999999))")]
    public void InvalidArithmeticReadsZero(string expression) =>
        Assert.Equal(0f, WorldLookLaneEvaluator.Evaluate(ValueExpression.Parse(expression), Fixtures.BuildDocument(), 0, -1));

    [Fact]
    public void LanesRoundTripThroughTheWorldSourceGeneratedContext() {
        var document = Fixtures.BuildDocument() with {
            LooksRaw = new(Rows: [new("probe", new WorldLookSource.Catalog(0), 1f,
                WorldLookMotion.Default with { Lanes = [ValueExpression.Parse("0.6"), null, ValueExpression.Parse("1")] })]),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(document, WorldJsonContext.Default.WorldDefinition);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, WorldJsonContext.Default.WorldDefinition)!;
        Assert.Equal(new System.Numerics.Vector4(0.6f, 0f, 1f, 0f),
            WorldLookLaneEvaluator.EvaluateLanes(restored.Looks[0].Motion.Lanes, restored, 0, -1));
    }

    [Fact]
    public void FourAnonymousLanesPreserveInteriorGapsAndTheFourthValue() {
        ValueExpression?[] lanes = [ValueExpression.Parse("0.25"), null, ValueExpression.Parse("0.75"), ValueExpression.Parse("1")];
        Assert.Equal(new System.Numerics.Vector4(0.25f, 0f, 0.75f, 1f),
            WorldLookLaneEvaluator.EvaluateLanes(lanes, Fixtures.BuildDocument(), 0, -1));
        var options = new System.Text.Json.JsonSerializerOptions();
        options.Converters.Add(new WorldRenderLanesConverter());
        var encoded = System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<ValueExpression?>>(lanes, options);
        var decoded = System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<ValueExpression?>>(encoded, options);
        Assert.Equal(4, decoded!.Count);
        Assert.Null(decoded[1]);
        var trimmed = System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<ValueExpression?>>([lanes[0], null, null, null], options);
        using var json = System.Text.Json.JsonDocument.Parse(trimmed);
        Assert.Equal(1, json.RootElement.GetArrayLength());
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<ValueExpression?>>("[null,null,null,null,null]", options));
    }
    [Fact]
    public void ALiteralExpressionEvaluatesToItsOwnValue() {
        var definition = Fixtures.BuildDocument();
        var expression = ValueExpression.Parse(text: "0.5");

        var value = WorldLookLaneEvaluator.Evaluate(
            expression: expression,
            definition: definition,
            tick: 0ul,
            bodyIndex: -1
        );

        Assert.Equal(expected: 0.5f, actual: value);
    }
    [Fact]
    public void ANullExpressionEvaluatesToZero() {
        var definition = Fixtures.BuildDocument();

        var value = WorldLookLaneEvaluator.Evaluate(
            expression: null,
            definition: definition,
            tick: 0ul,
            bodyIndex: -1
        );

        Assert.Equal(expected: 0f, actual: value);
    }
    [Fact]
    public void ArithmeticOverLiteralsEvaluatesExactly() {
        var definition = Fixtures.BuildDocument();
        var expression = ValueExpression.Parse(text: "clamp(0.2 + 0.9, 0, 1)");

        var value = WorldLookLaneEvaluator.Evaluate(
            expression: expression,
            definition: definition,
            tick: 0ul,
            bodyIndex: -1
        );

        Assert.Equal(expected: 1f, actual: value);
    }
    [Fact]
    public void DivisionByZeroReadsZeroRatherThanThrowing() {
        var definition = Fixtures.BuildDocument();
        var expression = ValueExpression.Parse(text: "1 / 0");

        var value = WorldLookLaneEvaluator.Evaluate(
            expression: expression,
            definition: definition,
            tick: 0ul,
            bodyIndex: -1
        );

        Assert.Equal(expected: 0f, actual: value);
    }
    [Fact]
    public void AnUnsupportedTokenReadsZeroRatherThanThrowing() {
        // popCount is state-only vocabulary (bitboards) — outside the restricted arithmetic subset this
        // presentation-layer evaluator supports.
        var definition = Fixtures.BuildDocument();
        var expression = new ValueExpression(Tokens: [
            new ValueToken.Constant(Value: 7m),
            new ValueToken.PopCount(),
        ]);

        var value = WorldLookLaneEvaluator.Evaluate(
            expression: expression,
            definition: definition,
            tick: 0ul,
            bodyIndex: -1
        );

        Assert.Equal(expected: 0f, actual: value);
    }
}
