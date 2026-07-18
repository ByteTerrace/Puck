namespace Puck.World.Protocol;

/// <summary>What kind of actor a <see cref="WorldPrincipal"/> stands for — the one named primitive the three former
/// ad-hoc ownership forms (the engagement latch, machine-input ownership, the addon slot owner) collapse into. A
/// principal acts through <see cref="IServerLink"/>; the server checks its grants (see
/// <see cref="Puck.World.Server.WorldGrants"/>) before a write applies.</summary>
internal enum PrincipalKind : byte {
    /// <summary>A local roster seat — <see cref="WorldPrincipal.Index"/> is its 0-based slot (0..3).</summary>
    Seat,

    /// <summary>The stdin/console/script control surface — the one non-seat local authority the <c>player.*</c>,
    /// <c>world.*</c>, and mutation verbs act as.</summary>
    Console,

    /// <summary>A WASM addon — <see cref="WorldPrincipal.Name"/> is its descriptor name. A non-human principal that
    /// drives a body through the same protocol as a seat.</summary>
    Addon,

    /// <summary>A network/population body — <see cref="WorldPrincipal.Index"/> is its 0-based entity index (4..127).
    /// The engagement route of a population entry rides this identity; the socket arc reuses it for remote clients.</summary>
    Peer,
}

/// <summary>
/// The acting identity every <see cref="IServerLink"/> write submission carries — a seat, the console/script surface,
/// an addon, or a network/population peer. Zero-alloc, equatable, and hashable (a value key into the server grant
/// table): a <see cref="Seat"/>/<see cref="Peer"/> carries its index (name null), an <see cref="Addon"/> its name
/// (index 0), and <see cref="Console"/> neither.
/// </summary>
/// <param name="Kind">The kind of actor.</param>
/// <param name="Index">The 0-based slot/entity index for <see cref="PrincipalKind.Seat"/>/<see cref="PrincipalKind.Peer"/>;
/// zero otherwise.</param>
/// <param name="Name">The addon descriptor name for <see cref="PrincipalKind.Addon"/>; <see langword="null"/> otherwise.</param>
internal readonly record struct WorldPrincipal(PrincipalKind Kind, int Index, string? Name) {
    /// <summary>The console/script control surface.</summary>
    public static WorldPrincipal Console { get; } = new(Kind: PrincipalKind.Console, Index: 0, Name: null);

    /// <summary>The seat principal for a 0-based slot.</summary>
    /// <param name="slot">The 0-based seat slot (0..3).</param>
    public static WorldPrincipal Seat(int slot) => new(Kind: PrincipalKind.Seat, Index: slot, Name: null);

    /// <summary>The addon principal for a descriptor name.</summary>
    /// <param name="name">The addon descriptor name.</param>
    public static WorldPrincipal Addon(string name) => new(Kind: PrincipalKind.Addon, Index: 0, Name: name);

    /// <summary>The peer principal for a 0-based entity index (4..127).</summary>
    /// <param name="index">The 0-based population entity index.</param>
    public static WorldPrincipal Peer(int index) => new(Kind: PrincipalKind.Peer, Index: index, Name: null);

    /// <summary>A short stable label for console echoes — <c>seat1</c>..<c>seat4</c>, <c>console</c>, <c>addon:&lt;name&gt;</c>,
    /// <c>peer&lt;n&gt;</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        PrincipalKind.Seat => $"seat{Index + 1}",
        PrincipalKind.Console => "console",
        PrincipalKind.Addon => $"addon:{Name}",
        PrincipalKind.Peer => $"peer{Index}",
        _ => "?",
    };
}
