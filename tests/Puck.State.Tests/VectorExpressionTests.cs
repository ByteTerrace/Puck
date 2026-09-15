using Puck.Maths;
using Xunit;

namespace Puck.State.Tests;

public sealed class VectorExpressionTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    private static StateSpace TestSpace(string name = "testSpace", int dimensions = 8) => new(
        Name: Name(name),
        Model: "test-model",
        Revision: "1",
        Dimensions: dimensions
    );

    private static StateRow VectorTable(string name, string spaceName, int capacity, params (string Key, StateVector Vector)[] entries) => new(
        Name: Name(name),
        Kind: CellKind.Vector,
        Space: spaceName,
        Capacity: capacity,
        Cells: entries.Select(e => new StateCell(Key: Name(e.Key), Vector: e.Vector)).ToArray()
    );

    private static StateRow IntTable(string name, int capacity, params (string Key, long Value)[] entries) => new(
        Name: Name(name),
        Kind: CellKind.Int,
        Capacity: capacity,
        Cells: entries.Select(e => new StateCell(Key: Name(e.Key), Value: e.Value)).ToArray()
    );

    private static StateVector MakeUnitVector(int dim, int nonZeroIndex, sbyte value = 127) {
        var comps = new sbyte[dim];
        comps[nonZeroIndex] = value;
        Assert.True(StateVector.TryCreate(comps, out var vec, out _));
        return vec!;
    }

    [Fact]
    public void Dot_EvaluatesExactIntegerDotProduct() {
        var space = TestSpace();
        var v1 = MakeUnitVector(8, 0, 127);
        var v2 = MakeUnitVector(8, 0, 127);
        var v3 = MakeUnitVector(8, 1, 127);

        var rows = new[] {
            VectorTable("vecs", "testSpace", 4, ("a", v1), ("b", v2), ("c", v3))
        };
        var section = new StateSection(Spaces: [space], Rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var expr = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Dot,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "b")
            )
        ]);

        var compiled = RuleCompiler.CompileExpression(expr, CellKind.Int, "testRule", "setState", context);
        Assert.Single(compiled);
        Assert.Equal(ExpressionOp.Operand, compiled[0].Operation);

        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        // Evaluate operand fact directly
        var fact = compiled[0].Operand!.Read(host);
        Assert.False(fact.IsAbsent);
        Assert.Equal(127L * 127L, fact.Value);

        // Dot with orthogonal vector
        var exprOrthogonal = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Dot,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "c")
            )
        ]);
        var compiledOrthogonal = RuleCompiler.CompileExpression(exprOrthogonal, CellKind.Int, "testRule", "setState", context);
        var factOrthogonal = compiledOrthogonal[0].Operand!.Read(host);
        Assert.False(factOrthogonal.IsAbsent);
        Assert.Equal(0L, factOrthogonal.Value);
    }

    [Fact]
    public void Similarity_EvaluatesExactCosineQ16() {
        var space = TestSpace();
        var v1 = MakeUnitVector(8, 0, 127);
        var v2 = MakeUnitVector(8, 0, -127);
        var v3 = MakeUnitVector(8, 1, 127);

        var rows = new[] {
            VectorTable("vecs", "testSpace", 4, ("a", v1), ("negA", v2), ("ortho", v3))
        };
        var section = new StateSection(Spaces: [space], Rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        // Self-similarity = 1.0 (65536 in Q48.16)
        var exprSelf = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Similarity,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "a")
            )
        ]);
        var compiledSelf = RuleCompiler.CompileExpression(exprSelf, CellKind.Fixed, "testRule", "setState", context);
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        var factSelf = compiledSelf[0].Operand!.Read(host);
        Assert.False(factSelf.IsAbsent);
        Assert.Equal(FixedQ4816.One.Value, factSelf.Value);

        // Opposite similarity = -1.0 (-65536 in Q48.16)
        var exprOpposite = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Similarity,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "negA")
            )
        ]);
        var compiledOpposite = RuleCompiler.CompileExpression(exprOpposite, CellKind.Fixed, "testRule", "setState", context);
        var factOpposite = compiledOpposite[0].Operand!.Read(host);
        Assert.False(factOpposite.IsAbsent);
        Assert.Equal(-FixedQ4816.One.Value, factOpposite.Value);

        // Orthogonal similarity = 0.0
        var exprOrtho = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Similarity,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "ortho")
            )
        ]);
        var compiledOrtho = RuleCompiler.CompileExpression(exprOrtho, CellKind.Fixed, "testRule", "setState", context);
        var factOrtho = compiledOrtho[0].Operand!.Read(host);
        Assert.False(factOrtho.IsAbsent);
        Assert.Equal(0L, factOrtho.Value);
    }

    [Fact]
    public void Identical_EvaluatesOneOrZero() {
        var space = TestSpace();
        var v1 = MakeUnitVector(8, 0, 127);
        var v2 = MakeUnitVector(8, 0, 127);
        var v3 = MakeUnitVector(8, 1, 127);

        var rows = new[] {
            VectorTable("vecs", "testSpace", 4, ("a", v1), ("b", v2), ("c", v3))
        };
        var section = new StateSection(Spaces: [space], Rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var exprEq = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Identical,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "b")
            )
        ]);
        var compiledEq = RuleCompiler.CompileExpression(exprEq, CellKind.Int, "testRule", "setState", context);
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        var factEq = compiledEq[0].Operand!.Read(host);
        Assert.Equal(1L, factEq.Value);

        var exprNeq = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Identical,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "c")
            )
        ]);
        var compiledNeq = RuleCompiler.CompileExpression(exprNeq, CellKind.Int, "testRule", "setState", context);
        var factNeq = compiledNeq[0].Operand!.Read(host);
        Assert.Equal(0L, factNeq.Value);
    }

    [Fact]
    public void AbsentCell_ReturnsAbsentFact() {
        var space = TestSpace();
        var v1 = MakeUnitVector(8, 0, 127);

        var rows = new[] {
            VectorTable("vecs", "testSpace", 4, ("a", v1))
        };
        var section = new StateSection(Spaces: [space], Rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        // Reading "missing" key
        var expr = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Dot,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("vecs", "missing")
            )
        ]);
        var compiled = RuleCompiler.CompileExpression(expr, CellKind.Int, "testRule", "setState", context);
        var layout = new FrameLayout(rows, static _ => null, name => (name == "testSpace") ? space : null);
        var host = new FrameHost(layout, rows, catalog, CompiledPatterns.Empty, []);
        host.Frame.Load(new RowStore(rows));

        var fact = compiled[0].Operand!.Read(host);
        Assert.True(fact.IsAbsent);
    }

    [Fact]
    public void SpaceMismatch_ThrowsRefusal() {
        var space1 = TestSpace("space1", 8);
        var space2 = TestSpace("space2", 8);

        var rows = new[] {
            VectorTable("vecs1", "space1", 4),
            VectorTable("vecs2", "space2", 4)
        };
        var section = new StateSection(Spaces: [space1, space2], Rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var expr = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Dot,
                Left: new VectorOperandToken.Cell("vecs1", "a"),
                Right: new VectorOperandToken.Cell("vecs2", "b")
            )
        ]);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileExpression(expr, CellKind.Int, "testRule", "setState", context)
        );
        Assert.Equal(RuleRefusal.VectorSpaceMismatch, ex.Refusal);
    }

    [Fact]
    public void NonVectorOperand_ThrowsRefusal() {
        var space = TestSpace();
        var rows = new[] {
            VectorTable("vecs", "testSpace", 4),
            IntTable("nums", 4)
        };
        var section = new StateSection(Spaces: [space], Rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(section, catalog, null, null, null, 240, RuleVocabulary.Core);

        var expr = new ValueExpression(Tokens: [
            new ValueToken.VectorCall(
                Operation: ExpressionOp.Dot,
                Left: new VectorOperandToken.Cell("vecs", "a"),
                Right: new VectorOperandToken.Cell("nums", "b")
            )
        ]);

        var ex = Assert.Throws<RuleException>(() =>
            RuleCompiler.CompileExpression(expr, CellKind.Int, "testRule", "setState", context)
        );
        Assert.Equal(RuleRefusal.VectorOperandNotVector, ex.Refusal);
    }
}
