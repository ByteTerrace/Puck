namespace Puck.State.Rules;

/// <summary>A typed host fact exposed through the rule operand protocol. Admission proves the reader serves the
/// declared facet; this is the single bridge that hands that facet and resolved addresses to a host operand.</summary>
/// <typeparam name="TFacet">The host capability the operand reads.</typeparam>
public abstract class FacetRuleOperand<TFacet> : OperandFact<TFacet>, IRuleOperand where TFacet : IFacet {
    /// <summary>Initializes the operand's value encoding.</summary>
    /// <param name="valueKind">The value encoding.</param>
    protected FacetRuleOperand(CellKind valueKind) : base(valueKind: valueKind) { }

    /// <inheritdoc/>
    public virtual ReadOnlySpan<OperandAddress> Addresses => [];

    /// <summary>Reads through the addresses the interpreter resolved. Address-free operands implement the
    /// inherited two-argument read.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="facet">The declared host capability.</param>
    /// <param name="keys">The resolved addresses.</param>
    /// <returns>The live fact.</returns>
    public virtual Fact Read(IStateReader reader, TFacet facet, ReadOnlySpan<ResolvedKey> keys) => Read(reader: reader, facet: facet);

    Fact IRuleOperand.Read(IStateReader reader, ReadOnlySpan<ResolvedKey> keys) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return Read(reader: reader, facet: ((TFacet)reader), keys: keys);
    }
}
