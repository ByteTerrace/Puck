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
    // A literal evaluates to itself, arithmetic over literals evaluates exactly, and a division by zero reads zero
    // rather than throwing.
    [InlineData("0.5", 0.5f)]
    [InlineData("clamp(0.2 + 0.9, 0, 1)", 1f)]
    [InlineData("1 / 0", 0f)]
    [Theory]
    public void AnExpressionOverLiteralsEvaluatesExactly(string text, float expected) => Assert.Equal(
        actual: WorldLookLaneEvaluator.Evaluate(
            expression: ExpressionProgram.Parse(text: text),
            reads: ClientFixtures.StateReads(definition: Fixtures.BuildDocument())
        ),
        expected: expected
    );
    [Fact]
    public void ANullExpressionEvaluatesToZero() {
        var definition = Fixtures.BuildDocument();

        var value = WorldLookLaneEvaluator.Evaluate(
            expression: null,
            reads: ClientFixtures.StateReads(definition: definition)
        );

        Assert.Equal(
            actual: value,
            expected: 0f
        );
    }
    [Fact]
    public void AnUnsupportedTokenReadsZeroRatherThanThrowing() {
        // popCount is state-only vocabulary (bitboards) — outside the restricted arithmetic subset this
        // presentation-layer evaluator supports.
        var definition = Fixtures.BuildDocument();
        var expression = new ExpressionProgram(Instructions: [
            Instruction.Constant(value: 7m),
            Instruction.Of(operation: ExpressionOp.SetBitCount),
        ]);

        var value = WorldLookLaneEvaluator.Evaluate(
            expression: expression,
            reads: ClientFixtures.StateReads(definition: definition)
        );

        Assert.Equal(
            actual: value,
            expected: 0f
        );
    }
    [Fact]
    public void FourAnonymousLanesPreserveInteriorGapsAndTheFourthValue() {
        ExpressionProgram?[] lanes = [ExpressionProgram.Parse(text: "0.25"), null, ExpressionProgram.Parse(text: "0.75"), ExpressionProgram.Parse(text: "1")];

        Assert.Equal(
            new System.Numerics.Vector4(
                w: 1f,
                x: 0.25f,
                y: 0f,
                z: 0.75f
            ),
            WorldLookLaneEvaluator.EvaluateLanes(
                expressions: lanes,
                reads: ClientFixtures.StateReads(definition: Fixtures.BuildDocument())
            )
        );
        var options = new System.Text.Json.JsonSerializerOptions();

        options.Converters.Add(item: new WorldRenderLanesConverter());
        var encoded = System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<ExpressionProgram?>>(
            options: options,
            value: lanes
        );
        var decoded = System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<ExpressionProgram?>>(
            json: encoded,
            options: options
        );

        Assert.Equal(
            4,
            decoded!.Count
        );
        Assert.Null(@object: decoded[1]);
        var trimmed = System.Text.Json.JsonSerializer.Serialize<IReadOnlyList<ExpressionProgram?>>(
            [lanes[0], null, null, null],
            options
        );
        using var json = System.Text.Json.JsonDocument.Parse(trimmed);

        Assert.Equal(
            1,
            json.RootElement.GetArrayLength()
        );
        Assert.Throws<System.Text.Json.JsonException>(testCode: () =>
            System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<ExpressionProgram?>>(
            json: "[null,null,null,null,null]",
            options: options
        ));
    }
    [InlineData("clamp(0.5, 1, 0)")]
    [InlineData("sign((9999999999999999999999999999 * 9999999999999999999999999999) - (9999999999999999999999999999 * 9999999999999999999999999999))")]
    [Theory]
    public void InvalidArithmeticReadsZero(string expression) =>
        Assert.Equal(
            0f,
            WorldLookLaneEvaluator.Evaluate(
                expression: ExpressionProgram.Parse(text: expression),
                reads: ClientFixtures.StateReads(definition: Fixtures.BuildDocument())
            )
        );
    [Fact]
    public void LanesRoundTripThroughTheWorldSourceGeneratedContext() {
        var document = Fixtures.BuildDocument() with {
            LooksRaw = new(Rows: [new(
                "probe",
                new WorldLookSource.Catalog(Index: 0),
                1f,
                WorldLookMotion.Default with { Lanes = [ExpressionProgram.Parse(text: "0.6"), null, ExpressionProgram.Parse(text: "1")] }
            )]),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            document,
            WorldJsonContext.Default.WorldDefinition
        );
        var restored = System.Text.Json.JsonSerializer.Deserialize(
            json: json,
            jsonTypeInfo: WorldJsonContext.Default.WorldDefinition
        )!;

        Assert.Equal(
            new System.Numerics.Vector4(
                w: 0f,
                x: 0.6f,
                y: 0f,
                z: 1f
            ),
            WorldLookLaneEvaluator.EvaluateLanes(
                expressions: restored.Looks[0].Motion.Lanes,
                reads: ClientFixtures.StateReads(definition: restored)
            )
        );
    }
}
