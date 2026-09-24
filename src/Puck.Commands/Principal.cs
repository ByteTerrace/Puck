namespace Puck.Commands;

/// <summary>
/// Who is acting: the one identity a dispatched command, a submission, a mutation, and a replayed intent carry — a
/// seat, the console/script surface, an addon, a network/population peer, or the world's own authored program.
/// Zero-alloc, equatable, and hashable: a <see cref="Seat"/>/<see cref="Peer"/> carries its index (name null), a
/// <see cref="Peer"/> also carries its admission generation, an <see cref="Addon"/> its name (index 0), and
/// <see cref="Console"/>/<see cref="World"/> neither.
/// </summary>
/// <remarks>
/// <para><b>Only an ingress door mints a principal.</b> The text submission door stamps <see cref="Console"/>, the
/// snapshot mixer stamps the lane's resolved identity (<see cref="IPrincipalResolver"/>), an injection sink stamps the
/// identity it was constructed with, and a host's own transport and admission doors stamp the identity they
/// authenticated. A handler reads <see cref="CommandContext.Principal"/> and never constructs one: constructing a
/// principal to attribute an action is laundering an identity.</para>
/// <para>The default value is <see cref="PrincipalKind.Unspecified"/> — a principal that no door stamped. It is never a
/// legitimate acting identity; a handler reading one has found an ingress path that skipped its door.</para>
/// <para>A principal is only ever an actor. What a grant may name beyond an actor — a group, another document — is a
/// separate type in the world schema (<c>Puck.World.Protocol.Grantee</c>), so no value of this type can be a group or
/// a document.</para>
/// </remarks>
/// <param name="Kind">The kind of actor.</param>
/// <param name="Index">The 0-based slot/entity index for <see cref="PrincipalKind.Seat"/>/<see cref="PrincipalKind.Peer"/>;
/// zero otherwise.</param>
/// <param name="Name">The extension identity name for <see cref="PrincipalKind.Addon"/>; <see langword="null"/> otherwise.</param>
/// <param name="Generation">The admission generation for <see cref="PrincipalKind.Peer"/>; zero otherwise.</param>
public readonly record struct Principal(PrincipalKind Kind, int Index, string? Name, int Generation) {
    /// <summary>Gets the console/script control surface — the identity the text submission door stamps.</summary>
    public static Principal Console { get; } = new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.Console,
        Name: null
    );
    /// <summary>
    /// Gets the world's own authored program — the singleton identity every effect fired by a world rule or by a kit's
    /// generate effect carries.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a principal and not a borrowed one.</b> An entity driven by an authored program has no
    /// submitter, so entity-to-entity effects are authorised by what the world's own programs declare. Those effects
    /// write the document, which has a door, so the authored program needs a name at that door. Stamping
    /// <see cref="Console"/> (or the firing seat) would be laundering: Console is a real actor with real rows, and a
    /// seat never composed the effect — the document did. The seat's input only selects which authored effect runs;
    /// the generator, the destination row, and the value are all document data no seat can choose.</para>
    /// <para><b>What it costs.</b> The exemption is structural — the world's mutation admission returns admitted for
    /// this kind before consulting the grant table, so it can never be narrowed by a grant. The only way to change
    /// what the world's program does is to change the document, and authoring a rule or a kit is itself gated. Every
    /// non-authority gate still runs.</para>
    /// <para><b>Consequences of holding no rows.</b> It is refused at the grant door (a row naming it would be
    /// accepted and inert) and refused on the wire: nothing off-process can claim to be the world. Its label
    /// <c>world</c> parses, because a read-back that cannot name a principal cannot answer for it.</para>
    /// </remarks>
    public static Principal World { get; } = new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.World,
        Name: null
    );

    /// <summary>Gets whether this principal was stamped by a door at all.</summary>
    public bool IsStamped => (Kind != PrincipalKind.Unspecified);

    /// <summary>Returns the named extension principal used by WASM descriptors and host-composed providers.</summary>
    /// <param name="name">The extension's identity name.</param>
    /// <returns>The addon principal.</returns>
    public static Principal Addon(string name) => new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.Addon,
        Name: name
    );
    /// <summary>Describes a short stable label for echoes — <c>seat1</c>…, <c>console</c>, <c>world</c>,
    /// <c>addon:&lt;name&gt;</c>, <c>peer:&lt;n&gt;:&lt;generation&gt;</c>, or <c>unstamped</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        PrincipalKind.Seat => $"seat{(Index + 1)}",
        PrincipalKind.Console => "console",
        PrincipalKind.Addon => $"addon:{Name}",
        PrincipalKind.Peer => $"peer:{Index}:{Generation}",
        PrincipalKind.World => "world",
        _ => "unstamped",
    };
    /// <summary>Returns the peer principal for a 0-based population index.</summary>
    /// <param name="index">The 0-based population entity index.</param>
    /// <param name="generation">The positive admission generation.</param>
    /// <returns>The peer principal.</returns>
    public static Principal Peer(int index, int generation) => new(
        Generation: generation,
        Index: index,
        Kind: PrincipalKind.Peer,
        Name: null
    );
    /// <summary>Returns the seat principal for a 0-based slot.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The seat principal.</returns>
    /// <remarks>Do not call this to attribute an action reached for a slot: a claimed slot may be answering to a peer
    /// or a guest module, so synthesizing a seat there attributes the claimant's action to the seat it displaced. Ask
    /// <see cref="IPrincipalResolver.PrincipalOf"/> instead — the host's roster owns that answer. The legitimate direct
    /// callers are narrow and each says why at the call site: the roster's own fallback and session-lifecycle mirrors,
    /// the grant table's boot-time seed, an explicit <c>seatN</c> token, and the offline replay rehydrator.</remarks>
    public static Principal Seat(int slot) => new(
        Generation: 0,
        Index: slot,
        Kind: PrincipalKind.Seat,
        Name: null
    );
}
