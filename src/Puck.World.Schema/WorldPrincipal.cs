using System.Globalization;

namespace Puck.World.Protocol;

/// <summary>What kind of actor a <see cref="WorldPrincipal"/> stands for — the one named primitive that engagement
/// and machine-input ownership both reduce to. A
/// principal acts through <c>IServerLink</c>; the server checks its grants (see
/// <c>Puck.World.Server.WorldGrants</c>) before a write applies.</summary>
public enum PrincipalKind : byte {
    /// <summary>A local roster seat — <see cref="WorldPrincipal.Index"/> is its 0-based slot (0..3).</summary>
    Seat,

    /// <summary>The stdin/console/script control surface — the one non-seat local authority the <c>player.*</c>,
    /// <c>world.*</c>, and mutation verbs act as.</summary>
    Console,

    /// <summary>A WASM addon — <see cref="WorldPrincipal.Name"/> is its descriptor name. A non-human principal that
    /// reaches the world through typed capability channels alone (see <c>Server.WorldAddonRuntime</c>), never
    /// through a seat's input path.</summary>
    Addon,

    /// <summary>A network/population body — <see cref="WorldPrincipal.Index"/> is its 0-based entity index (4..127).
    /// The engagement route of a population entry rides this identity; a socket transport reuses it for remote clients.</summary>
    Peer,

    /// <summary>Another world document asking this document's authority to act.</summary>
    Document,

    /// <summary>The world's own authored program — see <see cref="WorldPrincipal.World"/>.</summary>
    World,

