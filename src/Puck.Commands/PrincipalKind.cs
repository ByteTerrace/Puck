namespace Puck.Commands;

/// <summary>What kind of actor a <see cref="Principal"/> stands for.</summary>
/// <remarks>A codec maps each member to its own wire byte through an explicit table, never a cast, so inserting a
/// member here cannot silently change what a persisted or transported byte means.</remarks>
public enum PrincipalKind : byte {
    /// <summary>Not a principal — the value an unstamped <see cref="Principal"/> carries. Always a defect at a
    /// dispatch door; never an identity. <c>0</c> is the absence of a principal, not a default one.</summary>
    Unspecified = 0,

    /// <summary>The stdin/console/script control surface — the one non-seat local authority the text submission door
    /// stamps.</summary>
    Console = 1,

    /// <summary>A local roster seat — <see cref="Principal.Index"/> is its 0-based slot.</summary>
    Seat = 2,

    /// <summary>A named extension — <see cref="Principal.Name"/> identifies its WASM descriptor or host-composed
    /// provider. Its typed contributions cross the ordinary capability gates, never a seat's identity.</summary>
    Addon = 3,

    /// <summary>A network or population body — <see cref="Principal.Index"/> is its 0-based entity index and
    /// <see cref="Principal.Generation"/> its admission generation.</summary>
    Peer = 4,

    /// <summary>The world's own authored program — see <see cref="Principal.World"/>.</summary>
    World = 5,
}
