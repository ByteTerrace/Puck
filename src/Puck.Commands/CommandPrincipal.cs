namespace Puck.Commands;

/// <summary>
/// The acting identity a dispatched command carries — stamped HOST-BOUND at the ingress door that produced it and
/// read (never constructed) by handlers. Zero-alloc, equatable, and hashable: a <see cref="Seat"/>/<see cref="Peer"/>
/// carries its index (name null), a <see cref="Peer"/> also carries its admission generation, an <see cref="Addon"/>
/// its name (index 0), and <see cref="Console"/> neither.
/// </summary>
/// <remarks>
/// This is the ingress layer's own principal shape, deliberately mirroring the DATA a host principal carries rather
/// than referencing the host's type: <c>Puck.Commands</c> sits below every world, and a principal that only the world
/// could name could not be stamped at the router. A host maps between the two with an exhaustive switch at one seam.
/// <para>The default value is <see cref="CommandPrincipalKind.Unspecified"/> — a principal that no door stamped. It is
/// never a legitimate acting identity; a handler reading one has found an ingress path that skipped its door.</para>
/// </remarks>
/// <param name="Kind">The kind of actor.</param>
/// <param name="Index">The 0-based slot/entity index for <see cref="CommandPrincipalKind.Seat"/>/<see cref="CommandPrincipalKind.Peer"/>;
/// zero otherwise.</param>
/// <param name="Name">The descriptor name for <see cref="CommandPrincipalKind.Addon"/>; <see langword="null"/> otherwise.</param>
/// <param name="Generation">The admission generation for <see cref="CommandPrincipalKind.Peer"/>; zero otherwise.</param>
public readonly record struct CommandPrincipal(CommandPrincipalKind Kind, int Index, string? Name, int Generation) {
    /// <summary>The console/script control surface — the identity the text submission door stamps.</summary>
    public static CommandPrincipal Console { get; } = new(
        Kind: CommandPrincipalKind.Console,
        Index: 0,
        Name: null,
        Generation: 0
    );

    /// <summary>Whether this principal was stamped by a door at all.</summary>
    public bool IsStamped => (Kind != CommandPrincipalKind.Unspecified);

    /// <summary>The seat principal for a 0-based slot.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The seat principal.</returns>
    /// <remarks>A mixer must NOT call this to attribute a slot's input: a claimed slot may be answering to a peer or a
    /// guest module, so synthesizing a seat there attributes the claimant's action to the seat it displaced. Ask
    /// <see cref="ICommandPrincipalResolver.PrincipalOf"/> instead — the host's roster owns that answer.</remarks>
    public static CommandPrincipal Seat(int slot) => new(
        Kind: CommandPrincipalKind.Seat,
        Index: slot,
        Name: null,
        Generation: 0
    );

    /// <summary>The guest-module principal for a descriptor name.</summary>
    /// <param name="name">The module's descriptor name.</param>
    /// <returns>The addon principal.</returns>
    public static CommandPrincipal Addon(string name) => new(
        Kind: CommandPrincipalKind.Addon,
        Index: 0,
        Name: name,
        Generation: 0
    );

    /// <summary>The peer principal for a 0-based entity index.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The peer principal.</returns>
    /// <param name="generation">The positive admission generation.</param>
    public static CommandPrincipal Peer(int index, int generation) => new(
        Kind: CommandPrincipalKind.Peer,
        Index: index,
        Name: null,
        Generation: generation
    );

    /// <summary>A short stable label for echoes — <c>seat1</c>…, <c>console</c>, <c>addon:&lt;name&gt;</c>,
    /// <c>peer:&lt;n&gt;:&lt;generation&gt;</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        CommandPrincipalKind.Seat => $"seat{(Index + 1)}",
        CommandPrincipalKind.Console => "console",
        CommandPrincipalKind.Addon => $"addon:{Name}",
        CommandPrincipalKind.Peer => $"peer:{Index}:{Generation}",
        _ => "unstamped",
    };
}
