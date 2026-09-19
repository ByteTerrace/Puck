using System.Globalization;

namespace Puck.State;

/// <summary>Which shape a <see cref="Mutation"/> carrier holds.</summary>
public enum MutationKind : byte {
    /// <summary>The carrier holds no case.</summary>
    None,

    /// <summary>A numeric set or add against one cell, minting it when the row's shape admits it.</summary>
    Write,

    /// <summary>A text set against one cell of a <see cref="CellKind.Text"/> row.</summary>
    WriteText,

    /// <summary>A removal of one cell of a keyed or ordered row.</summary>
    Remove,

    /// <summary>A push of one value onto a ring row.</summary>
    Push,

    /// <summary>One emission of a draw site.</summary>
    Generate,
}
/// <summary>One state mutation a firing effect asks its host to run, as a closed union over the shapes the rule
/// library itself emits. Every address is a catalog row ordinal and an interned cell key, never a name.</summary>
/// <remarks>Storage is inline — one ordinal, one key, one raw operand, one write mode and one reference beside a
/// discriminator byte — so a firing's effects cost no allocation. The default carrier holds no case at all:
/// <see cref="HasValue"/> is <see langword="false"/> and every payload accessor throws.</remarks>
[Union]
public readonly struct Mutation : IEquatable<Mutation>, IUnion {
    private readonly MutationKind m_kind;
    private readonly int m_rowOrdinal;
    private readonly CellKey m_key;
    private readonly long m_operand;
    private readonly StateWriteKind m_write;
    private readonly object? m_payload;

    private Mutation(MutationKind kind, int rowOrdinal, CellKey key, long operand, StateWriteKind write, object? payload) {
        m_key = key;
        m_kind = kind;
        m_operand = operand;
        m_payload = payload;
        m_rowOrdinal = rowOrdinal;
        m_write = write;
    }

    /// <summary>Gets a value indicating whether this carrier holds a case at all.</summary>
    public bool HasValue => (m_kind != MutationKind.None);
    /// <summary>Gets the addressed cell key of a <see cref="MutationKind.Write"/>,
    /// <see cref="MutationKind.WriteText"/> or <see cref="MutationKind.Remove"/>.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds a case that addresses no cell.</exception>
    public CellKey Key => ((m_kind is (MutationKind.Write or MutationKind.WriteText or MutationKind.Remove))
        ? m_key
        : throw Mismatch(expected: nameof(Key))
    );
    /// <summary>Gets which case this carrier holds.</summary>
    public MutationKind Kind => m_kind;
    /// <summary>Gets the raw operand of a <see cref="MutationKind.Write"/> or the pushed value of a
    /// <see cref="MutationKind.Push"/>, in the destination row's own encoding.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds a case that carries no operand.</exception>
    public long Operand => ((m_kind is (MutationKind.Write or MutationKind.Push))
        ? m_operand
        : throw Mismatch(expected: nameof(Operand))
    );
    /// <summary>Gets the addressed row's catalog ordinal.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds no case.</exception>
    public int RowOrdinal => (HasValue
        ? m_rowOrdinal
        : throw Mismatch(expected: nameof(RowOrdinal))
    );
    /// <summary>Gets the text of a <see cref="MutationKind.WriteText"/>.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public string Text => ((m_kind == MutationKind.WriteText)
        ? ((string)m_payload!)
        : throw Mismatch(expected: nameof(Text))
    );
    /// <summary>Gets whether a <see cref="MutationKind.Write"/> replaces the cell or accumulates into it.</summary>
    /// <exception cref="InvalidOperationException">The carrier holds another case, or none.</exception>
    public StateWriteKind Write => ((m_kind == MutationKind.Write)
        ? m_write
        : throw Mismatch(expected: nameof(Write))
    );

    object? IUnion.Value => (m_kind switch {
        MutationKind.Write => (m_rowOrdinal, m_key, m_operand, m_write),
        MutationKind.WriteText => (m_rowOrdinal, m_key, m_payload),
        MutationKind.Remove => (m_rowOrdinal, m_key),
        MutationKind.Push => (m_rowOrdinal, m_operand),
        MutationKind.Generate => m_rowOrdinal,
        _ => null,
    });

    /// <summary>Creates one emission of a draw site.</summary>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <returns>The carrier.</returns>
    public static Mutation Generate(int rowOrdinal) => new(
        key: default,
        kind: MutationKind.Generate,
        operand: 0L,
        payload: null,
        rowOrdinal: rowOrdinal,
        write: StateWriteKind.Set
    );
    /// <summary>Creates a push of one value onto a ring row.</summary>
    /// <param name="rowOrdinal">The ring row's catalog ordinal.</param>
    /// <param name="value">The raw value in the row's own encoding.</param>
    /// <returns>The carrier.</returns>
    public static Mutation Push(int rowOrdinal, long value) => new(
        key: default,
        kind: MutationKind.Push,
        operand: value,
        payload: null,
        rowOrdinal: rowOrdinal,
        write: StateWriteKind.Set
    );
    /// <summary>Creates a removal of one cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by the arena's catalog.</param>
    /// <returns>The carrier.</returns>
    public static Mutation Remove(int rowOrdinal, CellKey key) => new(
        key: key,
        kind: MutationKind.Remove,
        operand: 0L,
        payload: null,
        rowOrdinal: rowOrdinal,
        write: StateWriteKind.Set
    );
    /// <summary>Creates a numeric set or add against one cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by the arena's catalog.</param>
    /// <param name="operand">The replacement for a set, or the addend for an add, in the row's own encoding.</param>
    /// <param name="write">Set or add.</param>
    /// <returns>The carrier.</returns>
    public static Mutation Written(int rowOrdinal, CellKey key, long operand, StateWriteKind write) => new(
        key: key,
        kind: MutationKind.Write,
        operand: operand,
        payload: null,
        rowOrdinal: rowOrdinal,
        write: write
    );
    /// <summary>Creates a text set against one cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, interned by the arena's catalog.</param>
    /// <param name="text">The text to store.</param>
    /// <returns>The carrier.</returns>
    public static Mutation WrittenText(int rowOrdinal, CellKey key, string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        return new Mutation(
            key: key,
            kind: MutationKind.WriteText,
            operand: 0L,
            payload: text,
            rowOrdinal: rowOrdinal,
            write: StateWriteKind.Set
        );
    }

    /// <summary>Determines whether two carriers hold the same case with the same payload.</summary>
    /// <param name="left">The left carrier.</param>
    /// <param name="right">The right carrier.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    public static bool operator ==(Mutation left, Mutation right) => left.Equals(other: right);
    /// <summary>Determines whether two carriers differ in case or payload.</summary>
    /// <param name="left">The left carrier.</param>
    /// <param name="right">The right carrier.</param>
    /// <returns><see langword="true"/> when the two differ.</returns>
    public static bool operator !=(Mutation left, Mutation right) => !left.Equals(other: right);

    /// <inheritdoc/>
    public bool Equals(Mutation other) => (
        (m_kind == other.m_kind) &&
        (m_key == other.m_key) &&
        (m_operand == other.m_operand) &&
        (m_rowOrdinal == other.m_rowOrdinal) &&
        (m_write == other.m_write) &&
        Equals(
        objA: m_payload,
        objB: other.m_payload
    )
    );
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is Mutation other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        value1: m_kind,
        value2: m_rowOrdinal,
        value3: m_key,
        value4: m_operand,
        value5: m_write,
        value6: m_payload
    );
    /// <inheritdoc/>
    public override string ToString() => (m_kind switch {
        MutationKind.Write => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"Write({m_rowOrdinal}, {m_key.Ordinal}, {m_operand}, {m_write})"
    ),
        MutationKind.WriteText => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"WriteText({m_rowOrdinal}, {m_key.Ordinal})"
    ),
        MutationKind.Remove => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"Remove({m_rowOrdinal}, {m_key.Ordinal})"
    ),
        MutationKind.Push => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"Push({m_rowOrdinal}, {m_operand})"
    ),
        MutationKind.Generate => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"Generate({m_rowOrdinal})"
    ),
        _ => "none",
    });

    private InvalidOperationException Mismatch(string expected) => new(message: (HasValue
        ? $"A Mutation holding {m_kind} was read as {expected}."
        : $"A default Mutation holds no case and cannot be read as {expected}."
    ));
}
/// <summary>Why a firing effect did not fire, as the category it reported and the concrete runtime reason. The
/// default carrier is the non-refusal: <see cref="Code"/> is <see langword="null"/> and <see cref="IsRefused"/> is
/// <see langword="false"/>, so a fired effect allocates nothing and boxes no enum.</summary>
/// <param name="Code">The refusal category — a member of a <see cref="RefusalAttribute"/>-tagged enum — or
/// <see langword="null"/> when nothing refused.</param>
/// <param name="Reason">The concrete runtime reason, in the author's own vocabulary; empty when nothing
/// refused.</param>
public readonly record struct EffectRefusal(Enum? Code, string Reason) {
    /// <summary>Gets the carrier naming no refusal.</summary>
    public static EffectRefusal None => new(
        Code: null,
        Reason: string.Empty
    );
    /// <summary>Gets a value indicating whether this carrier names a refusal.</summary>
    public bool IsRefused => (Code is not null);

    /// <summary>Creates a refusal.</summary>
    /// <typeparam name="TRefusal">The tagged refusal enum.</typeparam>
    /// <param name="code">The category.</param>
    /// <param name="reason">The concrete runtime reason.</param>
    /// <returns>The carrier.</returns>
    public static EffectRefusal Of<TRefusal>(TRefusal code, string reason) where TRefusal : unmanaged, Enum => new(
        Code: code,
        Reason: reason
    );
}
/// <summary>The evaluation one effect fires under: which rule, the tick pair it answers as of, how wide the
/// simulation step is, and whether the call is the host's pre-commit admission check rather than the firing
/// itself.</summary>
/// <param name="RuleName">The firing rule's name, for the refusal ledger.</param>
/// <param name="Tick">The simulation tick.</param>
/// <param name="EngineTick">The engine tick.</param>
/// <param name="StepTicks">How many engine ticks the simulation step spans.</param>
/// <param name="Preflight">Whether the host should validate what it can without acting. An irreversible arm is
/// preflighted against the state the firing proposes to commit, so a refusal it can see rewinds the firing instead
/// of landing outward after the commit.</param>
public readonly record struct EffectFiring(string RuleName, ulong Tick, ulong EngineTick, ulong StepTicks, bool Preflight = false);
/// <summary>What a firing effect writes through: the reader every operand answers from, widened with the one
/// mutation door the library's own effects install by, the arm door for an effect the library compiled but does not
/// itself apply, and the notification a host installs its own side of a committed firing on.</summary>
/// <remarks>
/// <para>Every member is reached on the tick path. A host allocates nothing per call beyond what its own door
/// already allocates.</para>
/// <para>The host's own installation is the arena commit: an effect never asks a host to compose a private
/// candidate, because the firing's journal scope already is one.</para>
/// </remarks>
public interface IEffectHost : IStateReader {
    /// <summary>Runs one mutation through the host's own door, inside the firing's open journal scope.</summary>
    /// <param name="mutation">The mutation.</param>
    /// <param name="refusal">Why the door refused, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the mutation moved the arena; <see langword="false"/> with no refusal
    /// when it was admitted and left the addressed cell exactly as it was.</returns>
    bool Apply(in Mutation mutation, out EffectRefusal refusal);
    /// <summary>Observes a firing whose journal scope has just committed, so a host can install its own side of it.
    /// Called once per committed firing, before any irreversible arm fires.</summary>
    /// <param name="scope">The mark the committed scope closed with.</param>
    void Committed(int scope) { }
    /// <summary>Fires an arm the library compiled but does not itself apply — a document project's registered
    /// effect, or a library case whose kernel lives in a project this one does not reference. The default refuses by
    /// name, so an unbound arm is a counted refusal rather than a silent success.</summary>
    /// <param name="effect">The compiled effect.</param>
    /// <param name="firing">The evaluation the arm fires under.</param>
    /// <param name="refusal">Why the arm did not fire, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the arm fired.</returns>
    bool Fire(ICompiledFact effect, in EffectFiring firing, out EffectRefusal refusal) {
        refusal = Unbound(effect: effect);

        return false;
    }
    /// <summary>Creates the refusal an arm draws from a host that serves no arm of its kind.</summary>
    /// <param name="effect">The compiled effect.</param>
    /// <returns>The refusal.</returns>
    static EffectRefusal Unbound(ICompiledFact effect) {
        ArgumentNullException.ThrowIfNull(argument: effect);

        return EffectRefusal.Of(
            code: StateEffectRefusal.ArmUnbound,
            reason: $"this host serves no arm of kind '{effect.GetType().Name}'"
        );
    }
}
/// <summary>The refusals a host's own doors draw while a rule fires, beside the categories the evaluator and a
/// document project's arms report through the same ledger.</summary>
public enum StateEffectRefusal : byte {
    /// <summary>A compiled effect reached a host that serves no arm of its kind.</summary>
    [Refusal(door: "state.rule.fire", condition: "a compiled effect reaches a host that serves no arm of its kind", kind: RefusalKind.Verdict)]
    ArmUnbound,
}
