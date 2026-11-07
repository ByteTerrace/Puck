namespace Puck.State.Rules;

/// <summary>One resolved read operand of a compiled rule, whatever declares it: the library's own cases, or a
/// document project's case on <see cref="OperandFact{TFacet}"/>. The evaluator holds operands through this
/// interface.</summary>
/// <remarks>A facet is an interface the host implements, so the host's own reader is the facet instance a typed
/// fact's base hands it. <see cref="RuleNeeds.Admit"/> refuses a rule whose facet the reader does not advertise
/// before any read runs, which is what makes that resolution total.</remarks>
public interface IRuleOperand : ICompiledFact {
    /// <summary>Gets the raw encoding this operand's value is returned in.</summary>
    CellKind ValueKind { get; }

    /// <summary>Appends every state cell the read touches, including the cells its key indirections resolve through.</summary>
    /// <param name="into">The read set being collected.</param>
    void CollectReads(List<CellAccess> into);
    /// <summary>Returns the conservative work units one read costs.</summary>
    /// <param name="context">The context the operand was priced against.</param>
    /// <returns>The work units.</returns>
    RuleWork Cost(IRuleCostContext context);

    /// <summary>Gets every address the operand reads through, in the order <see cref="Read"/> receives them: a cell
    /// key, a live row's index, a table key or a ring age. Whoever reads the operand resolves each one first
    /// (<see cref="RuleOperandReads"/>): an address whose resolution faults ends the read before the operand runs,
    /// and an address computed by a program (<see cref="OperandAddress.Feed"/>) is fed the value that program left on
    /// the enclosing program's stack, so the operand never evaluates an expression.</summary>
    ReadOnlySpan<OperandAddress> Addresses { get; }

    /// <summary>Reads the operand's live fact for the evaluation in flight.</summary>
    /// <param name="reader">The evaluation in flight, which is also the host serving every facet it advertises.</param>
    /// <param name="keys">What each of <see cref="Addresses"/> named, in the same order: a cell key or an integer, or
    /// nothing. There is no faulted case to receive.</param>
    /// <returns>The fact.</returns>
    Fact Read(IStateReader reader, ReadOnlySpan<ResolvedKey> keys);
}
/// <summary>How an operand reads one of its declared addresses.</summary>
public enum AddressUse : byte {
    /// <summary>The address names an interned cell key, delivered as <see cref="ResolvedKey.Key"/>.</summary>
    Key,
    /// <summary>The address names an integer (a live row's table index, a table key, a ring age), delivered as
    /// <see cref="ResolvedKey.Index"/>.</summary>
    Index,
}
/// <summary>One address an operand reads through, and how it reads it.</summary>
public readonly struct OperandAddress {
    /// <summary>Initializes the address.</summary>
    /// <param name="reference">The compiled address.</param>
    /// <param name="use">How the operand reads what it names.</param>
    public OperandAddress(CompiledCellRef reference, AddressUse use) {
        Feed = reference.FeedProgram;
        Reference = reference;
        Use = use;
    }

    /// <summary>Gets the program computing the address, or <see langword="null"/> when the address needs none. A fed
    /// address is lowered into the enclosing program: these tokens run just before the operand's own, and the operand
    /// token consumes the value they leave.</summary>
    public CompiledExpressionToken[]? Feed { get; }
    /// <summary>Gets the compiled address.</summary>
    public CompiledCellRef Reference { get; }
    /// <summary>Gets how the operand reads what the address names.</summary>
    public AddressUse Use { get; }

    /// <summary>Lists the addresses an operand declares, skipping the absent ones.</summary>
    /// <param name="first">The first address, or <see langword="null"/>.</param>
    /// <param name="use">How the first is read.</param>
    /// <param name="second">The second address, or <see langword="null"/>.</param>
    /// <param name="secondUse">How the second is read.</param>
    /// <returns>The addresses, in declared order.</returns>
    public static OperandAddress[] List(CompiledCellRef? first, AddressUse use, CompiledCellRef? second = null, AddressUse secondUse = AddressUse.Key) {
        var list = new List<OperandAddress>(capacity: 2);

        if (first is { } head) {
            list.Add(item: new OperandAddress(
                reference: head,
                use: use
            ));
        }
        if (second is { } tail) {
            list.Add(item: new OperandAddress(
                reference: tail,
                use: secondUse
            ));
        }

        return [.. list];
    }
}
/// <summary>The value the program computing an address left, handed to the address's resolution: the integer its
/// program evaluated to, or the absence it read. <c>default</c> is the integer zero, which an address that carries no
/// program ignores.</summary>
/// <param name="Value">The integer the program evaluated to.</param>
/// <param name="Absent">Whether the program read an absence, so the address names nothing.</param>
public readonly record struct AddressFeed(long Value, bool Absent);
/// <summary>A read operand that answers from the arena alone. Case types are classes, never records or structs:
/// nothing at runtime compares two operands for equality, so a generated structural <c>Equals</c> would be a hazard
/// nobody asked for.</summary>
public abstract class RuleOperand : IRuleOperand {
    /// <summary>Initializes the operand with the encoding its value is returned in.</summary>
    /// <param name="valueKind">The raw encoding this operand's value is returned in.</param>
    protected RuleOperand(CellKind valueKind) => ValueKind = valueKind;

