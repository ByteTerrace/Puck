namespace Puck.State;

/// <summary>One resolved read operand of a rule — the base every case type derives from, whether declared here or by
/// a document project's <see cref="OperandFamily"/>. Case types are classes, never records or structs: nothing at
/// runtime compares two operands for equality or identity, so a generated structural <c>Equals</c> would be a hazard
/// nobody asked for. <see cref="ValueKind"/> is set once by the case's own constructor; everything else lives on the
/// concrete case type, reached through the virtuals every case answers for itself: what it reads
/// (<see cref="Read"/>), what one read costs (<see cref="Cost"/>), and which cells the read touches
/// (<see cref="CollectReads"/>).</summary>
public abstract class OperandFact {
    /// <summary>Initializes the operand with the encoding its value is returned in.</summary>
    /// <param name="valueKind">The raw encoding this operand's value is returned in.</param>
    protected OperandFact(CellKind valueKind) => ValueKind = valueKind;

    /// <summary>Gets the raw encoding this operand's value is returned in.</summary>
    public CellKind ValueKind { get; }

    /// <summary>Reads the operand's live fact for the evaluation in flight. Allocation-free on every case declared
    /// here; a document project's case keeps the same contract.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    public abstract RuleFact Read(IRuleReader reader);

    /// <summary>Returns the conservative work units one read costs — a state-candidate visit count for a read that
    /// scans, 1 for a direct read.</summary>
    /// <param name="context">The compile context the operand was resolved against.</param>
    public abstract long Cost(RuleCompileContext context);

    /// <summary>Appends every state cell the read touches, including the cells its key indirections resolve through.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<RuleAccess> into) { }
}

/// <summary>Shared shape for the case types that address a state row through a (row, key-or-indirection) pair —
/// so a generic caller that must accept any of them (a token-domain check inside a pattern's own value expression)
/// can read the row name and the live key indirection without a type-pattern switch enumerating every other case.</summary>
public interface IStateAddressedOperand {
    /// <summary>The row this operand addresses.</summary>
    string Row { get; }
    /// <summary>The live key indirection (<see cref="RuleFacts.CellKeyPrefix"/>), or <see langword="null"/> for
    /// a literal key.</summary>
    CompiledCellRef? KeyFrom { get; }
}

/// <summary>A cell key a document project's <see cref="KeyFamily"/> resolves live — the one arm of
/// <see cref="CompiledCellRef"/> this library does not spell itself.</summary>
public abstract class KeyFact {
    /// <summary>Resolves the key for the evaluation in flight. Allocation-free in steady state: a key minted once is
    /// cached by the family's own reader.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    public abstract string Resolve(IRuleReader reader);
    /// <summary>Appends every state cell the resolution reads through.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<RuleAccess> into) { }
}

/// <summary>The key an implicit binding computed: the cell whose ordinal is the bound integer, spelled as that
/// integer — what <c>row[from + 1]</c> resolves through.</summary>
public sealed class BindingKeyFact : KeyFact {
    /// <summary>Initializes the fact.</summary>
    /// <param name="ordinal">The binding's slot in the rule.</param>
    public BindingKeyFact(int ordinal) => Ordinal = ordinal;

    /// <summary>Gets the binding's slot.</summary>
    public int Ordinal { get; }
    /// <inheritdoc/>
    public override string Resolve(IRuleReader reader) => IndexKeyCache.Get(index: reader.BindingValue(ordinal: Ordinal));
}

/// <summary>A state cell address whose integer value is read as a cell key at evaluation time
/// (<see cref="RuleFacts.CellKeyPrefix"/>), a bound key token, or a document project's own live key
/// (<see cref="Custom"/>) — every dynamic key resolves through this one indirection carrier.</summary>
/// <param name="Row">The row holding the indirection cell, for a <c>$cell:</c> indirection; empty otherwise.</param>
/// <param name="Key">The indirection cell's key, for a <c>$cell:</c> indirection; empty otherwise.</param>
/// <param name="Binding">The bound key read instead, when not <see cref="BoundKey.None"/>; then
/// <paramref name="Row"/>/<paramref name="Key"/> are empty.</param>
/// <param name="Handle">The compiled handle for <paramref name="Row"/> when <paramref name="Binding"/> is
/// <see cref="BoundKey.None"/> — resolved once at compile time so the per-tick indirection read never repeats a
/// row-name scan; <see langword="default"/> (invalid) for a binding-carried reference, which names no row.</param>
/// <param name="Custom">A document project's own live key, or <see langword="null"/> for a <c>$cell:</c>/binding
/// indirection.</param>
/// <param name="InnerKeyBinding">For a <c>$cell:&lt;row&gt;:&lt;key&gt;</c> indirection whose own inner <c>&lt;key&gt;</c>
/// spells a binding token (<c>$each</c>/<c>$left</c>/<c>$right</c>) rather than a literal declared cell — <paramref
/// name="Row"/>/<paramref name="Handle"/> still name the row, but the cell read every evaluation is whichever one the
/// current binding names, never a compile-time-fixed <paramref name="Key"/> (left empty in this case).
/// <see cref="BoundKey.None"/> for an ordinary literal-keyed <c>$cell:</c> indirection. Distinct from
/// <paramref name="Binding"/>, which spells "the whole key is the binding, no row at all" — this spells "the row is
/// fixed, only its key is bound".</param>
public readonly record struct CompiledCellRef(string Row, string Key, BoundKey Binding = BoundKey.None, StateHandle Handle = default, KeyFact? Custom = null, BoundKey InnerKeyBinding = BoundKey.None);

/// <summary>One state cell a rule reads or writes — a literal <c>row.key</c>, or a whole row when the key is resolved
/// live (a <c>$cell:</c> indirection, a bound <c>$each</c>, a push, a generate, a transform).</summary>
/// <param name="Row">The state row.</param>
/// <param name="Key">The literal key, or <see langword="null"/> for any key of the row.</param>
/// <param name="IsSet">For a write, whether it replaces the cell (a set) rather than accumulating into it (an add).</param>
public readonly record struct RuleAccess(string Row, string? Key, bool IsSet = false) {
    /// <summary>Gets a value indicating whether two accesses can touch the same cell.</summary>
    /// <param name="other">The other access.</param>
    public bool Overlaps(RuleAccess other) =>
        string.Equals(a: Row, b: other.Row, comparisonType: StringComparison.Ordinal) &&
        ((Key is null) || (other.Key is null) || string.Equals(a: Key, b: other.Key, comparisonType: StringComparison.Ordinal));

    /// <summary>Formats the access as <c>row.key</c> or <c>row.*</c>.</summary>
    public string Describe() => $"{Row}.{Key ?? "*"}";

    /// <summary>Appends the cells a key indirection reads through, if any.</summary>
    /// <param name="reference">The indirection.</param>
    /// <param name="into">The read set being collected.</param>
    public static void CollectReference(CompiledCellRef? reference, List<RuleAccess> into) {
        if (reference is not { } cell) {
            return;
        }
        if (cell.Custom is { } custom) {
            custom.CollectReads(into: into);
            return;
        }
        if (cell.Row.Length == 0) {
            return;
        }

        into.Add(item: new RuleAccess(Row: cell.Row, Key: ((cell.Binding == BoundKey.None) ? cell.Key : null)));
    }
}
