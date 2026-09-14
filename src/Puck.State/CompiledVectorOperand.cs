using Puck.Maths;

namespace Puck.State;

/// <summary>One resolved vector operand for an expression or effect: either a state cell or a constant vector.</summary>
public sealed class CompiledVectorOperand {
    /// <summary>Gets the compiled row ordinal in the store/layout, or -1 for a constant.</summary>
    public int RowOrdinal { get; }
    /// <summary>Gets the compiled state handle, for a state cell.</summary>
    public StateHandle Handle { get; }
    /// <summary>Gets the row name, for a state cell.</summary>
    public string RowName { get; }
    /// <summary>Gets the literal cell key, or <see langword="null"/> when dynamic.</summary>
    public string? Key { get; }
    /// <summary>Gets the dynamic key indirection, or <see langword="null"/> for a literal key.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the pre-parsed cell key for a literal key, the slot key for a slot, or default.</summary>
    public CellName CellKey { get; }
    /// <summary>Gets the constant vector, or <see langword="null"/> for a live cell read.</summary>
    public StateVector? Constant { get; }
    /// <summary>Gets the vector space this operand is defined in.</summary>
    public StateSpace Space { get; }

    /// <summary>Gets whether this operand is a constant vector literal.</summary>
    public bool IsConstant => (Constant is not null);

    /// <summary>Initializes a live state cell vector operand.</summary>
    public CompiledVectorOperand(
        int rowOrdinal,
        StateHandle handle,
        string rowName,
        string? key,
        CompiledCellRef? keyFrom,
        CellName cellKey,
        StateSpace space
    ) {
        RowOrdinal = rowOrdinal;
        Handle = handle;
        RowName = rowName;
        Key = key;
        KeyFrom = keyFrom;
        CellKey = cellKey;
        Space = space;
        Constant = null;
    }

    /// <summary>Initializes a constant vector literal operand.</summary>
    public CompiledVectorOperand(StateVector constant, StateSpace space) {
        Constant = constant;
        Space = space;
        RowOrdinal = -1;
        Handle = default;
        RowName = string.Empty;
        Key = null;
        KeyFrom = null;
        CellKey = default;
    }

    /// <summary>Tries to read the component span for this operand during rule evaluation.</summary>
    /// <param name="reader">The rule reader.</param>
    /// <param name="span">The components on success, or empty.</param>
    /// <returns><see langword="true"/> if the vector cell was found and read; otherwise <see langword="false"/>.</returns>
    public bool TryReadSpan(IRuleReader reader, out ReadOnlySpan<sbyte> span) {
        if (Constant is { } c) {
            span = c.Components;
            return true;
        }

        var resolvedKey = RuleEvaluation.ResolveKey(reader: reader, key: Key, keyFrom: KeyFrom);
        if (string.IsNullOrEmpty(resolvedKey) && KeyFrom.HasValue) {
            span = default;
            return false;
        }

        var cellName = (KeyFrom.HasValue || (Key is not null))
            ? (CellName.TryParse(candidate: resolvedKey, name: out var parsed, reason: out _) ? parsed : default)
            : StateRow.SlotKey;

        if ((cellName == default) && (Key is not null)) {
            span = default;
            return false;
        }

        return reader.Store.TryStoredVector(rowOrdinal: RowOrdinal, key: cellName, components: out span);
    }

    /// <summary>Appends the state cell reads this operand touches.</summary>
    /// <param name="into">The access list.</param>
    public void CollectReads(List<RuleAccess> into) {
        if (IsConstant) { return; }
        into.Add(item: new RuleAccess(
            Row: RowName,
            Key: (KeyFrom is null) ? Key : null
        ));
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }
}

/// <summary>An operand that evaluates a vector function (dot, cosine, identical) over two vector operands.</summary>
public sealed class VectorCallOperand : OperandFact {
    /// <summary>Gets the vector operation.</summary>
    public ExpressionOp Operation { get; }
    /// <summary>Gets the left vector operand.</summary>
    public CompiledVectorOperand Left { get; }
    /// <summary>Gets the right vector operand.</summary>
    public CompiledVectorOperand Right { get; }
    /// <summary>Gets the dimensions of the vector space.</summary>
    public int Dimensions { get; }

    /// <summary>Initializes a vector call operand.</summary>
    public VectorCallOperand(ExpressionOp operation, CompiledVectorOperand left, CompiledVectorOperand right, CellKind valueKind)
        : base(valueKind: valueKind) {
        Operation = operation;
        Left = left;
        Right = right;
        Dimensions = left.Space.Dimensions;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        Left.CollectReads(into: into);
        Right.CollectReads(into: into);
    }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => Operation switch {
        ExpressionOp.Similarity => 2L + (3L * Dimensions),
        _ => 2L + Dimensions,
    };

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) {
        if (!Left.TryReadSpan(reader: reader, span: out var leftSpan) ||
            !Right.TryReadSpan(reader: reader, span: out var rightSpan)) {
            return RuleFact.Absent(kind: ValueKind);
        }

        switch (Operation) {
            case ExpressionOp.Dot: {
                var dot = SignedByteVectorFunctions.Dot(left: leftSpan, right: rightSpan);
                return RuleFact.Finite(value: dot, kind: CellKind.Int);
            }
            case ExpressionOp.Similarity: {
                var cos = SignedByteVectorFunctions.CosineQ16(left: leftSpan, right: rightSpan);
                return RuleFact.Finite(value: cos, kind: CellKind.Fixed);
            }
            case ExpressionOp.Identical: {
                var identical = leftSpan.SequenceEqual(other: rightSpan);
                return RuleFact.Finite(value: identical ? 1L : 0L, kind: CellKind.Int);
            }
            default:
                return RuleFact.Absent(kind: ValueKind);
        }
    }
}
