namespace Puck.SdfVm;

/// <summary>
/// A host's pick map (<see cref="SdfFrame.PickMap"/>) extended over the draw list a dressed frame renders: the host's own
/// mesh draws come first, then one draw per baked group, then the carried copies of every draw a portal straddle clips.
/// A visibility identity's source indexes that list, so a draw the host never listed names what it draws through its
/// identity: a baked group's through the group's first instance, a copy's through the draw it was copied from.
/// </summary>
public sealed class SdfDressedPickMap : ISdfPickMap {
    private readonly ISdfPickMap m_inner;
    private readonly object[] m_identities;
    private readonly int m_hostDraws;

    /// <summary>Initializes a new instance of the <see cref="SdfDressedPickMap"/> class.</summary>
    /// <param name="inner">The host's map, indexed by the host's own draws.</param>
    /// <param name="hostDraws">The count of draws the host listed, which lead the dressed list.</param>
    /// <param name="draws">The dressed draw list, read once for its identities.</param>
    public SdfDressedPickMap(ISdfPickMap inner, int hostDraws, IReadOnlyList<SdfMeshDraw> draws) {
        m_inner = inner;
        m_hostDraws = hostDraws;
        m_identities = new object[draws.Count];
        for (var index = 0; (index < m_identities.Length); index++) {
            m_identities[index] = draws[index].Identity;
        }
    }

    /// <summary>Gets the host map this one extends.</summary>
    public ISdfPickMap Inner => m_inner;
    /// <summary>Gets the count of draws the host listed, which lead the dressed list.</summary>
    public int HostDraws => m_hostDraws;

    /// <summary>Returns whether a dressed draw list still has this map's identities, in order.</summary>
    /// <param name="draws">The dressed draw list.</param>
    /// <returns><see langword="true"/> when every draw's identity equals the one the map captured.</returns>
    public bool Matches(IReadOnlyList<SdfMeshDraw> draws) {
        if (draws.Count != m_identities.Length) { return false; }
        for (var index = 0; (index < m_identities.Length); index++) {
            if (!ReferenceEquals(objA: draws[index].Identity, objB: m_identities[index]) && !draws[index].Identity.Equals(obj: m_identities[index])) { return false; }
        }
        return true;
    }
    /// <inheritdoc/>
    public object? Resolve(uint identity) {
        if (SdfVisibility.KindOf(identity) == SdfVisibilityKind.Mesh) {
            var source = SdfVisibility.SourceOf(identity);

            if (source < m_identities.Length) {
                var named = (SdfSlotClip.CopiedFrom(m_identities[source]) ?? m_identities[source]);

                if (named is SdfMeshInstance copy) { return copy.Identity; }
            }
        }
        return m_inner.Resolve(identity: Translate(identity: identity));
    }
    /// <inheritdoc/>
    public string? MaterialName(uint identity, int material) => m_inner.MaterialName(identity: Translate(identity: identity), material: material);

    // The host-map identity of what a dressed identity names: unchanged for an instance or a host draw, the first
    // instance of a baked group's draw, or the host draw (or group) a copy was made of. Zero names nothing.
    private uint Translate(uint identity) {
        if ((SdfVisibility.KindOf(identity: identity) != SdfVisibilityKind.Mesh) || (SdfVisibility.SourceOf(identity: identity) < m_hostDraws)) {
            return identity;
        }

        var source = SdfVisibility.SourceOf(identity: identity);

        if (source >= m_identities.Length) { return 0; }

        var named = m_identities[source];

        if (SdfSlotClip.CopiedFrom(identity: named) is { } copied) { named = copied; }
        if (named is SdfMeshInstance copy) {
            return ((copy.FirstInstance < 0) ? 0 : SdfVisibility.IdentityOf(SdfVisibilityKind.Sdf, ((uint)(copy.FirstInstance + 1))));
        }
        if (named is SdfMeshBakeGroup group) {
            return ((group.FirstInstance < 0) ? 0 : SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: ((uint)(group.FirstInstance + 1))));
        }
        for (var index = 0; (index < m_hostDraws); index++) {
            if (named.Equals(obj: m_identities[index])) { return SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: ((uint)index)); }
        }
        return 0;
    }
}
