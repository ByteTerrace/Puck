namespace Puck.State;

/// <summary>The marker every host capability carries. A facet is an interface a document project declares and a
/// host implements: the reads only that host can answer (bodies, regions, clocks) and the rows its own storage
/// serves. A compiled fact names the facet it reads in its own type — <see cref="OperandFact{TFacet}"/>,
/// <see cref="EffectFact{TFacet}"/>, <see cref="KeyFact{TFacet}"/> — so the facet instance arrives as an argument
/// and no fact can reach a capability it did not declare.</summary>
/// <remarks><see cref="IStateReader"/> carries no accessor that hands out a facet by type; the only door is the
/// typed argument a fact's base passes it, and the only question a reader answers about facets is
/// <see cref="IStateReader.Advertises{TFacet}"/>.</remarks>
public interface IFacet;
/// <summary>One facet named as a value: what a compiled rule's <see cref="RuleNeeds"/> lists and what
/// <see cref="RuleNeeds.Admit"/> asks a host for. Minted once per facet type by <see cref="Of{TFacet}"/> and
/// compared by reference, so two references to the same facet are the same instance.</summary>
/// <remarks>The probe is a typed call into <see cref="IStateReader.Advertises{TFacet}"/> closed over the facet's
/// own type argument, so admission asks the same question a fact's declaration asks and nothing reflects over a
/// type at admission time.</remarks>
public sealed class FacetRef {
    private readonly Func<IStateReader, bool> m_probe;

    private FacetRef(Type facet, Func<IStateReader, bool> probe) {
        Facet = facet;
        m_probe = probe;
    }

    /// <summary>Gets the facet's interface type.</summary>
    public Type Facet { get; }
    /// <summary>Gets the facet's name, which a refusal quotes.</summary>
    public string Name => Facet.Name;

    /// <summary>Returns the one reference for a facet type.</summary>
    /// <typeparam name="TFacet">The facet.</typeparam>
    /// <returns>The reference.</returns>
    public static FacetRef Of<TFacet>() where TFacet : IFacet => Cache<TFacet>.Instance;
    /// <summary>Returns whether a host serves this facet.</summary>
    /// <param name="reader">The host's reader.</param>
    /// <returns><see langword="true"/> when the host advertises the facet.</returns>
    public bool IsAdvertisedBy(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return m_probe(arg: reader);
    }
    /// <inheritdoc/>
    public override string ToString() => Name;

    private static class Cache<TFacet> where TFacet : IFacet {
        internal static readonly FacetRef Instance = new(
            facet: typeof(TFacet),
            probe: static reader => reader.Advertises<TFacet>()
        );
    }
}
/// <summary>The facet a compiled fact's type declares. Implemented only by the typed fact bases, each of which
/// answers from its own type argument, so a fact author has no member to fill in and a rule's
/// <see cref="RuleNeeds"/> is a reading of the declaration rather than a self-report.</summary>
public interface IFacetFact {
    /// <summary>Gets the facet the fact's type argument names.</summary>
    FacetRef Facet { get; }
}
/// <summary>What one compiled effect's kind needs beyond reading state, over and above the facet its type names.</summary>
[Flags]
public enum EffectNeeds : byte {
    /// <summary>Firing reads state and writes state, and nothing else.</summary>
    None = 0,

    /// <summary>Firing reads the tick pair, so a row-version comparison alone cannot prove its outcome unchanged.</summary>
    ReadsTick = 1,

    /// <summary>Firing cannot be rewound — a save, a HUD or placement upsert, anything that leaves the arena — so
    /// the arm is queued during a firing's scope and fired after the scope commits.</summary>
    Irreversible = 2,
}
/// <summary>What one compiled effect's kind declares about firing. Implemented by the typed effect base, and read
/// by <see cref="RuleNeedsBuilder"/> when it folds a rule's effects into its <see cref="RuleNeeds"/>.</summary>
public interface IEffectNeeds {
    /// <summary>Gets what firing needs.</summary>
    EffectNeeds Needs { get; }
}
