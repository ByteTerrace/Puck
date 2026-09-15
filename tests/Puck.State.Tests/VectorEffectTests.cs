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

    [Fact]
    public void VectorNearest_IntoInt_WritesDotScores() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(90, 90, 0, 0, 0, 0, 0, 0);
        var v3 = MakeVector(0, 127, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Vector: v1),
                new StateCell(Key: Name("c2"), Vector: v2),
                new StateCell(Key: Name("c3"), Vector: v3)
            ]
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v1)]
        );
        var destRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: []
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, qRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest", K: 3);
        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rNearestInt", context: context, subject: "rule");

        var rows = new[] { srcRow, qRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "rNearestInt", 1UL, 1UL);
        Assert.True(fired);

        var outRow = host.GetRow(2);
        Assert.NotNull(outRow.Cells);
        Assert.Equal(3, outRow.Cells.Count);
        Assert.Equal("c1", outRow.Cells[0].Key.Value);
        Assert.Equal(16129L, outRow.Cells[0].Value);
        Assert.Equal("c2", outRow.Cells[1].Key.Value);
        Assert.Equal(11430L, outRow.Cells[1].Value);
        Assert.Equal("c3", outRow.Cells[2].Key.Value);
        Assert.Equal(0L, outRow.Cells[2].Value);
    }

    [Fact]
    public void VectorNearest_IntoFixed_WritesCosineScores() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(90, 90, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Vector: v1),
                new StateCell(Key: Name("c2"), Vector: v2)
            ]
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v1)]
        );
        var destRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Fixed,
            Capacity: 4,
            Cells: []
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, qRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest", K: 2);
        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rNearestFixed", context: context, subject: "rule");

        var rows = new[] { srcRow, qRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "rNearestFixed", 1UL, 1UL);
        Assert.True(fired);

        var outRow = host.GetRow(2);
        Assert.NotNull(outRow.Cells);
        Assert.Equal(2, outRow.Cells.Count);
        Assert.Equal("c1", outRow.Cells[0].Key.Value);
        Assert.Equal(65536L, outRow.Cells[0].Value); // 1.0 in Q48.16
        Assert.Equal("c2", outRow.Cells[1].Key.Value);
        Assert.True(outRow.Cells[1].Value > 40000L && outRow.Cells[1].Value < 50000L); // ~0.707
    }

    [Fact]
    public void VectorNearest_IntoTextSlot_WritesTopKey() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(0, 127, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Vector: v1),
                new StateCell(Key: Name("c2"), Vector: v2)
            ]
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v1)]
        );
        var destRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Text,
            Domain: StateDomain.Slot.Instance,
            Cells: []
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, qRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest", K: 1);
        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rNearestText", context: context, subject: "rule");

        var rows = new[] { srcRow, qRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "rNearestText", 1UL, 1UL);
        Assert.True(fired);

        var outRow = host.GetRow(2);
        Assert.NotNull(outRow.Cells);
        Assert.Single(outRow.Cells);
        Assert.Equal("c1", outRow.Cells[0].Text);
    }

    [Fact]
    public void VectorNearest_Ties_BrokenLexicographically() {
        var space = TestSpace();
        var v = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("k_zebra"), Vector: v),
                new StateCell(Key: Name("k_apple"), Vector: v)
            ]
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v)]
        );
        var destRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: []
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, qRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest", K: 2);
        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rTies", context: context, subject: "rule");

        var rows = new[] { srcRow, qRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "rTies", 1UL, 1UL);
        Assert.True(fired);

        var outRow = host.GetRow(2);
        Assert.NotNull(outRow.Cells);
        Assert.Equal(2, outRow.Cells.Count);
        Assert.Equal("k_apple", outRow.Cells[0].Key.Value);
        Assert.Equal("k_zebra", outRow.Cells[1].Key.Value);
    }

    [Fact]
    public void VectorNearest_ThresholdBothDirections() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(90, 90, 0, 0, 0, 0, 0, 0);
        var v3 = MakeVector(0, 127, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Vector: v1),
                new StateCell(Key: Name("c2"), Vector: v2),
                new StateCell(Key: Name("c3"), Vector: v3)
            ]
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v1)]
        );
        var destRow1 = new StateRow(Name: Name("dest1"), Kind: CellKind.Int, Capacity: 4, Cells: []);
        var destRow2 = new StateRow(Name: Name("dest2"), Kind: CellKind.Int, Capacity: 4, Cells: []);

        var section = new StateSection(Spaces: [space], Rows: [srcRow, qRow, destRow1, destRow2]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var normalTransform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest1", K: 3, Threshold: "12000");
        var farthestTransform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest2", K: 3, Threshold: "12000", Farthest: true);

        var compiledNormal = RuleCompiler.CompileEffects(effects: [new ActionEffect.TransformState(Transform: normalTransform)], ruleName: "rNorm", context: context, subject: "rule");
        var compiledFarthest = RuleCompiler.CompileEffects(effects: [new ActionEffect.TransformState(Transform: farthestTransform)], ruleName: "rFar", context: context, subject: "rule");

        var rows = new[] { srcRow, qRow, destRow1, destRow2 };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        Assert.True(evaluator.FireEffects(compiledNormal, "rNorm", 1UL, 1UL));
        var out1 = host.GetRow(2);
        Assert.NotNull(out1.Cells);
        Assert.Single(out1.Cells);
        Assert.Equal("c1", out1.Cells[0].Key.Value);

        Assert.True(evaluator.FireEffects(compiledFarthest, "rFar", 1UL, 1UL));
        var out2 = host.GetRow(3);
        Assert.NotNull(out2.Cells);
        Assert.Equal(2, out2.Cells.Count);
        Assert.Equal("c3", out2.Cells[0].Key.Value);
        Assert.Equal("c2", out2.Cells[1].Key.Value);
    }

    [Fact]
    public void VectorNearest_WhereFilter() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(90, 90, 0, 0, 0, 0, 0, 0);
        var v3 = MakeVector(0, 127, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
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
            Name: Name("filter"),
            Kind: CellKind.Bool,
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Value: 1L),
                new StateCell(Key: Name("c2"), Value: 0L),
                new StateCell(Key: Name("c3"), Value: 1L)
            ]
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v1)]
        );
        var destRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: []
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, whereRow, qRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest", K: 3, Where: "filter");
        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rWhere", context: context, subject: "rule");

        var rows = new[] { srcRow, whereRow, qRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        Assert.True(evaluator.FireEffects(compiled, "rWhere", 1UL, 1UL));

        var outRow = host.GetRow(3);
        Assert.NotNull(outRow.Cells);
        Assert.Equal(2, outRow.Cells.Count);
        Assert.Equal("c1", outRow.Cells[0].Key.Value);
        Assert.Equal("c3", outRow.Cells[1].Key.Value);
    }

    [Fact]
    public void VectorNearest_ExcludeKey() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);
        var v2 = MakeVector(90, 90, 0, 0, 0, 0, 0, 0);

        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [
                new StateCell(Key: Name("c1"), Vector: v1),
                new StateCell(Key: Name("c2"), Vector: v2)
            ]
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v1)]
        );
        var destRow = new StateRow(
            Name: Name("dest"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: []
        );

        var section = new StateSection(Spaces: [space], Rows: [srcRow, qRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Nearest(From: "src", Query: "qRow[q]", Into: "dest", K: 2, Exclude: "c1");
        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rExclude", context: context, subject: "rule");

        var rows = new[] { srcRow, qRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        Assert.True(evaluator.FireEffects(compiled, "rExclude", 1UL, 1UL));

        var outRow = host.GetRow(2);
        Assert.NotNull(outRow.Cells);
        Assert.Single(outRow.Cells);
        Assert.Equal("c2", outRow.Cells[0].Key.Value);
    }

    [Fact]
    public void VectorNearest_FewerThanK_AndEmptySource() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);

        var srcFewer = new StateRow(
            Name: Name("srcFewer"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("c1"), Vector: v1)]
        );
        var srcEmpty = new StateRow(
            Name: Name("srcEmpty"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: []
        );
        var qRow = new StateRow(
            Name: Name("qRow"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 1,
            Cells: [new StateCell(Key: Name("q"), Vector: v1)]
        );
        var destFewer = new StateRow(Name: Name("destFewer"), Kind: CellKind.Int, Capacity: 8, Cells: []);
        var destEmpty = new StateRow(Name: Name("destEmpty"), Kind: CellKind.Int, Capacity: 8, Cells: []);

        var section = new StateSection(Spaces: [space], Rows: [srcFewer, srcEmpty, qRow, destFewer, destEmpty]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var tFewer = new StateTransform.Nearest(From: "srcFewer", Query: "qRow[q]", Into: "destFewer", K: 5);
        var tEmpty = new StateTransform.Nearest(From: "srcEmpty", Query: "qRow[q]", Into: "destEmpty", K: 5);

        var compFewer = RuleCompiler.CompileEffects(effects: [new ActionEffect.TransformState(Transform: tFewer)], ruleName: "rFewer", context: context, subject: "rule");
        var compEmpty = RuleCompiler.CompileEffects(effects: [new ActionEffect.TransformState(Transform: tEmpty)], ruleName: "rEmpty", context: context, subject: "rule");

        var rows = new[] { srcFewer, srcEmpty, qRow, destFewer, destEmpty };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);
        Assert.True(evaluator.FireEffects(compFewer, "rFewer", 1UL, 1UL));
        var outFewer = host.GetRow(3);
        Assert.NotNull(outFewer.Cells);
        Assert.Single(outFewer.Cells);

        Assert.True(evaluator.FireEffects(compEmpty, "rEmpty", 1UL, 1UL));
        var outEmpty = host.GetRow(4);
        Assert.NotNull(outEmpty.Cells);
        Assert.Empty(outEmpty.Cells);
    }

    [Fact]
    public void VectorRemember_SkipsNearDuplicate_AndIgnoresOwnKey() {
        var space = TestSpace();
        var v1 = MakeVector(127, 0, 0, 0, 0, 0, 0, 0);

        var historyRow = new StateRow(
            Name: Name("history"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("k1"), Vector: v1)]
        );
        var candRow = new StateRow(
            Name: Name("cand"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: [new StateCell(Key: Name("c"), Vector: v1)]
        );

        var section = new StateSection(Spaces: [space], Rows: [historyRow, candRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        // Near duplicate: tries to remember k2 which has cosine 1.0 >= 0.9 with k1.
        var tNear = new StateTransform.Remember(Into: "history", Key: "k2", From: "cand[c]", UnlessWithin: "0.9");
        var compNear = RuleCompiler.CompileEffects(effects: [new ActionEffect.TransformState(Transform: tNear)], ruleName: "rRemNear", context: context, subject: "rule");

        // Ignores own key: tries to remember k1 which has cosine 1.0 with itself, but should ignore own key k1.
        var tOwn = new StateTransform.Remember(Into: "history", Key: "k1", From: "cand[c]", UnlessWithin: "0.9");
        var compOwn = RuleCompiler.CompileEffects(effects: [new ActionEffect.TransformState(Transform: tOwn)], ruleName: "rRemOwn", context: context, subject: "rule");

        var rows = new[] { historyRow, candRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var innerHost = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var host = new CrossRowTestHost(innerHost, rows);

        var evaluator = new RuleEvaluator(host: host);

        // Near duplicate should be skipped (not added)
        Assert.True(evaluator.FireEffects(compNear, "rRemNear", 1UL, 1UL));
        var hRow = host.GetRow(0);
        Assert.NotNull(hRow.Cells);
        Assert.Single(hRow.Cells);
        Assert.Equal("k1", hRow.Cells[0].Key.Value);

        // Own key should be remembered
        Assert.True(evaluator.FireEffects(compOwn, "rRemOwn", 1UL, 1UL));
        hRow = host.GetRow(0);
        Assert.NotNull(hRow.Cells);
        Assert.Single(hRow.Cells);
        Assert.Equal("k1", hRow.Cells[0].Key.Value);
    }

    private sealed class CrossRowTestHost : IRuleHost {
        private readonly FrameHost m_inner;
        private readonly List<StateRow> m_rows;

        public CrossRowTestHost(FrameHost inner, IEnumerable<StateRow> rows) {
            m_inner = inner;
            m_rows = [.. rows];
        }

        public IReadOnlyList<StateRow> Rows => m_rows;
        public StateRow GetRow(int ordinal) => m_rows[ordinal];

        public ulong Tick => m_inner.Tick;
        public ulong EngineTick => m_inner.EngineTick;
        public StateStore Store => new RowStore(() => m_rows);
        public StateCatalog Catalog => m_inner.Catalog;
        public CompiledPatterns Patterns => m_inner.Patterns;
        public string? BoundEachKey => m_inner.BoundEachKey;
        public string? BoundTokenKey { get => m_inner.BoundTokenKey; set => m_inner.BoundTokenKey = value; }
        public string? BoundPreviousKey { get => m_inner.BoundPreviousKey; set => m_inner.BoundPreviousKey = value; }
        public bool TableKeyMissing { get => m_inner.TableKeyMissing; set => m_inner.TableKeyMissing = value; }
        public int BoundIndex(BoundKey key) => m_inner.BoundIndex(key);
        public long BindingValue(int ordinal) => m_inner.BindingValue(ordinal);
        public CompiledTable Table(int ordinal) => m_inner.Table(ordinal);
        public void ReportTableKeyMissing(string table, long key) => m_inner.ReportTableKeyMissing(table, key);
        public Span<long> BoardScratch(int cells) => m_inner.BoardScratch(cells);
        public Span<long> PatternWord => m_inner.PatternWord;

        public void BeginPreflight() => m_inner.BeginPreflight();
        public void EndPreflight() => m_inner.EndPreflight();
        public bool TryCommitPreflight(ulong tick, out string reason) => m_inner.TryCommitPreflight(tick, out reason);
        public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) =>
            m_inner.FireEffect(effect, ruleName, tick, stepTicks, preflight);
        public bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) =>
            m_inner.TryEvaluateOwn(rule, latch, tick, stepTicks, out applied);
        public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => m_inner.RefusalRecorded(in diagnostic);

        public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
            if (mutation is StateMutation.ApplyVector { Transform: ResolvedVectorTransform.Nearest nearest }) {
                return ApplyNearest(nearest, out reason);
            }
            if (mutation is StateMutation.ApplyVector { Transform: ResolvedVectorTransform.Remember remember }) {
                return ApplyRemember(remember, out reason);
            }
            if (mutation is StateMutation.UpsertCell upsert) {
                var rowOrdinal = m_rows.FindIndex(r => r.Name.Value == upsert.Row);
                if (rowOrdinal >= 0) {
                    var row = m_rows[rowOrdinal];
                    var cells = row.Cells is null ? new List<StateCell>() : row.Cells.ToList();
                    var idx = cells.FindIndex(c => c.Key.Value == upsert.Key);
                    var newCell = new StateCell(Key: CellName.Parse(upsert.Key), Value: upsert.Value, Text: upsert.Text, Vector: upsert.Vector);
                    if (idx >= 0) {
                        cells[idx] = newCell;
                    } else {
                        cells.Add(newCell);
                    }
                    m_rows[rowOrdinal] = row with { Cells = cells };
                }
            }
            return m_inner.TryApply(mutation, tick, preflight, out reason);
        }

        private bool ApplyNearest(ResolvedVectorTransform.Nearest nearest, out string reason) {
            ReadOnlySpan<sbyte> querySpan;
            if (nearest.QueryVector is not null) {
                querySpan = nearest.QueryVector.Components;
            } else if (nearest.QueryRowOrdinal.HasValue && nearest.QueryKey != default) {
                if (!Store.TryStoredVector(nearest.QueryRowOrdinal.Value, nearest.QueryKey, out querySpan)) {
                    reason = "query vector not found";
                    return false;
                }
            } else {
                reason = "invalid query";
                return false;
            }

            var fromRow = m_rows[nearest.FromRowOrdinal];
            var fromCells = fromRow.Cells ?? [];
            var candidateList = new List<NearestCandidate>(fromCells.Count);

            for (var i = 0; i < fromCells.Count; i++) {
                var cell = fromCells[i];
                if (!Store.TryStoredVector(nearest.FromRowOrdinal, cell.Key, out var cSpan)) {
                    continue;
                }
                var admitted = true;
                if (nearest.WhereRowOrdinal.HasValue) {
                    if (!Store.TryStored(nearest.WhereRowOrdinal.Value, cell.Key, out var boolVal, text: out _) || boolVal == 0L) {
                        admitted = false;
                    }
                }
                candidateList.Add(new NearestCandidate(cell.Key, cSpan.ToArray(), admitted));
            }

            var matches = new VectorTransforms.NearestMatch[nearest.K];
            var isFixedScore = (nearest.IntoKind is CellKind.Fixed or CellKind.Text);
            var count = VectorTransforms.SelectNearest(
                candidates: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidateList),
                query: querySpan,
                isFixedScore: isFixedScore,
                k: nearest.K,
                threshold: nearest.Threshold,
                excludeKey: nearest.Exclude,
                farthest: nearest.Farthest,
                results: matches
            );

            var intoRow = m_rows[nearest.TargetRowOrdinal];
            if (nearest.IntoKind == CellKind.Text) {
                var bestKey = count > 0 ? matches[0].Key.Value : string.Empty;
                m_rows[nearest.TargetRowOrdinal] = intoRow with {
                    Cells = [new StateCell(Key: StateRow.SlotKey, Text: bestKey)]
                };
            } else {
                var newCells = new List<StateCell>(count);
                for (var i = 0; i < count; i++) {
                    newCells.Add(new StateCell(Key: matches[i].Key, Value: matches[i].Score));
                }
                m_rows[nearest.TargetRowOrdinal] = intoRow with { Cells = newCells };
            }

            reason = string.Empty;
            return true;
        }

        private bool ApplyRemember(ResolvedVectorTransform.Remember remember, out string reason) {
            ReadOnlySpan<sbyte> vecSpan;
            StateVector? fromVec = null;
            if (remember.FromVector is not null) {
                fromVec = remember.FromVector;
                vecSpan = fromVec.Components;
            } else if (remember.FromRowOrdinal.HasValue && remember.FromKey != default) {
                var row = m_rows[remember.FromRowOrdinal.Value];
                var cell = row.Cells?.FirstOrDefault(c => c.Key == remember.FromKey);
                fromVec = cell?.Vector;
                if (fromVec is null) {
                    reason = "source vector not found";
                    return false;
                }
                vecSpan = fromVec.Components;
            } else {
                reason = "unknown source";
                return false;
            }

            var intoRow = m_rows[remember.IntoRowOrdinal];
            var existingCells = intoRow.Cells ?? [];
            var candidateList = new List<NearestCandidate>(existingCells.Count);
            for (var i = 0; i < existingCells.Count; i++) {
                var cell = existingCells[i];
                if (Store.TryStoredVector(remember.IntoRowOrdinal, cell.Key, out var cSpan)) {
                    candidateList.Add(new NearestCandidate(cell.Key, cSpan.ToArray(), true));
                }
            }

            if (!VectorTransforms.TryRemember(
                existingCells: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidateList),
                key: remember.Key,
                vector: vecSpan,
                unlessWithinQ16: remember.UnlessWithinQ16,
                matchingKey: out _
            )) {
                reason = string.Empty;
                return true;
            }

            var cells = existingCells.ToList();
            var existingIndex = cells.FindIndex(c => c.Key == remember.Key);
            if (existingIndex >= 0) {
                cells[existingIndex] = new StateCell(Key: remember.Key, Vector: fromVec);
            } else {
                if (intoRow.Capacity > 0 && cells.Count >= intoRow.Capacity) {
                    cells.RemoveAt(0);
                }
                cells.Add(new StateCell(Key: remember.Key, Vector: fromVec));
            }
            m_rows[remember.IntoRowOrdinal] = intoRow with { Cells = cells };
            reason = string.Empty;
            return true;
        }
    }
}