    /// <inheritdoc/>
    public FacetRef? RequiredFacet => null;
    /// <inheritdoc/>
    public CellKind ValueKind { get; }
    /// <inheritdoc/>
    public virtual ReadOnlySpan<OperandAddress> Addresses => default;

    /// <inheritdoc/>
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <inheritdoc/>
    public abstract RuleWork Cost(IRuleCostContext context);
    /// <inheritdoc/>
    public abstract Fact Read(IStateReader reader, ReadOnlySpan<ResolvedKey> keys);
}
/// <summary>Shared shape for the operand cases that address a row through a (row ordinal, key-or-indirection) pair,
/// so a caller that must accept any of them can read the row and the live key without enumerating every case.</summary>
public interface IStateAddressedOperand {
    /// <summary>Gets the catalog ordinal of the row this operand addresses, or <c>-1</c> for a live zone.</summary>
    int RowOrdinal { get; }
    /// <summary>Gets the live key indirection, or <see langword="null"/> for a literal key.</summary>
    CompiledCellRef? KeyFrom { get; }
}
/// <summary>A cell key a compiled rule resolves live: the engine's own (a zone endpoint, a binding) or a document
/// project's case on <see cref="KeyFact{TFacet}"/>. The key is interned, never a string.</summary>
public interface IRuleKey : ICompiledFact {
    /// <summary>Appends every state cell the resolution reads through.</summary>
    /// <param name="into">The read set being collected.</param>
    void CollectReads(List<CellAccess> into);
    /// <summary>Resolves the key for the evaluation in flight, and what it named.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <returns>The interned key. A fact naming a cell no key table interns is named with the invalid key: no row holds
    /// that cell, and it reads zero. A fact naming nothing (an empty zone's endpoint) is
    /// <see cref="FaultCase.Absent"/>, and reads absent; one whose computation faulted is
    /// <see cref="FaultCase.Faulted"/>.</returns>
    Resolved<CellKey> Resolve(IStateReader reader);
    /// <summary>Resolves the key when a program that selects a row it reads through has already left its value. Only
    /// a key that carries such a program (<see cref="ZoneEndKey"/>) reads the feed.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="feed">The value the key's program left.</param>
    /// <returns>The interned key, as <see cref="Resolve(IStateReader)"/> answers.</returns>
    Resolved<CellKey> Resolve(IStateReader reader, in AddressFeed feed) => Resolve(reader: reader);
    /// <summary>Resolves the key as an integer index when the program it reads through has already left its value.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="feed">The value the key's program left.</param>
    /// <returns>The index, as <see cref="ResolveIndex(IStateReader)"/> answers.</returns>
    Resolved<long> ResolveIndex(IStateReader reader, in AddressFeed feed) => ResolveIndex(reader: reader);
    /// <summary>Resolves a key for a state-writing effect. A dynamic key family may explicitly admit a runtime key
    /// here; ordinary reads never do so.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <returns>The key (the invalid default when the fact names no cell), <see cref="WriteKeyCase.Faulted"/> when
    /// computing it faulted, or <see cref="WriteKeyCase.Refused"/> when a required runtime-key admission failed; the
    /// last two make the enclosing firing rewind.</returns>
    WriteKey ResolveForWrite(IStateReader reader) {
        var resolved = Resolve(reader: reader);

        if (resolved.Case == FaultCase.Faulted) {
            return WriteKey.Faulted();
        }

        _ = resolved.TryNamed(value: out var key);

        return WriteKey.Resolved(key: key);
    }
    /// <summary>Resolves the key as an integer index — a live zone's table index, a static table's key.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <returns>The index when the key names a cell spelling an integer, <see cref="FaultCase.Faulted"/> when computing
    /// it faulted, and <see cref="FaultCase.Absent"/> otherwise.</returns>
    Resolved<long> ResolveIndex(IStateReader reader);
}
/// <summary>A live cell key that answers from the arena alone.</summary>
public abstract class RuleKeyFact : IRuleKey {
    /// <inheritdoc/>
    public FacetRef? RequiredFacet => null;

