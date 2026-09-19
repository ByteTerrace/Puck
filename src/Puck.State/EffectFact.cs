namespace Puck.State;

/// <summary>The operation a write asks of one state cell.</summary>
public enum StateWriteKind : byte {
    /// <summary>Replace the cell.</summary>
    Set,
    /// <summary>Add the operand to the cell.</summary>
    Add,
}
/// <summary>One compiled effect whose firing needs a host facet. The facet is the type argument, so the evaluator
/// resolves it from the host once and hands it to <see cref="TryFire"/>; an effect reaching a capability it did not
/// name in its type does not compile.</summary>
/// <typeparam name="TFacet">The facet firing needs.</typeparam>
/// <remarks>The facet comes from the type argument and never from <see cref="Needs"/>, which declares only what
/// firing does to the world: whether it reads the tick, and whether it can be rewound.</remarks>
public abstract class EffectFact<TFacet> : IEffectNeeds, IFacetFact, ICompiledFact where TFacet : IFacet {
    /// <summary>Initializes the effect with its read-back spelling.</summary>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    protected EffectFact(string describe) => Describe = describe;

    /// <summary>Gets the authored spelling, for the rules read-back.</summary>
    public string Describe { get; }
    /// <inheritdoc/>
    public FacetRef Facet => FacetRef.Of<TFacet>();

    /// <inheritdoc/>
    FacetRef? ICompiledFact.RequiredFacet => Facet;

    /// <inheritdoc/>
    public virtual EffectNeeds Needs => EffectNeeds.None;
    /// <summary>Gets a value indicating whether firing submits a state mutation — an effect that only emits
    /// (a cue, a pose, a save) reads back as emitted rather than as a skipped write.</summary>
    public virtual bool SubmitsMutation => true;

    /// <summary>Appends every state cell firing reads: copy sources, expression operands, and the cells its key
    /// indirections resolve through.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <summary>Appends every state cell firing writes.</summary>
    /// <param name="into">The write set being collected.</param>
    public virtual void CollectWrites(List<CellAccess> into) { }
    /// <summary>Returns the conservative work units one firing costs.</summary>
    /// <param name="context">The compile context the effect was resolved against.</param>
    /// <returns>The work units.</returns>
    public abstract long Cost(IRuleCostContext context);
    /// <summary>Fires the effect for the evaluation in flight.</summary>
    /// <param name="host">The host, which is also the reader every operand answers from and the mutation door an
    /// arena write installs through.</param>
    /// <param name="facet">The facet the evaluator resolved from the host.</param>
    /// <param name="firing">The evaluation the effect fires under. An effect declaring
    /// <see cref="EffectNeeds.Irreversible"/> is called once with <see cref="EffectFiring.Preflight"/> set, against
    /// the state the firing proposes to commit, and once more after the commit.</param>
    /// <param name="refusal">Why the effect did not fire, or <see cref="EffectRefusal.None"/>.</param>
    /// <returns><see langword="true"/> when the effect fired.</returns>
    public abstract bool TryFire(IEffectHost host, TFacet facet, in EffectFiring firing, out EffectRefusal refusal);
}
