using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>One resolved vector operand for an expression or effect: either a state cell addressed by ordinal and
/// interned key, or a constant vector.</summary>
public sealed class CompiledVector {
    /// <summary>Initializes a live state-cell vector operand.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The literal cell key, or the invalid default when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="space">The vector space this operand is defined in.</param>
    /// <param name="describe">The authored spelling, for the read-back.</param>
    public CompiledVector(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, StateSpace space, string describe) {
        ArgumentNullException.ThrowIfNull(argument: space);

        Constant = null;
        Describe = describe;
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
        Space = space;
    }
    /// <summary>Initializes a constant vector literal operand.</summary>
    /// <param name="constant">The literal.</param>
    /// <param name="space">The vector space this operand is defined in.</param>
    public CompiledVector(StateVector constant, StateSpace space) {
        ArgumentNullException.ThrowIfNull(argument: constant);
        ArgumentNullException.ThrowIfNull(argument: space);

        Constant = constant;
        Describe = "vector(...)";
        Key = default;
        KeyFrom = null;
        RowOrdinal = -1;
        Space = space;
    }

    /// <summary>Gets the constant vector, or <see langword="null"/> for a live cell read.</summary>
    public StateVector? Constant { get; }
    /// <summary>Gets the authored spelling, for the read-back.</summary>
    public string Describe { get; }
    /// <summary>Gets a value indicating whether this operand is a constant vector literal.</summary>
    public bool IsConstant => (Constant is not null);
    /// <summary>Gets the literal cell key, or the invalid default when <see cref="KeyFrom"/> applies.</summary>
    public CellKey Key { get; }
    /// <summary>Gets the dynamic key indirection, or <see langword="null"/> for a literal key.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the row's catalog ordinal, or <c>-1</c> for a constant.</summary>
    public int RowOrdinal { get; }
    /// <summary>Gets the vector space this operand is defined in.</summary>
    public StateSpace Space { get; }

    /// <summary>Appends the state cells this operand reads.</summary>
    /// <param name="into">The read set being collected.</param>
    public void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (IsConstant) {
            return;
        }

        into.Add(item: new CellAccess(
            Key: ((KeyFrom is null)
            ? Key
            : default),
            RowOrdinal: RowOrdinal
        ));
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
    }
    /// <summary>Reads the component span for this operand during evaluation.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="span">The components on success, or empty.</param>
    /// <returns><see langword="true"/> when the vector was read.</returns>
    public bool TryReadSpan(IStateReader reader, out ReadOnlySpan<sbyte> span) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (Constant is { } constant) {
            span = constant.Components;

            return true;
        }

        var key = RuleReads.ResolveKey(
            keyFrom: KeyFrom,
            literal: Key,
            reader: reader
        );

        if (!key.IsValid) {
            span = default;

            return false;
        }

        return reader.Arena.TryReadVector(
            components: out span,
            key: key,
            rowOrdinal: RowOrdinal
        );
    }
}
/// <summary>An operand that evaluates a vector function (dot, cosine, identical) over two vector operands.</summary>
public sealed class VectorCallOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="operation">The vector operation.</param>
    /// <param name="left">The left vector operand.</param>
    /// <param name="right">The right vector operand.</param>
    /// <param name="valueKind">Fixed for a cosine, Int otherwise.</param>
    public VectorCallOperand(ExpressionOp operation, CompiledVector left, CompiledVector right, CellKind valueKind) : base(valueKind: valueKind) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        Dimensions = left.Space.Identity.Dimensions;
        Left = left;
        Operation = operation;
        Right = right;
    }

    /// <summary>Gets the dimensions of the vector space.</summary>
    public int Dimensions { get; }
    /// <summary>Gets the left vector operand.</summary>
    public CompiledVector Left { get; }
    /// <summary>Gets the vector operation.</summary>
    public ExpressionOp Operation { get; }
    /// <summary>Gets the right vector operand.</summary>
    public CompiledVector Right { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        Left.CollectReads(into: into);
        Right.CollectReads(into: into);
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (Operation switch {
        ExpressionOp.Similarity => (2L + (3L * Dimensions)),
        _ => (2L + Dimensions),
    });
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        if (
            !Left.TryReadSpan(
            reader: reader,
            span: out var left
        ) ||
            !Right.TryReadSpan(
            reader: reader,
            span: out var right
        )
        ) {
            return RuleFact.Absent(kind: ValueKind);
        }

        return (Operation switch {
            ExpressionOp.Dot => RuleFact.Finite(
            kind: CellKind.Int,
            value: SignedByteVectorFunctions.Dot(
                left: left,
                right: right
            )
        ),
            ExpressionOp.Similarity => RuleFact.Finite(
            kind: CellKind.Fixed,
            value: SignedByteVectorFunctions.CosineQ16(
                left: left,
                right: right
            )
        ),
            ExpressionOp.Identical => RuleFact.Finite(
            kind: CellKind.Int,
            value: (left.SequenceEqual(other: right)
                ? 1L
                : 0L)
        ),
            _ => RuleFact.Absent(kind: ValueKind),
        });
    }
}