    /// <summary>A group — <see cref="WorldPrincipal.Name"/> is the group's stable id (see
    /// <c>Puck.World.WorldGroup.Id</c>). A grant naming a group as its <see cref="WorldGrant.Principal"/> is held by
    /// every current member at check time (<c>Server.WorldGrants.Allows</c>'s group-expansion step) — never baked into
    /// a member's own rows at grant time, so a member who leaves loses the hold on its very next check. A group never
    /// acts: nothing stamps this kind as an ingress's acting principal, so it never reaches
    /// <c>WorldPrincipalMapping</c> or the wire codec as a submitter — it exists only as a grant target and as
    /// a membership-row value.</summary>
    Group,
}
/// <summary>
/// The acting identity every <c>IServerLink</c> write submission carries — a seat, the console/script surface,
/// an addon, or a network/population peer. Zero-alloc, equatable, and hashable (a value key into the server grant
/// table): a <see cref="Seat"/>/<see cref="Peer"/> carries its index (name null), an <see cref="Addon"/> its name
/// (index 0), and <see cref="Console"/> neither.
/// </summary>
/// <param name="Kind">The kind of actor.</param>
/// <param name="Index">The 0-based slot/entity index for <see cref="PrincipalKind.Seat"/>/<see cref="PrincipalKind.Peer"/>;
/// zero otherwise.</param>
/// <param name="Name">The addon descriptor name for <see cref="PrincipalKind.Addon"/>; <see langword="null"/> otherwise.</param>
/// <param name="Generation">The admission generation for <see cref="PrincipalKind.Peer"/>; zero otherwise.</param>
public readonly record struct WorldPrincipal(PrincipalKind Kind, int Index, string? Name, int Generation) {
    /// <summary>Gets the console/script control surface.</summary>
    public static WorldPrincipal Console { get; } = new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.Console,
        Name: null
    );
    /// <summary>
    /// Gets the world's own authored program — the singleton identity every effect fired by a <see cref="WorldRule"/> or by
    /// a kit's <c>ActionEffect.Generate</c> carries.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a principal and not a borrowed one.</b> A per-body <c>ActionEffect</c> has always written
    /// without consulting <c>WorldGrants</c> at all — an entity driven by an authored program has no submitter, so
    /// entity-to-entity effects are authorised by what the world's own programs declare. A world rule is the same
    /// shape one level up. What changed is that these effects now write the document, which has a door; so the
    /// authored program needs a name at that door. Stamping <see cref="Console"/> (or the firing seat) would be
    /// laundering: Console is a real actor with real rows, and a seat never composed the effect — the document did.
    /// The seat's input only selects which authored effect runs; the generator, the destination row, and the value
    /// are all document data no seat can choose. So a seat-driven kit effect carries this principal too, and
    /// <c>world.why world edit state:&lt;row&gt;</c> answers for it honestly rather than answering about a human who
    /// pressed a button.</para>
    /// <para><b>What it costs.</b> The exemption is structural — <c>Server.WorldServer.TryAdmitMutation</c> returns
    /// admitted for this kind before consulting the table, so it can never be narrowed by a grant. That is deliberate
    /// and bounded: the only way to change what the world's program does is to change the document, and authoring a
    /// rule row (<c>Mutate/section:rules</c>) or a kit (<c>Mutate/section:kits</c>) is itself gated. Every non-authority
    /// gate still runs unconditionally — compose, whole-document validate, envelope, solids — so an authored program
    /// can never do what the document may not.</para>
    /// <para><b>Consequences of holding no rows.</b> It is refused at the grant door (a row naming it would be
    /// accepted-and-inert, the "phantom grant" shape the table exists to prevent) and refused on the wire
    /// (<c>WorldSubmissionCodec</c>): nothing off-process can claim to be the world. <see cref="TryParse"/> does
    /// accept the <c>world</c> token, because a read-back that cannot name a principal cannot answer for it — the
    /// token reaches diagnostics (<c>world.why</c>) and the grant door's own refusal, never an acting identity.</para>
    /// </remarks>
    public static WorldPrincipal World { get; } = new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.World,
        Name: null
    );

    /// <summary>Returns the addon principal for a descriptor name.</summary>
    /// <param name="name">The addon descriptor name.</param>
    public static WorldPrincipal Addon(string name) => new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.Addon,
        Name: name
    );
    /// <summary>Describes a short stable label for console echoes — <c>seat1</c>..<c>seat4</c>, <c>console</c>, <c>addon:&lt;name&gt;</c>,
    /// <c>peer:&lt;n&gt;:&lt;generation&gt;</c>, <c>group:&lt;id&gt;</c>.</summary>
    /// <returns>The label.</returns>
    public string Describe() => Kind switch {
        PrincipalKind.Seat => $"seat{(Index + 1)}",
        PrincipalKind.Console => "console",
        PrincipalKind.Addon => $"addon:{Name}",
        PrincipalKind.Peer => $"peer:{Index}:{Generation}",
        PrincipalKind.Document => $"document:{Name}",
        PrincipalKind.World => "world",
        PrincipalKind.Group => $"group:{Name}",
        _ => "?",
    };
    /// <summary>Creates a world document principal.</summary>
    public static WorldPrincipal Document(string id) => new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.Document,
        Name: id
    );
    /// <summary>Returns the group principal for a stable group id.</summary>
    /// <param name="id">The group's stable id.</param>
    public static WorldPrincipal Group(string id) => new(
        Generation: 0,
        Index: 0,
        Kind: PrincipalKind.Group,
        Name: id
    );
    /// <summary>Returns the peer principal for a 0-based entity index (4..127).</summary>
    /// <param name="index">The 0-based population entity index.</param>
    /// <param name="generation">The positive admission generation.</param>
    public static WorldPrincipal Peer(int index, int generation) => new(
        Generation: generation,
        Index: index,
        Kind: PrincipalKind.Peer,
        Name: null
    );
    /// <summary>Returns the seat principal for a 0-based slot. <b>Do not call this to attribute an action reached for a slot</b>
    /// (a drive-intent submission, an engagement route) — a slot can be claimed by something that is not a seat
    /// (<c>Puck.World.Client.PlayerRoster.TryClaimSlot</c>: the editor session today, a replay device or
    /// network peer stand-in tomorrow), so minting this inline attributes the claimant's action to the seat it
    /// displaced. A command handler never asks at all: it reads the identity its ingress door stamped, through
    /// <c>WorldPrincipalMapping</c>. Everything else asks
    /// <c>Puck.World.Client.PlayerRoster.PrincipalOf</c> for the slot's actual acting identity — it already
    /// falls back to this constructor when nothing claimed the slot. The legitimate direct callers are narrow and each
    /// says why at the call site: <c>Puck.World.Client.PlayerRoster.PrincipalOf</c>'s own fallback, the
    /// roster's session-lifecycle mirrors (join/leave/profile — not the write-boundary this doc warns about), the
    /// grant table's boot-time seed, an explicit <c>seatN</c> console token, and the offline replay rehydrator (no
    /// live roster to ask).</summary>
    /// <param name="slot">The 0-based seat slot (0..3).</param>
    public static WorldPrincipal Seat(int slot) => new(
        Generation: 0,
        Index: slot,
        Kind: PrincipalKind.Seat,
        Name: null
    );
    /// <summary>Parses a principal token (<c>seat1</c>..<c>seat4</c> | <c>console</c> | <c>addon:&lt;name&gt;</c> |
    /// <c>peer:&lt;n&gt;:&lt;generation&gt;</c>) — shared by <c>Puck.World.WorldPrincipalJsonConverter</c> and
    /// <c>Puck.World.WorldGrantCommandModule</c>'s <c>world.grant</c>/<c>world.revoke</c> console verbs, so a
    /// document-sourced principal (a <see cref="WorldGrant.Principal"/> row, an addon manifest's implicit
    /// self-reference) always canonicalizes through the identical grammar a console token does. There is no other
    /// way to construct a non-canonical <see cref="WorldPrincipal"/> from either surface.</summary>
    /// <param name="token">The token to parse.</param>
    /// <param name="principal">The parsed principal, on success.</param>
    /// <returns><see langword="true"/> when the token parsed.</returns>
    public static bool TryParse(ReadOnlySpan<char> token, out WorldPrincipal principal) {
        principal = Console;

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "console"
        )) {
            return true;
        }

        // The `world` token parses so a READ-BACK can name it (world.why must be able to answer for the world's own
        // program) and so the grant door can refuse a row for it BY NAME rather than by silently failing to parse.
        // It never becomes an acting identity: the wire refuses it, and no ingress stamps it.
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "world"
        )) {
            principal = World;

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "addon:"
        ) &&
            (token.Length > 6)
        ) {
            principal = Addon(name: token[6..].ToString());

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "seat"
        ) &&
            int.TryParse(
            s: token[4..],
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var seat
        ) &&
            (seat >= 1) &&
            (seat <= 4)
        ) {
            // Seat(slot) is right here: an operator's "seatN" token deliberately NAMES the seat identity as a
            // world.grant/world.revoke TARGET, regardless of who currently claims the slot.
            principal = Seat(slot: (seat - 1));

            return true;
        }

        if (token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "peer:"
        )) {
            var remainder = token[5..];
            var separator = remainder.IndexOf(value: ':');

            if (
                (separator <= 0) ||
                !int.TryParse(
                s: remainder[..separator],
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var peer
            ) ||
                !WorldPopulationLimits.IsPeerIndex(index: peer) ||
                !int.TryParse(
                s: remainder[(separator + 1)..],
                style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture,
                result: out var generation
            ) ||
                (generation <= 0)
            ) {
                return false;
            }

            principal = Peer(
                generation: generation,
                index: peer
            );

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "document:"
        ) &&
            (token.Length > 9)
        ) {
            principal = Document(id: token[9..].ToString());
            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "group:"
        ) &&
            (token.Length > 6)
        ) {
            principal = Group(id: token[6..].ToString());

            return true;
        }

        return false;
    }
}
