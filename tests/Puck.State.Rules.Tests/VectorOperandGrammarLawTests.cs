using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>One vector-operand grammar: <c>mix</c>, <c>nearest</c>, <c>remember</c>, and <c>setState.vector</c> read
/// their operands through the same lexer <c>dot()</c> and <c>similarity()</c> do.</summary>
public sealed class VectorOperandGrammarLawTests {
    private static CompiledRule Compile(ActionEffect effect) => RuleCompiler.Compile(
        context: VectorFixture.Context(),
        rule: RulesFixture.Rule(
            effects: [effect],
            name: "vectors"
        )
    );

    [Fact]
    public void AMixTermReadsThroughTheOneVectorLexer() {
        var compiled = Compile(effect: new ActionEffect.TransformState(Transform: new StateTransform.Mix(
            Into: "wide",
            Terms: [new VectorTerm(
                From: "bank[a]",
                Weight: 1
            )]
        )));
        var effect = Assert.IsType<VectorMixEffect>(@object: compiled.Effects[0]);
        var term = Assert.Single(collection: effect.Terms);

        Assert.False(condition: term.Source.IsConstant);
        Assert.NotEqual(
            actual: term.Source.RowOrdinal,
            expected: -1
        );
    }
    [Fact]
    public void ANearestQueryReadsThroughTheOneVectorLexer() {
        var compiled = Compile(effect: new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
            From: "bank",
            Into: "ranks",
            K: 2,
            Query: "wide"
        )));
        var effect = Assert.IsType<VectorNearestEffect>(@object: compiled.Effects[0]);

        Assert.False(condition: effect.Query.IsConstant);
        Assert.Equal(
            actual: effect.K,
            expected: 2
        );
        Assert.Equal(
            actual: effect.IntoKind,
            expected: CellKind.Int
        );
    }
    [Fact]
    public void ARememberSourceReadsThroughTheOneVectorLexer() {
        var compiled = Compile(effect: new ActionEffect.TransformState(Transform: new StateTransform.Remember(
            From: "wide",
            Into: "bank",
            Key: "a",
            UnlessWithin: "0.5"
        )));
        var effect = Assert.IsType<VectorRememberEffect>(@object: compiled.Effects[0]);

        Assert.False(condition: effect.Source.IsConstant);
        Assert.Equal(
            actual: effect.UnlessWithinQ16,
            expected: Puck.Maths.FixedQ4816.FromDouble(value: 0.5).Value
        );
    }
    [Fact]
    public void ASetStateVectorLiteralReadsThroughTheOneVectorLexer() {
        var components = new sbyte[16];

        components[0] = 127;

        Assert.True(condition: StateVector.TryCreate(
            components: components,
            error: out _,
            vector: out var authored
        ));

        var literal = authored!.ToBase64Url();
        var compiled = Compile(effect: new ActionEffect.SetState(
            State: "wide",
            Vector: literal
        ));
        var effect = Assert.IsType<VectorCopyEffect>(@object: compiled.Effects[0]);

        Assert.True(condition: effect.Source.IsConstant);
        Assert.Equal(
            actual: effect.Dimensions,
            expected: 16
        );
    }
    [Fact]
    public void ASetStateVectorCopyAddressesItsSourceByOrdinal() {
        var compiled = Compile(effect: new ActionEffect.SetState(
            FromState: "bank",
            FromKey: "a",
            State: "wide"
        ));
        var effect = Assert.IsType<VectorCopyEffect>(@object: compiled.Effects[0]);

        Assert.False(condition: effect.Source.IsConstant);
        Assert.NotEqual(
            actual: effect.Source.RowOrdinal,
            expected: -1
        );
        Assert.True(condition: effect.Source.Key.IsValid);
    }
}
