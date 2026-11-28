using Puck.Maths;
using CompiledCellRef = Puck.State.Rules.CompiledCellRef;
using CompiledValueSource = Puck.State.Rules.CompiledValueSource;
using EffectSource = Puck.State.Rules.EffectSource;

namespace Puck.World;

/// <summary>What one identity record effect does to the identity's record store.</summary>
public enum WorldIdentityRecordOp : byte {
    /// <summary>Claims the lowest free slot of a pool and writes its fields.</summary>
    Claim,
    /// <summary>Claims a pair between two live instances and writes its fields.</summary>
    ClaimPair,
    /// <summary>Writes fields of the live instance in a slot.</summary>
    Write,
    /// <summary>Releases the live instance in a slot.</summary>
    Release,
}
/// <summary>One field a compiled identity record effect writes: a numeric literal stored as the field's own kind, a
/// fixed-point source read at firing, or a text literal.</summary>
/// <param name="Field">The record field.</param>
/// <param name="Source">The fixed-point source, or a literal's placeholder when <paramref name="Literal"/> or
/// <paramref name="Text"/> is set.</param>
/// <param name="Position">The source's position among the effect's sources, or -1 when it reads none.</param>
/// <param name="Literal">The numeric literal, or <see langword="null"/>.</param>
/// <param name="Text">The text literal, or <see langword="null"/>.</param>
public readonly record struct WorldIdentityRecordInput(CellName Field, CompiledValueSource Source, int Position, decimal? Literal, string? Text) {
    /// <summary>Returns the value this input gives a field of a kind, from the sources the firing read.</summary>
    /// <param name="kind">The field's kind on the identity's record.</param>
    /// <param name="values">The sources the firing read.</param>
    /// <param name="value">The value, on success.</param>
    /// <param name="reason">Why there is none, or empty on success.</param>
    /// <returns><see langword="true"/> when the input names a value of that kind.</returns>
    public bool TryValue(CellKind kind, in EffectValues values, out CellValue value, out string reason) {
        value = default;
        if (Text is { } text) {
            if (kind != CellKind.Text) {
                reason = $"field '{Field}' is {kind}, not text";
                return false;
            }
            value = CellValue.Text(value: text);
            reason = string.Empty;
            return true;
        }
        if (kind == CellKind.Text) {
            reason = $"field '{Field}' is text and takes a text literal";
            return false;
        }
        if (Literal is { } literal) {
            switch (kind) {
                case CellKind.Int when (decimal.Truncate(literal) == literal) && (literal >= long.MinValue) && (literal <= long.MaxValue):
                    value = CellValue.Int(value: (long)literal);
                    break;
                case CellKind.Fixed:
                    value = CellValue.Fixed(rawBits: NumericLiteral.ToFixed(value: literal).Value);
                    break;
                case CellKind.Bool:
                    value = CellValue.Bool(value: (literal != decimal.Zero));
                    break;
                default:
                    reason = $"field '{Field}' ({kind}) cannot hold {literal}";
                    return false;
            }
            reason = string.Empty;
            return true;
        }

        // The expression is read in fixed point (WorldFactsCompiler.ResolveIdentityRecord).
        long raw;

        if (Source.IsLiteral) {
            raw = Source.RawValue;
        } else if (!values[Position].TryFinite(raw: out raw)) {
            reason = $"field '{Field}''s expression names no number";
            return false;
        }

        var number = FixedQ4816.FromRawBits(value: raw);

        switch (kind) {
            case CellKind.Int when (number.Value & ((1L << FixedQ4816.FractionBitCount) - 1L)) == 0L:
                value = CellValue.Int(value: (raw >> FixedQ4816.FractionBitCount));
                break;
            case CellKind.Int:
                reason = $"field '{Field}' is Int and the expression gives {number}, which has a fraction";
                return false;
            case CellKind.Fixed:
                value = CellValue.Fixed(rawBits: raw);
                break;
            case CellKind.Bool:
                value = CellValue.Bool(value: (raw != 0L));
                break;
            default:
                reason = $"field '{Field}' ({kind}) cannot hold a number";
                return false;
        }
        reason = string.Empty;
        return true;
    }
}
/// <summary>Claims, writes or releases an instance of a record pool on the identity a world-addressed body drives
/// under, through the identity store's one door: the writes land in a stage the firing's scope rewinds, and the stage is
/// installed when the firing commits, under the writer the executing world's package names.</summary>
/// <remarks>The stage is host state beside the arena, so this arm fires inside the firing and declares no deferral;
/// the arena row it reads is the body key's, and it writes no arena row.</remarks>
public sealed class WorldIdentityRecordEffect : WorldFactEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="op">What it does.</param>
    /// <param name="key">The addressed body's literal key, or the invalid default.</param>
    /// <param name="keyFrom">The addressed body's live indirection, or <see langword="null"/>.</param>
    /// <param name="pool">The pool on the identity's document.</param>
    /// <param name="slot">The slot (the left endpoint for a pair), or a zero constant for a claim.</param>
    /// <param name="second">The right endpoint for a pair, or the lifetime a write or release names; a zero constant
    /// otherwise.</param>
    /// <param name="hasSecond">Whether <paramref name="second"/> was authored.</param>
    /// <param name="fields">The fields written, in order.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WorldIdentityRecordEffect(WorldIdentityRecordOp op, CellKey key, CompiledCellRef? keyFrom, CellName pool, in CompiledValueSource slot, in CompiledValueSource second, bool hasSecond, IReadOnlyList<WorldIdentityRecordField> fields, string describe) : base(describe: describe, reach: WorldDisclosureReach.Bodies) {
        ArgumentNullException.ThrowIfNull(argument: fields);

        var sources = new List<EffectSource> { EffectSource.Key(keyFrom: keyFrom, literal: key) };

        Op = op;
        Key = key;
        KeyFrom = keyFrom;
        Pool = pool;
        Slot = slot;
        SlotPosition = Append(sources: sources, source: in slot);
        Second = second;
        HasSecond = hasSecond;
        SecondPosition = Append(sources: sources, source: in second);

        var inputs = new WorldIdentityRecordInput[fields.Count];

        for (var index = 0; (index < fields.Count); index++) {
            var field = fields[index];

            inputs[index] = new WorldIdentityRecordInput(
                Field: field.Field,
                Literal: field.Literal,
                Position: (((field.Literal is null) && (field.Text is null)) ? Append(sources: sources, source: field.Source, kind: CellKind.Fixed) : -1),
                Source: field.Source,
                Text: field.Text
            );
        }
        Fields = inputs;
        m_sources = [.. sources];
    }

    private readonly EffectSource[] m_sources;

    /// <summary>The position of the addressed body's index among the sources.</summary>
    public const int BodySource = 0;

    /// <inheritdoc/>
    public override EffectSource[] Sources => m_sources;
    /// <summary>Gets the fields written, in order.</summary>
    public IReadOnlyList<WorldIdentityRecordInput> Fields { get; }
    /// <summary>Gets a value indicating whether <see cref="Second"/> was authored.</summary>
    public bool HasSecond { get; }
    /// <summary>Gets the addressed body's literal key, or the invalid default.</summary>
    public CellKey Key { get; }
    /// <summary>Gets the addressed body's live indirection, or <see langword="null"/>.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets what firing needs: the stage it writes is host state the firing's scope rewinds.</summary>
    public override EffectNeeds Needs => EffectNeeds.None;
    /// <summary>Gets what the effect does.</summary>
    public WorldIdentityRecordOp Op { get; }
    /// <summary>Gets the pool on the identity's document.</summary>
    public CellName Pool { get; }
    /// <summary>Gets the right endpoint of a pair, or the lifetime a write or release names.</summary>
    public CompiledValueSource Second { get; }
    /// <summary>Gets <see cref="Second"/>'s position among the sources, or -1 for a literal.</summary>
    public int SecondPosition { get; }
    /// <summary>Gets the slot, or a pair's left endpoint.</summary>
    public CompiledValueSource Slot { get; }
    /// <summary>Gets <see cref="Slot"/>'s position among the sources, or -1 for a literal.</summary>
    public int SlotPosition { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        Slot.CollectReads(into: into);
        Second.CollectReads(into: into);
        foreach (var field in Fields) {
            field.Source.CollectReads(into: into);
        }
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
    }
    /// <inheritdoc/>
    /// <remarks>A claim scans the pool's occupancy words and a release its dependent pairs; the store's own pool
    /// capacity is the identity's, which a compile cannot read, so the price is the fixed bound of one store write plus
    /// its sources.</remarks>
    public override RuleWork Cost(IRuleCostContext context) {
        var cost = (512L + Slot.Cost(context: context) + Second.Cost(context: context));

        foreach (var field in Fields) {
            cost += (16L + field.Source.Cost(context: context));
        }
        return cost;
    }

    // Appends a non-literal source, read in its compiled kind, and answers its position, or -1 for a literal the arm
    // reads raw.
    private static int Append(List<EffectSource> sources, in CompiledValueSource source, CellKind kind = CellKind.Int) {
        if (source.AsEffectSource(kind: kind) is not { } read) {
            return -1;
        }
        sources.Add(item: read);
        return (sources.Count - 1);
    }
}
/// <summary>One authored field of an identity record effect, compiled: its source, or a literal.</summary>
/// <param name="Field">The record field.</param>
/// <param name="Source">The fixed-point source, or a zero constant for a literal.</param>
/// <param name="Literal">The numeric literal, or <see langword="null"/>.</param>
/// <param name="Text">The text literal, or <see langword="null"/>.</param>
public readonly record struct WorldIdentityRecordField(CellName Field, CompiledValueSource Source, decimal? Literal, string? Text);
