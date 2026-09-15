using Xunit;

namespace Puck.State.Tests;

public sealed class VectorRefusalTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    private static StateSpace TestSpace(string name = "testSpace", int dimensions = 8) => new(
        Name: Name(name),
        Model: "test-model",
        Revision: "1",
        Dimensions: dimensions
    );

    [Fact]
    public void VectorEffectNotAdmitted_AddStateOnVectorRow_Throws() {
        var space = TestSpace();
        var vecRow = new StateRow(Name: Name("vRow"), Kind: CellKind.Vector, Space: "testSpace", Capacity: 4, Cells: []);
        var section = new StateSection(Spaces: [space], Rows: [vecRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var effect = new ActionEffect.AddState(State: "vRow", Key: "k1", Value: 10m);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileEffects(effects: [effect], ruleName: "r1", context: context, subject: "rule")
        );
        Assert.Equal(RuleRefusal.VectorEffectNotAdmitted, ex.Refusal);
    }

    [Fact]
    public void VectorMixTerms_ExcessTerms_Throws() {
        var space = TestSpace();
        var vecRow = new StateRow(Name: Name("vRow"), Kind: CellKind.Vector, Space: "testSpace", Capacity: 4, Cells: []);
        var section = new StateSection(Spaces: [space], Rows: [vecRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        // 9 terms (max is 8)
        var terms = Enumerable.Range(0, 9).Select(i => new VectorTerm("vRow[k1]", 1)).ToArray();
        var transform = new StateTransform.Mix(Into: "vRow[out]", Terms: terms);
        var effect = new ActionEffect.TransformState(Transform: transform);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileEffects(effects: [effect], ruleName: "r2", context: context, subject: "rule")
        );
        Assert.Equal(RuleRefusal.VectorMixTerms, ex.Refusal);
    }

    [Fact]
    public void VectorFilterShape_WhereRowNotBool_Throws() {
        var space = TestSpace();
        var vecRow = new StateRow(Name: Name("vRow"), Kind: CellKind.Vector, Space: "testSpace", Capacity: 4, Cells: []);
        var intRow = new StateRow(Name: Name("notBool"), Kind: CellKind.Int, Capacity: 4, Cells: []);
        var section = new StateSection(Spaces: [space], Rows: [vecRow, intRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Mean(From: "vRow", Into: "vRow[out]", Where: "notBool");
        var effect = new ActionEffect.TransformState(Transform: transform);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileEffects(effects: [effect], ruleName: "r3", context: context, subject: "rule")
        );
        Assert.Equal(RuleRefusal.VectorFilterShape, ex.Refusal);
    }

    [Fact]
    public void VectorNearestShape_InvalidK_Throws() {
        var space = TestSpace();
        var vecRow = new StateRow(Name: Name("vRow"), Kind: CellKind.Vector, Space: "testSpace", Capacity: 4, Cells: []);
        var destRow = new StateRow(Name: Name("ranks"), Kind: CellKind.Int, Capacity: 4, Cells: []);
        var section = new StateSection(Spaces: [space], Rows: [vecRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        // K = 5 exceeds capacity = 4
        var transform = new StateTransform.Nearest(From: "vRow", Query: "vRow[q]", Into: "ranks", K: 5);
        var effect = new ActionEffect.TransformState(Transform: transform);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileEffects(effects: [effect], ruleName: "r4", context: context, subject: "rule")
        );
        Assert.Equal(RuleRefusal.VectorNearestShape, ex.Refusal);
    }

    [Fact]
    public void VectorRememberShape_UnlessWithinOutOfRange_Throws() {
        var space = TestSpace();
        var vecRow = new StateRow(Name: Name("vRow"), Kind: CellKind.Vector, Space: "testSpace", Capacity: 4, Cells: []);
        var section = new StateSection(Spaces: [space], Rows: [vecRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        // unlessWithin 1.5 > 1.0
        var transform = new StateTransform.Remember(Into: "vRow", Key: "newK", From: "vRow[k1]", UnlessWithin: "1.5");
        var effect = new ActionEffect.TransformState(Transform: transform);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileEffects(effects: [effect], ruleName: "r5", context: context, subject: "rule")
        );
        Assert.Equal(RuleRefusal.VectorRememberShape, ex.Refusal);
    }

    [Fact]
    public void VectorNearest_MalformedExclude_ThrowsVectorExcludeKey() {
        var space = TestSpace();
        var vecRow = new StateRow(Name: Name("vRow"), Kind: CellKind.Vector, Space: "testSpace", Capacity: 4, Cells: []);
        var destRow = new StateRow(Name: Name("ranks"), Kind: CellKind.Int, Capacity: 4, Cells: []);
        var section = new StateSection(Spaces: [space], Rows: [vecRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var transform = new StateTransform.Nearest(From: "vRow", Query: "vRow[q]", Into: "ranks", K: 1, Exclude: "bad:key");
        var effect = new ActionEffect.TransformState(Transform: transform);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileEffects(effects: [effect], ruleName: "rExclude", context: context, subject: "rule")
        );
        Assert.Equal(RuleRefusal.VectorExcludeKey, ex.Refusal);
    }

    [Fact]
    public void VectorNearest_MalformedExclude_FailsEvaluation() {
        var space = TestSpace();
        var vecRow = new StateRow(Name: Name("vRow"), Kind: CellKind.Vector, Space: "testSpace", Capacity: 4, Cells: []);
        var destRow = new StateRow(Name: Name("ranks"), Kind: CellKind.Int, Capacity: 4, Cells: []);
        var section = new StateSection(Spaces: [space], Rows: [vecRow, destRow]);
        var catalog = StateCatalog.Compile(section: section);

        Assert.True(StateVector.TryCreate(components: [127, 0, 0, 0, 0, 0, 0, 0], vector: out var queryVec, error: out var err), err);
        var effect = new VectorNearestEffect(
            intoRowOrdinal: 1,
            intoRowName: "ranks",
            intoHandle: default,
            intoKind: CellKind.Int,
            isIntoSlot: false,
            fromRowOrdinal: 0,
            fromRowName: "vRow",
            fromHandle: default,
            dimensions: 8,
            fromCapacity: 4,
            query: new CompiledVectorOperand(constant: queryVec!, space: space),
            k: 1,
            threshold: null,
            farthest: false,
            whereRowOrdinal: null,
            whereRowName: null,
            whereHandle: default,
            excludeKey: "malformed:key",
            excludeKeyFrom: null,
            excludeCellKey: default,
            describe: "nearest vRow -> ranks"
        );

        var rows = new[] { vecRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        var evaluator = new RuleEvaluator(host: host);

        var fired = evaluator.FireEffects([effect], "testRule", 1UL, 1UL);
        Assert.False(fired);
    }

    [Fact]
    public void VectorMixZero_OppositeVectors_FailsEvaluation() {
        var space = TestSpace();
        var v1 = StateVector.TryCreate(components: [127, 0, 0, 0, 0, 0, 0, 0], vector: out var vec1, error: out var err1) ? vec1! : throw new Exception(err1);
        var v2 = StateVector.TryCreate(components: [-127, 0, 0, 0, 0, 0, 0, 0], vector: out var vec2, error: out var err2) ? vec2! : throw new Exception(err2);

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
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rMixZero", context: context, subject: "rule");

        var rows = new[] { srcRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "rMixZero", 1UL, 1UL);
        Assert.False(fired);
        Assert.True(host.Refusals > 0);
    }

    [Fact]
    public void VectorMeanEmpty_EmptyTable_FailsEvaluation() {
        var space = TestSpace();
        var srcRow = new StateRow(
            Name: Name("src"),
            Kind: CellKind.Vector,
            Space: "testSpace",
            Capacity: 4,
            Cells: []
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

        var transform = new StateTransform.Mean(
            From: "src",
            Into: "dest[out]"
        );
        var effect = new ActionEffect.TransformState(Transform: transform);
        var compiled = RuleCompiler.CompileEffects(effects: [effect], ruleName: "rMeanEmpty", context: context, subject: "rule");

        var rows = new[] { srcRow, destRow };
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        var evaluator = new RuleEvaluator(host: host);
        var fired = evaluator.FireEffects(compiled, "rMeanEmpty", 1UL, 1UL);
        Assert.False(fired);
        Assert.True(host.Refusals > 0);
    }
}


