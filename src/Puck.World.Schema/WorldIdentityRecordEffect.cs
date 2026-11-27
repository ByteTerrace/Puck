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
/// <summary>One field a compiled identity record effect writes: a numeric literal stored as the field's own kind, an
/// integer source read at firing, a text literal, or the id of the identity another body drives under.</summary>
/// <param name="Field">The record field.</param>
/// <param name="Source">The integer source, or a literal's placeholder when <paramref name="Literal"/> or
/// <paramref name="Text"/> is set.</param>
/// <param name="Position">The position among the effect's sources of the integer source, or of the body an identity id
/// is read from; -1 when it reads none.</param>
/// <param name="Literal">The numeric literal, or <see langword="null"/>.</param>
/// <param name="Text">The text literal, or <see langword="null"/>.</param>
/// <param name="IdentityOf">Whether the field holds the id of the identity the body at <paramref name="Position"/>
/// drives under.</param>
public readonly record struct WorldIdentityRecordInput(CellName Field, CompiledValueSource Source, int Position, decimal? Literal, string? Text, bool IdentityOf = false) {
    /// <summary>Returns the value this input gives a field of a kind, from the sources the firing read.</summary>
    /// <param name="kind">The field's kind on the identity's record.</param>
    /// <param name="values">The sources the firing read.</param>
    /// <param name="identityId">The id of the identity the body this input names drives under, for an
    /// <see cref="IdentityOf"/> input; ignored otherwise.</param>
    /// <param name="value">The value, on success.</param>
    /// <param name="reason">Why there is none, or empty on success.</param>
    /// <returns><see langword="true"/> when the input names a value of that kind.</returns>
    public bool TryValue(CellKind kind, in EffectValues values, string? identityId, out CellValue value, out string reason) {
        value = default;
        if (IdentityOf) {
            if (kind != CellKind.Text) {
                reason = $"field '{Field}' is {kind}, not text, so it cannot hold an identity's id";
                return false;
            }
            if (identityId is null) {
                reason = $"field '{Field}' names a body that drives under no identity";
                return false;
            }
            value = CellValue.Text(value: identityId);
            reason = string.Empty;
            return true;
        }
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
            if (!WorldIdentityRecordLiteral.TryFromNumber(kind: kind, literal: literal, value: out value)) {
                reason = $"field '{Field}' ({kind}) cannot hold {literal}";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        long raw;

        if (Source.IsLiteral) {
            raw = Source.RawValue;
        } else if (!values[Position].TryFinite(raw: out raw)) {
            reason = $"field '{Field}''s expression names no number";
            return false;
        }
        switch (kind) {
            case CellKind.Int:
                value = CellValue.Int(value: raw);
                break;
            case CellKind.Fixed:
                value = CellValue.Fixed(rawBits: FixedQ4816.FromInteger(value: raw).Value);
                break;
            case CellKind.Bool:
                value = CellValue.Bool(value: (raw != 0L));
                break;
            default:
                reason = $"field '{Field}' ({kind}) cannot hold an integer";
                return false;
        }
        reason = string.Empty;
        return true;
    }
}
/// <summary>Reads an authored literal as the value an identity record field of one kind holds: the one conversion a
/// record effect's literal field and a record read's match literal share.</summary>
public static class WorldIdentityRecordLiteral {
    /// <summary>Converts a numeric literal to a field kind: an exact integer, a fixed-point number, or a flag (zero is
    /// false).</summary>
    /// <param name="kind">The field's kind.</param>
    /// <param name="literal">The numeric literal.</param>
    /// <param name="value">The value, on success.</param>
    /// <returns><see langword="true"/> when a field of <paramref name="kind"/> can hold <paramref name="literal"/>.</returns>
    public static bool TryFromNumber(CellKind kind, decimal literal, out CellValue value) {
        switch (kind) {
            case CellKind.Int when (decimal.Truncate(literal) == literal) && (literal >= long.MinValue) && (literal <= long.MaxValue):
                value = CellValue.Int(value: (long)literal);
                return true;
            case CellKind.Fixed:
                value = CellValue.Fixed(rawBits: NumericLiteral.ToFixed(value: literal).Value);
                return true;
            case CellKind.Bool:
                value = CellValue.Bool(value: (literal != decimal.Zero));
                return true;
            default:
                value = default;
                return false;
        }
    }
    /// <summary>Parses an authored literal as a field kind: a text field holds it as written, and any other kind reads it
    /// as an invariant-culture number (<see cref="TryFromNumber"/>).</summary>
    /// <param name="kind">The field's kind.</param>
    /// <param name="literal">The literal, as authored.</param>
    /// <param name="value">The value, on success.</param>
    /// <returns><see langword="true"/> when a field of <paramref name="kind"/> can hold <paramref name="literal"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> is <see langword="null"/>.</exception>
    public static bool TryParse(CellKind kind, string literal, out CellValue value) {
        ArgumentNullException.ThrowIfNull(argument: literal);
        if (kind == CellKind.Text) {
            value = CellValue.Text(value: literal);
            return true;
        }
        if (!decimal.TryParse(s: literal, style: System.Globalization.NumberStyles.Number, provider: System.Globalization.CultureInfo.InvariantCulture, result: out var number)) {
            value = default;
            return false;
        }
        return TryFromNumber(kind: kind, literal: number, value: out value);
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

            inputs[index] = ((field.IdentityOf is { } other)
                ? new WorldIdentityRecordInput(
                    Field: field.Field,
                    IdentityOf: true,
                    Literal: null,
                    Position: AppendBody(body: other, sources: sources),
                    Source: field.Source,
                    Text: null
                )
                : new WorldIdentityRecordInput(
                    Field: field.Field,
                    Literal: field.Literal,
                    Position: (((field.Literal is null) && (field.Text is null)) ? Append(sources: sources, source: field.Source) : -1),
                    Source: field.Source,
                    Text: field.Text
                ));
            if (field.IdentityOf is { } named) {
                m_bodies.Add(item: named);
            }
        }
        Fields = inputs;
        m_sources = [.. sources];
    }

    private readonly EffectSource[] m_sources;
    private readonly List<WorldIdentityRecordBody> m_bodies = [];

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
        foreach (var body in m_bodies) {
            CompiledCellRef.CollectReference(into: into, reference: body.KeyFrom);
        }
    }
    /// <inheritdoc/>
    /// <remarks>A claim scans the pool's occupancy words and a release its dependent pairs, each priced at
    /// <see cref="WorldIdentityRecords.StoreWalkWork"/>, plus its sources.</remarks>
    public override RuleWork Cost(IRuleCostContext context) {
        var cost = (WorldIdentityRecords.StoreWalkWork + Slot.Cost(context: context) + Second.Cost(context: context));

        foreach (var field in Fields) {
            cost += (16L + field.Source.Cost(context: context));
        }
        return cost;
    }

    // Appends a body another field names and answers its position: the arm reads its index as it reads the written body's.
    private static int AppendBody(List<EffectSource> sources, WorldIdentityRecordBody body) {
        sources.Add(item: EffectSource.Key(keyFrom: body.KeyFrom, literal: body.Key));
        return (sources.Count - 1);
    }
    // Appends a non-literal source and answers its position, or -1 for a literal the arm reads raw.
    private static int Append(List<EffectSource> sources, in CompiledValueSource source) {
        if (source.AsEffectSource(kind: CellKind.Int) is not { } read) {
            return -1;
        }
        sources.Add(item: read);
        return (sources.Count - 1);
    }
}
/// <summary>One authored field of an identity record effect, compiled: its source, or a literal.</summary>
/// <param name="Field">The record field.</param>
/// <param name="Source">The integer source, or a zero constant for a literal.</param>
/// <param name="Literal">The numeric literal, or <see langword="null"/>.</param>
/// <param name="Text">The text literal, or <see langword="null"/>.</param>
/// <param name="IdentityOf">The body whose identity's id the field holds, or <see langword="null"/>.</param>
public readonly record struct WorldIdentityRecordField(CellName Field, CompiledValueSource Source, decimal? Literal, string? Text, WorldIdentityRecordBody? IdentityOf = null);
/// <summary>A body an identity record effect names beside the one it writes: a literal key or a live indirection.</summary>
/// <param name="Key">The body's literal key, or the invalid default.</param>
/// <param name="KeyFrom">The body's live indirection, or <see langword="null"/>.</param>
public readonly record struct WorldIdentityRecordBody(CellKey Key, CompiledCellRef? KeyFrom);