    /// <inheritdoc/>
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <inheritdoc/>
    public abstract Resolved<CellKey> Resolve(IStateReader reader);
    /// <summary>Resolves the key as an integer index. The default reads the key's interned name as an integer.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <returns>The index when the key spells an integer, <see cref="FaultCase.Faulted"/> when resolving it faulted,
    /// and <see cref="FaultCase.Absent"/> otherwise.</returns>
    public virtual Resolved<long> ResolveIndex(IStateReader reader) => IndexOf(
        reader: reader,
        resolved: Resolve(reader: reader)
    );
    /// <inheritdoc/>
    public virtual Resolved<CellKey> Resolve(IStateReader reader, in AddressFeed feed) => Resolve(reader: reader);
    /// <inheritdoc/>
    public virtual Resolved<long> ResolveIndex(IStateReader reader, in AddressFeed feed) => ResolveIndex(reader: reader);

    /// <summary>Reads a resolved key's interned name as an integer index.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="resolved">What the key resolved to.</param>
    /// <returns>The index when the key spells an integer, <see cref="FaultCase.Faulted"/> when resolving it faulted,
    /// and <see cref="FaultCase.Absent"/> otherwise.</returns>
    protected static Resolved<long> IndexOf(IStateReader reader, Resolved<CellKey> resolved) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        if (resolved.Case == FaultCase.Faulted) {
            return Resolved<long>.Faulted;
        }
        if (
            resolved.TryNamed(value: out var key) &&
            reader.Arena.Keys.TryGetName(
                key: key,
                name: out var name
            ) &&
            long.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var index,
                s: name.Value,
                style: System.Globalization.NumberStyles.Integer
            )
        ) {
            return Resolved<long>.Named(value: index);
        }

        return Resolved<long>.Nothing;
    }
}
/// <summary>One compiled effect of a rule, whatever declares it: the library's own cases, or a document project's
/// case on <see cref="EffectFact{TFacet}"/>. Firing stays with the evaluator that owns the mutation door; what this
/// carries is the shape the evaluator reads.</summary>
public interface IRuleEffect : ICompiledFact, IEffectNeeds {
    /// <summary>Gets the nested effect sequences this effect holds, each in firing order; empty for an effect with
    /// no arms.</summary>
    /// <remarks>An arm is deferred by its own <see cref="IEffectNeeds.Needs"/>, at whatever depth it sits, so a
    /// composite never reports its children's needs as its own.</remarks>
    IReadOnlyList<IRuleEffect[]> Arms { get; }
    /// <summary>Gets the authored spelling, for the rules read-back.</summary>
    string Describe { get; }
    /// <summary>Gets the values the evaluator reads before the arm runs, in the order the arm consumes them through
    /// <see cref="EffectFiring.Values"/>: an arm that fires outside the arena evaluates nothing itself, so a source
    /// that faults refuses the firing as faulted. Empty for an effect the evaluator applies itself.</summary>
    EffectSource[] Sources => [];
    /// <summary>Gets a value indicating whether firing submits a state mutation — an effect that only emits (a cue,
    /// a pose, a save) reads back as emitted rather than as a skipped write.</summary>
    bool SubmitsMutation { get; }

