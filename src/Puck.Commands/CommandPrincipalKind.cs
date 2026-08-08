namespace Puck.Commands;

/// <summary>
/// What kind of actor a <see cref="CommandPrincipal"/> stands for — the ingress layer's OWN principal discriminant.
/// A host that carries a richer principal notion (Puck.World's <c>WorldPrincipal</c>) maps onto this at its seam with
/// an exhaustive switch, never a cast: decoding one enum's ordinals as another's silently pins whatever values that
/// other enum happened to have when the mapping was written, so a reordering there would change what a stamped
/// principal MEANS without a line here changing.
/// </summary>
// Every member value below is pinned independently of any other enum: a host mapping onto it reads these names, and
// a persisted or transported principal reads these numbers. Changing one is a break, never a refactor.
public enum CommandPrincipalKind : byte {
    /// <summary>Not a principal — the value an unstamped <see cref="CommandPrincipal"/> carries. Always a defect at a
    /// dispatch door; never an identity. <c>0</c> is the absence of a principal, not a default one.</summary>
    Unspecified = 0,

    /// <summary>The stdin/console/script control surface — the one non-seat local authority the text submission door
    /// stamps.</summary>
    Console = 1,

    /// <summary>A local roster seat — <see cref="CommandPrincipal.Index"/> is its 0-based slot.</summary>
    Seat = 2,

    /// <summary>A hosted guest module — <see cref="CommandPrincipal.Name"/> is its descriptor name.</summary>
    Addon = 3,

    /// <summary>A network or population body — <see cref="CommandPrincipal.Index"/> is its 0-based entity index.</summary>
    Peer = 4,
}
