namespace Puck.State;

/// <summary>One resolved read operand whose read needs a host facet. The facet is the type argument, so the
/// evaluator resolves it from the host once and hands it to <see cref="Read"/>; a fact reaching a capability it did
/// not name in its type does not compile.</summary>
/// <typeparam name="TFacet">The facet the read needs.</typeparam>
/// <remarks>Case types are classes, never records or structs: nothing at runtime compares two operands for equality
/// or identity, so a generated structural <c>Equals</c> would be a hazard nobody asked for. A document project's
/// operand family registers the case; the facet is read off this base rather than declared on the case, so
/// <see cref="RuleNeeds"/> reads the declaration and never a self-report.</remarks>
public abstract class OperandFact<TFacet> : IFacetFact, ICompiledFact where TFacet : IFacet {
    /// <summary>Initializes the operand with the encoding its value is returned in.</summary>
    /// <param name="valueKind">The raw encoding this operand's value is returned in.</param>
    protected OperandFact(CellKind valueKind) => ValueKind = valueKind;

    /// <inheritdoc/>
    public FacetRef Facet => FacetRef.Of<TFacet>();

    /// <inheritdoc/>
    FacetRef? ICompiledFact.RequiredFacet => Facet;

    /// <summary>Gets the raw encoding this operand's value is returned in.</summary>
    public CellKind ValueKind { get; }

    /// <summary>Appends every state cell the read touches, including the cells its key indirections resolve through.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <summary>Returns the conservative work units one read costs — a state-candidate visit count for a read that
    /// scans, 1 for a direct read.</summary>
    /// <param name="context">The compile context the operand was resolved against.</param>
    /// <returns>The work units.</returns>
    public abstract long Cost(IRuleCostContext context);
    /// <summary>Reads the operand's live fact for the evaluation in flight.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="facet">The facet the evaluator resolved from the host.</param>
    /// <returns>The fact.</returns>
    public abstract RuleFact Read(IStateReader reader, TFacet facet);
}
/// <summary>A cell key whose resolution needs a host facet, handed to <see cref="Resolve"/> as the type argument
/// names it. The key is interned (<see cref="CellKey"/>), never a string.</summary>
/// <typeparam name="TFacet">The facet the resolution needs.</typeparam>
public abstract class KeyFact<TFacet> : IFacetFact, ICompiledFact where TFacet : IFacet {
    /// <inheritdoc/>
    public FacetRef Facet => FacetRef.Of<TFacet>();

    /// <inheritdoc/>
    FacetRef? ICompiledFact.RequiredFacet => Facet;

    /// <summary>Appends every state cell the resolution reads through.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<CellAccess> into) { }
    /// <summary>Resolves the key for the evaluation in flight. Allocation-free in steady state: a key the host
    /// mints once stays interned in the catalog's key table.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="facet">The facet the evaluator resolved from the host.</param>
    /// <returns>The interned key.</returns>
    public abstract CellKey Resolve(IStateReader reader, TFacet facet);
}
