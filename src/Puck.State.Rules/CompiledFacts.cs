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
    long Cost(IRuleCostContext context);
    /// <summary>Reads the operand's live fact for the evaluation in flight.</summary>
    /// <param name="reader">The evaluation in flight, which is also the host serving every facet it advertises.</param>
    /// <returns>The fact.</returns>
    RuleFact Read(IStateReader reader);
}
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
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <inheritdoc/>
    public abstract long Cost(IRuleCostContext context);
    /// <inheritdoc/>
    public abstract RuleFact Read(IStateReader reader);
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
    /// <summary>Resolves the key for the evaluation in flight, and whether it named a cell at all.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="named">Whether the fact named a cell. A fact naming a cell no key table interns answers
    /// <see langword="true"/> beside the invalid key: no row holds that cell, and it reads zero. A fact naming
    /// nothing — an empty zone's endpoint — answers <see langword="false"/>, and reads absent.</param>
    /// <returns>The interned key.</returns>
    CellKey Resolve(IStateReader reader, out bool named);
    /// <summary>Resolves the key as an integer index — a live zone's table index, a static table's key.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="index">The index on success.</param>
    /// <returns><see langword="true"/> when the key spells an integer.</returns>
    bool TryResolveIndex(IStateReader reader, out long index);
}
/// <summary>A live cell key that answers from the arena alone.</summary>
public abstract class RuleKeyFact : IRuleKey {
    /// <inheritdoc/>
    public FacetRef? RequiredFacet => null;

    /// <inheritdoc/>
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <inheritdoc/>
    public abstract CellKey Resolve(IStateReader reader, out bool named);
    /// <summary>Resolves the key as an integer index. The default reads the key's interned name as an integer.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="index">The index on success.</param>
    /// <returns><see langword="true"/> when the key spells an integer.</returns>
    public virtual bool TryResolveIndex(IStateReader reader, out long index) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var key = Resolve(
            named: out _,
            reader: reader
        );

        if (reader.Catalog.Keys.TryGetName(
            key: key,
            name: out var name
        )) {
            return long.TryParse(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out index,
                s: name.Value,
                style: System.Globalization.NumberStyles.Integer
            );
        }

        index = 0L;

        return false;
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
    long Cost(IRuleCostContext context);
    /// <summary>Fires an arm the evaluator does not apply itself. The evaluator owns the mutation door for the
    /// library's own cases and never calls this for one; what reaches here is a document project's registered
    /// effect, which bridges to its own facet-typed firing, or a library case whose kernel lives outside this
    /// project.</summary>
    /// <param name="host">The mutation door, which is also the reader every operand answers from.</param>
    /// <param name="firing">The evaluation the arm fires under.</param>
    /// <param name="refusal">Why the arm did not fire, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the arm fired.</returns>
    bool TryFire(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal);
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
    /// <inheritdoc/>
    public abstract long Cost(IRuleCostContext context);
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
public readonly record struct CompiledCellRef(int RowOrdinal, CellKey Key, BoundKey Binding = BoundKey.None, IRuleKey? Custom = null, BoundKey InnerKeyBinding = BoundKey.None, CellKind Kind = CellKind.Int) {
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
}
