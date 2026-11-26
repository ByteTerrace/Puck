namespace Puck.World;

/// <summary>Everything about a replica recipient that decides what its stream is composed of, taken from its view and
/// nothing else (<see cref="WorldRecipientView.DisclosureClass"/>): the store it reads, the audience the store's
/// disclosure table keys it by (its ceiling, whether it operates, and its answer to every policy the store holds), and
/// its body disclosure. Two views of one authority whose classes are equal are disclosed the same document, rows,
/// cells, bodies, solid sizes and cues, so a stream composed for one is a stream composed for the other.</summary>
/// <remarks>Only <see cref="WorldRecipientView.DisclosureClass"/> makes one, from the view's own answers at the moment
/// it is asked, so no caller can name a class a view does not stand in. A class is compared, never kept as a
/// permission: a stream that shares another's image asks its view again at every delivery.</remarks>
public sealed class WorldDisclosureClass : IEquatable<WorldDisclosureClass> {
    private readonly WorldDisclosureImages m_store;
    private readonly string m_audience;
    private readonly WorldSinkDisclosure m_bodies;

    internal WorldDisclosureClass(WorldDisclosureImages store, string audience, WorldSinkDisclosure bodies) {
        m_store = store;
        m_audience = audience;
        m_bodies = bodies;
    }

    // Whether this class is the one these answers make.
    internal bool Holds(WorldDisclosureImages store, string audience, WorldSinkDisclosure bodies) => ReferenceEquals(objA: m_store, objB: store) &&
        string.Equals(a: m_audience, b: audience, comparisonType: StringComparison.Ordinal) && (m_bodies == bodies);

    /// <inheritdoc/>
    public bool Equals(WorldDisclosureClass? other) => (other is not null) && other.Holds(store: m_store, audience: m_audience, bodies: m_bodies);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as WorldDisclosureClass));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o: m_store),
        StringComparer.Ordinal.GetHashCode(obj: m_audience), m_bodies);
}
