using Xunit;

namespace Puck.World.Client.Tests;

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
    // A bodyState lane reads the slot as the wearing body's (or a named body's) latest snapshot entry carried it, a
    // delivery that moves it refreshes the read, a register file of the wrong width is ignored, and a lane naming a
    // slot the world does not declare is refused.
    [Fact]
    public void ABodyStateLaneReadsTheDeliveredSlot_ControlAnUndeclaredSlotIsRefused() {
        const long One = 65536L;
        var baseDocument = Fixtures.BuildDocument();
        var definition = baseDocument with {
            StateRaw = new WorldStateSection(
                Body: [new ActionStateSlot(Name: "fuel", Kind: Puck.Physics.Motion.ActionStateKind.Counter)],
                Identity: [new ActionStateSlot(Name: "cooldown", Kind: Puck.Physics.Motion.ActionStateKind.Timer)],
                World: baseDocument.State
            ),
        };
        var view = new WorldDocumentStateView(definition: () => definition);
        var mirror = new WorldStateMirror(view: view);
        var reads = new WorldStateLease();
        var lane = ExpressionProgram.Parse(text: "bodyState($body, fuel) + bodyState(body, 0, cooldown)");
        var moved = new int[WorldDocumentStateView.MaxMovedBodyState];

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );
        reads.Bind(
            bodyIndex: 1,
            mirror: mirror
        );
        Assert.Equal(
            actual: WorldLookLaneEvaluator.Evaluate(
                expression: lane,
                reads: reads
            ),
            expected: 0f
        );

        view.ApplyBodyState(
            body: 1,
            values: [(3L * One), 0L]
        );
        view.ApplyBodyState(
            body: 0,
            values: [0L, (One / 2L)]
        );

        var count = view.TakeMovedBodyState(moved: moved);

        Assert.Equal(
            actual: count,
            expected: 2
        );
        mirror.RefreshRows(ordinals: moved.AsSpan(
            length: count,
            start: 0
        ));
        mirror.Apply(fraction: 1f);
        Assert.Equal(
            actual: WorldLookLaneEvaluator.Evaluate(
                expression: lane,
                reads: reads
            ),
            expected: 3.5f
        );

        view.ApplyBodyState(
            body: 1,
            values: [One]
        );
        Assert.Equal(
            actual: view.TakeMovedBodyState(moved: moved),
            expected: 0
        );

        WorldDefinition Wearing(string text) => definition with {
            LooksRaw = new(Rows: [new(
                "probe",
                new WorldLookSource.Catalog(Index: 0),
                1f,
                WorldLookMotion.Default with { Lanes = [ExpressionProgram.Parse(text: text)] }
            )]),
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Wearing(text: "bodyState($body, fuel)"),
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Wearing(text: "bodyState($body, missing)"),
            reason: out var reason
        ));
        Assert.Contains(
            expectedSubstring: "bodyState($body, <slot>)",
            actualString: reason,
            comparisonType: StringComparison.Ordinal
        );
    }
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
