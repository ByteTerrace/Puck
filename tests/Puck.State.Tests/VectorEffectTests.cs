using Xunit;

namespace Puck.State.Tests;

public sealed class VectorEffectTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    private static StateSpace TestSpace(string name = "testSpace", int dimensions = 8) => new(
        Name: Name(name),
        Model: "test-model",
        Revision: "1",
        Dimensions: dimensions
    );

    private static StateVector MakeVector(params sbyte[] comps) {
        Assert.True(StateVector.TryCreate(components: comps, vector: out var vec, error: out var err), err);
        return vec!;
    }

    [Fact]
    public void VectorCopy_CompilesAndAppliesLiteral() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);

        var targetRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("k1"))]
        );

        var section = new StateSection(Spaces: [space], Rows: [targetRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var effect = new ActionEffect.SetState(
            State: "dest",
            Key: "k1",
            Vector: v1.ToBase64Url()
        );

        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "testRule", context: context, subject: "rule");
        Assert.Single(compiled);
        Assert.IsType<VectorCopyEffect>(compiled[0]);

        var copyEffect = (VectorCopyEffect)compiled[0];
        Assert.Equal("dest", copyEffect.Row);
        Assert.Equal("k1", copyEffect.Key);
        Assert.True(copyEffect.Source.IsConstant);

        var rows = new[] { targetRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "testRule", 1UL, 1UL);
        Assert.True(fired);

        Assert.True(host.Frame.TryStoredVector(rowOrdinal: 0, key: Name("k1"), out var stored));
        Assert.Equal(127, stored[0]);
    }

    [Fact]
    public void VectorMix_CompilesAndAppliesMixTransform() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(0, 127, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("a"), Vector: v1),
                new StateCell(Key: Name("b"), Vector: v2)
            ]
        );

        var destRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("out"))]
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Mix(
            Into: "dest[out]",
            Terms: [
                new VectorTerm("src[a]", 1),
                new VectorTerm("src[b]", 1)
            ]
        );

        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "mixRule", context: context, subject: "rule");
        Assert.Single(compiled);
        Assert.IsType<VectorMixEffect>(compiled[0]);

        var rows = new[] { srcRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "mixRule", 1UL, 1UL);
        Assert.True(fired);

        Assert.True(host.Frame.TryStoredVector(rowOrdinal: 1, key: Name("out"), out var mixed));
        Assert.Equal(mixed[0], mixed[1]);
        Assert.True(mixed[0] > 80);
    }

    [Fact]
    public void VectorMean_CompilesAndAppliesWithWhere() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(0, 127, 0, 0, 0, 0, 0, 0);
        var v3 = MakeVector(0, -127, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("candidates"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Vector: v1),
                new StateCell(Key: Name("c2"), Vector: v2),
                new StateCell(Key: Name("c3"), Vector: v3)
            ]
        );

        var whereRow = new StateRow(
            Name: Name("active"),
            Kind: CellKind.Bool,
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Value: 1L),
                new StateCell(Key: Name("c2"), Value: 1L),
                new StateCell(Key: Name("c3"), Value: 0L) // excluded by where
            ]
        );

        var destRow = new StateRow(
            Name: Name("meanOut"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("result"))]
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, whereRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Mean(
            From: "candidates",
            Into: "meanOut[result]",
            Where: "active"
        );

        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "meanRule", context: context, subject: "rule");
        Assert.Single(compiled);
        Assert.IsType<VectorMeanEffect>(compiled[0]);

        var rows = new[] { srcRow, whereRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "meanRule", 1UL, 1UL);
        Assert.True(fired);

        Assert.True(host.Frame.TryStoredVector(rowOrdinal: 2, key: Name("result"), out var meanVec));
        // c1 and c2 average
        Assert.Equal(meanVec[0], meanVec[1]);
        Assert.True(meanVec[0] > 80);
    }
}