    /// <summary>Appends every state cell firing reads.</summary>
    /// <param name="into">The read set being collected.</param>
    void CollectReads(List<CellAccess> into);
    /// <summary>Appends every state cell firing writes.</summary>
    /// <param name="into">The write set being collected.</param>
    void CollectWrites(List<CellAccess> into);
    /// <summary>Returns the conservative work units one firing costs.</summary>
    /// <param name="context">The context the effect was priced against.</param>
    /// <returns>The work units.</returns>
    RuleWork Cost(IRuleCostContext context);
    /// <summary>Fires an arm the evaluator does not apply itself. The evaluator owns the mutation door for the
    /// library's own cases and never calls this for one; what reaches here is a document project's registered
    /// effect, which bridges to its own facet-typed firing, or a library case whose kernel lives outside this
    /// project.</summary>
    /// <param name="host">The mutation door, which is also the reader every operand answers from.</param>
    /// <param name="firing">The evaluation the arm fires under.</param>
    /// <param name="refusal">Why the arm did not fire, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the arm fired.</returns>
    bool TryFire(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal);
    /// <summary>Delivers an admitted outward effect after its gameplay transaction has committed.</summary>
    bool TryDeliverAfterCommit(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal) {
        if (firing.Preflight || ((Needs & EffectNeeds.Irreversible) == EffectNeeds.None)) {
            throw new InvalidOperationException(message: "Only an admitted post-commit delivery reaches this door.");
        }
        return TryFire(firing: in firing, host: host, refusal: out refusal);
    }
}
/// <summary>A compiled effect that writes the arena and nothing outside it.</summary>
public abstract class RuleEffect : IRuleEffect {
    /// <summary>Initializes the effect with its read-back spelling.</summary>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    protected RuleEffect(string describe) => Describe = describe;

    /// <inheritdoc/>
    public virtual IReadOnlyList<IRuleEffect[]> Arms => [];
    /// <inheritdoc/>
    public string Describe { get; }
    /// <summary>Gets what firing needs beyond reading and writing the arena. An arena write is rewound by the
    /// firing's own journal scope, so the library's own effects declare nothing.</summary>
    public virtual EffectNeeds Needs => EffectNeeds.None;
    /// <inheritdoc/>
    public FacetRef? RequiredFacet => null;
    /// <inheritdoc/>
    public virtual bool SubmitsMutation => true;

    /// <inheritdoc/>
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <inheritdoc/>
    public virtual void CollectWrites(List<CellAccess> into) { }

