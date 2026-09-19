namespace Puck.State;

/// <summary>What one compiled rule needs from the host that runs it: the facets its facts' types name, whether it
/// reads the tick, the host-owned rows it reads, whether any of its effects is irreversible, and whether a row
/// version comparison alone can prove its verdict unchanged.</summary>
/// <remarks>Every field is read off the rule's own declarations by <see cref="RuleNeedsBuilder"/> — a facet from a
/// fact's type argument, the rest from the effect kinds — so no member a fact author fills in can widen or narrow
/// it. <see cref="Facets"/> keeps the order the walk added them, which is a function of the rule alone, so the
/// facet <see cref="Admit"/> names first is the same on every run.</remarks>
public sealed class RuleNeeds {
    private readonly FacetRef[] m_facets;
    private readonly int[] m_hostOwnedRows;

    internal RuleNeeds(FacetRef[] facets, int[] hostOwnedRows, bool irreversible, bool readsTick, bool isVolatile) {
        Irreversible = irreversible;
        ReadsTick = readsTick;
        Volatile = isVolatile;
        m_facets = facets;
        m_hostOwnedRows = hostOwnedRows;
    }

    /// <summary>Gets the facets the rule's facts declare, in walk order, each once.</summary>
    public IReadOnlyList<FacetRef> Facets => m_facets;
    /// <summary>Gets the catalog ordinals of the host-owned rows the rule reads, ascending, each once.</summary>
    public IReadOnlyList<int> HostOwnedRows => m_hostOwnedRows;
    /// <summary>Gets a value indicating whether any of the rule's effects cannot be rewound, so its arm is queued
    /// during a firing's scope and fired after the scope commits.</summary>
    public bool Irreversible { get; }
    /// <summary>Gets the needs of a rule that reads state and nothing else.</summary>
    public static RuleNeeds None { get; } = new(
        facets: [],
        hostOwnedRows: [],
        irreversible: false,
        isVolatile: false,
        readsTick: false
    );
    /// <summary>Gets a value indicating whether the rule reads the tick pair, which changes every tick with no
    /// state write.</summary>
    public bool ReadsTick { get; }
    /// <summary>Gets a value indicating whether row versions alone cannot prove the rule's reads unchanged.</summary>
    public bool Volatile { get; }

    /// <summary>Returns whether a host serves everything a rule needs, refusing by facet name when it does not.</summary>
    /// <param name="needs">The rule's needs.</param>
    /// <param name="reader">The host's reader.</param>
    /// <param name="refusal">The refusal, naming the first facet the host does not advertise; empty on admission.</param>
    /// <returns><see langword="true"/> when the host advertises every facet the rule names.</returns>
    public static bool Admit(RuleNeeds needs, IStateReader reader, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: needs);
        ArgumentNullException.ThrowIfNull(argument: reader);

        foreach (var facet in needs.m_facets) {
            if (!facet.IsAdvertisedBy(reader: reader)) {
                refusal = $"the host does not serve the facet '{facet.Name}'";

                return false;
            }
        }

        refusal = string.Empty;

        return true;
    }
}
/// <summary>Folds one rule's declarations into its <see cref="RuleNeeds"/>: a facet per fact whose type names one,
/// the tick and irreversibility of each effect kind, and the host-owned rows the reads touch.</summary>
/// <remarks>A builder accumulates one rule and is reset by <see cref="Clear"/> between rules. It is compile-time
/// machinery: nothing on it runs on the tick path.</remarks>
public sealed class RuleNeedsBuilder {
    private readonly List<FacetRef> m_facets = [];
    private readonly List<int> m_hostOwnedRows = [];

    private bool m_irreversible;
    private bool m_readsTick;
    private bool m_volatile;

    /// <summary>Records a facet the rule needs.</summary>
    /// <param name="facet">The facet.</param>
    public void Add(FacetRef facet) {
        ArgumentNullException.ThrowIfNull(argument: facet);

        if (!m_facets.Contains(item: facet)) {
            m_facets.Add(item: facet);
        }
    }
    /// <summary>Records what one effect kind declares about firing.</summary>
    /// <param name="needs">The effect's needs.</param>
    public void AddEffectNeeds(EffectNeeds needs) {
        m_irreversible |= needs.HasFlag(flag: EffectNeeds.Irreversible);
        m_readsTick |= needs.HasFlag(flag: EffectNeeds.ReadsTick);
    }
    /// <summary>Records a fact of the rule: its facet when its type names one, and nothing when it does not.</summary>
    /// <param name="fact">The compiled fact.</param>
    public void AddFact(ICompiledFact? fact) {
        if (fact is null) {
            return;
        }
        if (fact.RequiredFacet is { } declared) {
            Add(facet: declared);
        }
        if (fact is IEffectNeeds effect) {
            AddEffectNeeds(needs: effect.Needs);
        }
    }
    /// <summary>Records a host-owned row the rule reads.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    public void AddHostOwnedRow(int rowOrdinal) {
        if (!m_hostOwnedRows.Contains(item: rowOrdinal)) {
            m_hostOwnedRows.Add(item: rowOrdinal);
        }
    }
    /// <summary>Returns the accumulated needs.</summary>
    /// <returns>The needs.</returns>
    public RuleNeeds Build() {
        var hostOwnedRows = m_hostOwnedRows.ToArray();

        Array.Sort(array: hostOwnedRows);

        // A facet read and a host-owned row both answer from storage no row version covers, so either one makes the
        // rule's verdict unprovable from versions alone.
        return new RuleNeeds(
            facets: [.. m_facets],
            hostOwnedRows: hostOwnedRows,
            irreversible: m_irreversible,
            isVolatile: (m_volatile || m_readsTick || (m_facets.Count > 0) || (hostOwnedRows.Length > 0)),
            readsTick: m_readsTick
        );
    }
    /// <summary>Resets the builder for the next rule.</summary>
    public void Clear() {
        m_facets.Clear();
        m_hostOwnedRows.Clear();

        m_irreversible = false;
        m_readsTick = false;
        m_volatile = false;
    }
    /// <summary>Records that the rule reads the tick pair.</summary>
    public void MarkReadsTick() => m_readsTick = true;
    /// <summary>Records that row versions alone cannot prove the rule's reads unchanged for a reason outside the
    /// facts — an unresolved row name, or a row carrying a value-over-time trait.</summary>
    public void MarkVolatile() => m_volatile = true;
}
