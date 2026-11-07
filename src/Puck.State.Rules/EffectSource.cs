namespace Puck.State.Rules;

/// <summary>What one declared source of an effect reads.</summary>
public enum EffectSourceKind : byte {
    /// <summary>A cell key resolved to an integer index: a body an arm addresses.</summary>
    Key,
    /// <summary>An expression program.</summary>
    Program,
    /// <summary>An operand.</summary>
    Operand,
}
/// <summary>One value an effect needs, declared so the evaluator reads it before the arm runs: an arm that fires
/// outside the arena evaluates nothing itself, so a source that faults refuses the firing as faulted instead of an arm
/// reporting a refusal of its own.</summary>
public readonly struct EffectSource {
    private EffectSource(EffectSourceKind kind, CellKey literal, CompiledCellRef? keyFrom, CompiledExpressionToken[]? program, IRuleOperand? operand, CellKind valueKind) {
        Kind = kind;
        Literal = literal;
        KeyFrom = keyFrom;
        Program = program;
        Operand = operand;
        ValueKind = valueKind;
    }

    /// <summary>Gets what the source reads.</summary>
    public EffectSourceKind Kind { get; }
    /// <summary>Gets the literal key of a <see cref="EffectSourceKind.Key"/> source, used when
    /// <see cref="KeyFrom"/> is <see langword="null"/>.</summary>
    public CellKey Literal { get; }
    /// <summary>Gets the live key indirection of a <see cref="EffectSourceKind.Key"/> source, or
    /// <see langword="null"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the program of a <see cref="EffectSourceKind.Program"/> source.</summary>
    public CompiledExpressionToken[]? Program { get; }
    /// <summary>Gets the operand of a <see cref="EffectSourceKind.Operand"/> source.</summary>
    public IRuleOperand? Operand { get; }
    /// <summary>Gets the encoding a program or operand answers in.</summary>
    public CellKind ValueKind { get; }

    /// <summary>Declares a key the arm uses as an integer index.</summary>
    /// <param name="literal">The compile-time key, used when <paramref name="keyFrom"/> is
    /// <see langword="null"/>.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <returns>The source; it reads as the index, or <c>-1</c> when the key names no integer cell.</returns>
    public static EffectSource Key(CellKey literal, CompiledCellRef? keyFrom) => new(
        keyFrom: keyFrom,
        kind: EffectSourceKind.Key,
        literal: literal,
        operand: null,
        program: null,
        valueKind: CellKind.Int
    );
    /// <summary>Declares an expression program.</summary>
    /// <param name="program">The program.</param>
    /// <param name="kind">The encoding the program answers in.</param>
    /// <returns>The source.</returns>
    public static EffectSource Expression(CompiledExpressionToken[] program, CellKind kind) {
        ArgumentNullException.ThrowIfNull(argument: program);

        return new(
            keyFrom: null,
            kind: EffectSourceKind.Program,
            literal: default,
            operand: null,
            program: program,
            valueKind: kind
        );
    }
    /// <summary>Declares an operand.</summary>
    /// <param name="operand">The operand.</param>
    /// <param name="kind">The encoding the arm reads the operand in.</param>
    /// <returns>The source.</returns>
    public static EffectSource FromOperand(IRuleOperand operand, CellKind kind) {
        ArgumentNullException.ThrowIfNull(argument: operand);

        return new(
            keyFrom: null,
            kind: EffectSourceKind.Operand,
            literal: default,
            operand: operand,
            program: null,
            valueKind: kind
        );
    }
}