    /// <summary>Appends a write access record for an addressed effect.</summary>
    protected static void CollectWriteAccess(IStateAddressedEffect effect, List<CellAccess> into, bool isSet = true) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            IsSet: isSet,
            Key: ((effect.KeyFrom is null)
            ? effect.Key
            : default),
            RowOrdinal: effect.RowOrdinal
        ));
    }

    /// <inheritdoc/>
    public abstract RuleWork Cost(IRuleCostContext context);
    /// <summary>Fires an arm the evaluator does not apply itself. The default hands the effect to the host, which
    /// refuses by name unless it serves that arm.</summary>
    /// <param name="host">The mutation door.</param>
    /// <param name="firing">The evaluation the arm fires under.</param>
    /// <param name="refusal">Why the arm did not fire, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the arm fired.</returns>
    public virtual bool TryFire(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: host);

        return host.Fire(
            effect: this,
            firing: in firing,
            refusal: out refusal
        );
    }
}
/// <summary>Shared shape for the effect cases whose firing addresses one destination cell through a
/// (row ordinal, key-or-indirection) pair before the kind-specific work runs.</summary>
public interface IStateAddressedEffect {
    /// <summary>Gets the pre-parsed destination key for a literal key, or the invalid default.</summary>
    CellKey Key { get; }
    /// <summary>Gets the live key indirection, or <see langword="null"/> for a literal <see cref="Key"/>.</summary>
    CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the destination row's catalog ordinal.</summary>
    int RowOrdinal { get; }
    /// <summary>Gets the live row selection, or <see langword="null"/> when <see cref="RowOrdinal"/> names the row.
    /// A selection that resolves to no row addresses nothing and the effect is skipped.</summary>
    LiveRow? RowFrom => null;
}
/// <summary>Widens <see cref="IStateAddressedEffect"/> with the set/add write mode.</summary>
public interface IStateWriteEffect : IStateAddressedEffect {
    /// <summary>Gets whether the write replaces the cell or accumulates into it.</summary>
    StateWriteKind Write { get; }
}
/// <summary>Shared shape for the effect cases whose numeric value is read live rather than carried as a literal.</summary>
public interface IValueSourcedEffect {
    /// <summary>Gets the compiled value source.</summary>
    CompiledValueSource Source { get; }
}
/// <summary>A cell key resolved at evaluation: the value of another cell read as a key
/// (<see cref="RuleFacts.CellKeyPrefix"/>), a bound key token, or a compiled key fact. Every dynamic key a rule
/// spells resolves through this one carrier, and every address in it is an ordinal or an interned key.</summary>
/// <param name="RowOrdinal">The catalog ordinal of the row holding the indirection cell, or <c>-1</c>.</param>
/// <param name="Key">The indirection cell's interned key, or the invalid default.</param>
/// <param name="Binding">The bound key read instead, when not <see cref="BoundKey.None"/>; then
/// <paramref name="RowOrdinal"/> is <c>-1</c>.</param>
/// <param name="Custom">A compiled key fact, or <see langword="null"/> for a cell or binding indirection.</param>
/// <param name="InnerKeyBinding">For an indirection whose own inner key spells a binding token rather than a literal
/// declared cell: the row is known, and the cell read every evaluation is whichever one the binding names.</param>
/// <param name="Kind">The cell kind of the source cell, <see cref="CellKind.Int"/> or <see cref="CellKind.Text"/>.</param>
/// <param name="Program">A compiled Int expression whose value names the cell (the cell whose name is the integer it
/// evaluates to), or <see langword="null"/>. An operand's program is lowered into the enclosing program
/// (<see cref="OperandAddress.Feed"/>); an effect's destination, and a live row selected where no operand reads it,
/// has the evaluator run it where the address resolves.</param>
public readonly record struct CompiledCellRef(int RowOrdinal, CellKey Key, BoundKey Binding = BoundKey.None, IRuleKey? Custom = null, BoundKey InnerKeyBinding = BoundKey.None, CellKind Kind = CellKind.Int, CompiledExpressionToken[]? Program = null) {
    /// <summary>Gets the program whose value this address resolves through: its own <see cref="Program"/>, or the
    /// one a zone endpoint's live row selects through. An address holds at most one.</summary>
    public CompiledExpressionToken[]? FeedProgram => (Program ?? ((Custom as ZoneEndKey)?.FeedProgram));
    /// <summary>Gets the facet this indirection's own key fact declares, or <see langword="null"/>.</summary>
    public FacetRef? RequiredFacet => Custom?.RequiredFacet;

    /// <summary>Appends the cells a key indirection reads through, if any.</summary>
    /// <param name="reference">The indirection.</param>
    /// <param name="into">The read set being collected.</param>
    public static void CollectReference(CompiledCellRef? reference, List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (reference is not { } cell) {
            return;
        }
        if (cell.Program is { } program) {
            RuleDataflow.CollectExpression(
                into: into,
                tokens: program
            );

            return;
        }
        if (cell.Custom is { } custom) {
            custom.CollectReads(into: into);

            return;
        }
        if (cell.RowOrdinal < 0) {
            return;
        }

        into.Add(item: new CellAccess(
            Key: ((cell.Binding == BoundKey.None)
            ? cell.Key
            : default),
            RowOrdinal: cell.RowOrdinal
        ));
    }
    /// <summary>Appends every semantic fact a key indirection carries, including the expression behind a local or
    /// computed key. A local memo schedule uses this beside its row reach, so a clock or facet behind the key is not
    /// mistaken for a stable lookup.</summary>
    /// <param name="reference">The indirection.</param>
    /// <param name="into">The needs being built.</param>
    public static void CollectFacts(CompiledCellRef? reference, RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (reference is not { } cell) {
            return;
        }
        if (cell.Program is { } program) {
            RuleDataflow.CollectExpressionSchedulingFacts(
                into: into,
                tokens: program
            );

            return;
        }
        if (cell.Custom is not { } custom) {
            return;
        }

        into.AddFact(fact: custom);

        var source = (custom as LocalKeyFact)?.Source;

        if (source is not null) {
            RuleDataflow.CollectExpressionSchedulingFacts(
                into: into,
                tokens: source
            );
        }
    }
}
