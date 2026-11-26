using Puck.Networking;

namespace Puck.World.Server;

/// <summary>Who stands behind one crossing operation, as the path it arrived by established it: a world in the same
/// process, or a federation connection's verified identity. A crossing door that is handed none treats its caller as
/// having proved nothing.</summary>
public sealed class WorldTransferCaller {
    private WorldTransferCaller(string authority, bool colocated, VerifiedSession? session) {
        Authority = authority;
        IsColocated = colocated;
        Session = session;
    }

    /// <summary>Gets the authority namespace the caller acts as.</summary>
    public string Authority { get; }
    /// <summary>Gets a value indicating whether the caller is a world in this process.</summary>
    public bool IsColocated { get; }
    /// <summary>Gets the session a federation connection's proof signed for, or <see langword="null"/>.</summary>
    public VerifiedSession? Session { get; }

    /// <summary>Returns the caller a world in this process is: the host that owns both worlds made the call, so its
    /// namespace is as established as a completed handshake's.</summary>
    /// <param name="source">The calling world.</param>
    /// <returns>The caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public static WorldTransferCaller Colocated(WorldServer source) {
        ArgumentNullException.ThrowIfNull(argument: source);
        return new(authority: source.AuthorityIdentity, colocated: true, session: null);
    }
    /// <summary>Returns the caller a federation connection is, from the identity its proof verified.</summary>
    /// <param name="identity">The connection's verified identity.</param>
    /// <returns>The caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public static WorldTransferCaller Authenticated(VerifiedPeerIdentity identity) {
        ArgumentNullException.ThrowIfNull(argument: identity);
        return new(authority: identity.Authority, colocated: false, session: identity.Session);
    }
}
/// <summary>What a caller is to a closed rewind group.</summary>
public enum WorldTransferParty : byte {
    /// <summary>A world of the group, restored together with this one.</summary>
    Member = 0,

    /// <summary>A player's session the host's issuer vouched for: admitted as seats that carry nothing the group must
    /// give back, and ended when the group is restored.</summary>
    Guest = 1,

    /// <summary>Anyone else. Every crossing operation is refused and writes nothing.</summary>
    Outside = 2,
}
/// <summary>A closed rewind group's boundary: the authorities that are restored together, and the issuer whose
/// players the group admits as guests. A world carries one from before its first admission, and it never changes
/// during an activation.</summary>
public sealed class WorldTransferBoundary {
    /// <summary>The refusal every crossing operation of an outside caller answers.</summary>
    public const string Refusal = "closed rewind group refuses an external transfer";

    private readonly Func<string, bool> m_contains;

    /// <summary>Initializes a new instance of the <see cref="WorldTransferBoundary"/> class.</summary>
    /// <param name="contains">Whether an authority namespace is a world of the group.</param>
    /// <param name="guestIssuer">The fingerprint of the issuer root whose vouched sessions are admitted as guests, or
    /// <see langword="null"/> for a group that admits none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contains"/> is <see langword="null"/>.</exception>
    public WorldTransferBoundary(Func<string, bool> contains, string? guestIssuer = null) {
        ArgumentNullException.ThrowIfNull(argument: contains);
        m_contains = contains;
        GuestIssuer = guestIssuer;
    }

    /// <summary>Gets the fingerprint of the issuer root whose vouched sessions are admitted as guests, or
    /// <see langword="null"/>.</summary>
    public string? GuestIssuer { get; }

    /// <summary>Returns whether an authority namespace is a world of the group.</summary>
    /// <param name="authority">The namespace.</param>
    /// <returns><see langword="true"/> for a world of the group.</returns>
    public bool Contains(string authority) => m_contains(arg: authority);
    /// <summary>Classifies the caller of a crossing operation that names <paramref name="sourceAuthority"/>. A member
    /// is a world of this process under a name the group holds; a guest is a federation connection whose proof signed
    /// for a session a chain under <see cref="GuestIssuer"/> vouched for. A name never decides it: a connection whose
    /// authenticated namespace spells a world's name is outside.</summary>
    /// <param name="caller">Who made the call, or <see langword="null"/> for a caller that proved nothing.</param>
    /// <param name="sourceAuthority">The source namespace the operation names.</param>
    /// <returns>The caller's party.</returns>
    public WorldTransferParty Classify(WorldTransferCaller? caller, string sourceAuthority) {
        if ((caller is null) || !string.Equals(a: caller.Authority, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
            return WorldTransferParty.Outside;
        }
        if (caller.IsColocated) {
            return (Contains(authority: sourceAuthority) ? WorldTransferParty.Member : WorldTransferParty.Outside);
        }
        return ((
            (GuestIssuer is { } issuer) &&
            (caller.Session is { Vouched: true } session) &&
            string.Equals(a: session.Domain, b: issuer, comparisonType: StringComparison.Ordinal) &&
            !Contains(authority: sourceAuthority)
        )
            ? WorldTransferParty.Guest
            : WorldTransferParty.Outside);
    }
    /// <summary>Returns why a guest's reservation is not one a closed group admits, or <see langword="null"/>: every
    /// member is a human seat with no companion, whose incarnation the guest's own session minted and which carries
    /// no title.</summary>
    /// <param name="request">The reservation.</param>
    /// <returns>The named refusal, or <see langword="null"/>.</returns>
    public static string? GuestReservationRefusal(WorldTransferReservationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        if (!request.PeerAdmission) {
            return "closed rewind group admits a player's party only through the peer door";
        }
        for (var index = 0; (index < (request.Members?.Count ?? 0)); index++) {
            var member = request.Members![index];

            if (member.Companion is not null) {
                return $"closed rewind group refuses traveler {(index + 1)}: it is a companion, and a player's party enters as its seats alone";
            }
            if (!member.LandsHuman) {
                return $"closed rewind group refuses traveler {(index + 1)}: it is not a player's seat ({member.Source}), and a player's party enters as its seats alone";
            }
            if (member.Mobility is not { } mobility) {
                return $"closed rewind group refuses traveler {(index + 1)}: it carries no stable mobility identity";
            }
            if (
                mobility.Incarnation.Titled ||
                !string.Equals(a: mobility.Incarnation.Authority, b: request.SourceAuthority, comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: mobility.DepartedFrom.Authority, b: request.SourceAuthority, comparisonType: StringComparison.Ordinal)
            ) {
                return $"closed rewind group refuses traveler {(index + 1)}: incarnation {mobility.Incarnation} was not minted by the session that offers it";
            }
        }
        return null;
    }
    /// <summary>Returns why a guest's commit is not one a closed group admits, or <see langword="null"/>: every member
    /// lands at the destination's own seat spawn and carries no action register, no continuum, no companion and no
    /// body to drive.</summary>
    /// <param name="members">The commit's members.</param>
    /// <returns>The named refusal, or <see langword="null"/>.</returns>
    public static string? GuestCommitRefusal(IReadOnlyList<WorldTransferCommitMember> members) {
        ArgumentNullException.ThrowIfNull(argument: members);
        for (var index = 0; (index < members.Count); index++) {
            var member = members[index];
            var carried = ((member.Arrival != WorldTransferArrival.Spawn)
                ? $"a {member.Arrival} arrival"
                : ((member.Companion is not null)
                    ? "a companion"
                    : ((member.Drives is not null)
                        ? "a body to drive"
                        : ((member.Continuum is not null)
                            ? "carried motion"
                            : ((member.ActionContinuity is { Registers.Count: > 0 })
                                ? "action registers"
                                : null)))));

            if (carried is not null) {
                return $"closed rewind group refuses traveler {(index + 1)}: it carries {carried}, and a player's seat lands at this world's own spawn carrying nothing";
            }
        }
        return null;
    }
}
/// <summary>The decision the crossing door made about one operation's caller. Every operation that can change a
/// world's crossing ledger runs behind one, and only <see cref="Of"/> and <see cref="Recorded"/> make one that admits
/// anything: the default value refuses.</summary>
public readonly struct WorldTransferAdmission {
    private const byte Unbounded = 1;
    private const byte Member = 2;
    private const byte Guest = 3;

    private readonly byte m_kind;

    private WorldTransferAdmission(byte kind) => m_kind = kind;

    /// <summary>Gets a value indicating whether the operation is refused: its caller is outside a closed group.</summary>
    public bool Refused => (m_kind == 0);
    /// <summary>Gets a value indicating whether the caller is a guest of a closed group.</summary>
    public bool IsGuest => (m_kind == Guest);

    /// <summary>Decides a live operation. A world with no boundary admits every caller, as an open world does.</summary>
    /// <param name="boundary">The world's boundary, or <see langword="null"/> for a world outside any closed group.</param>
    /// <param name="caller">Who made the call, or <see langword="null"/> for a caller that proved nothing.</param>
    /// <param name="sourceAuthority">The source namespace the operation names.</param>
    /// <returns>The admission.</returns>
    public static WorldTransferAdmission Of(WorldTransferBoundary? boundary, WorldTransferCaller? caller, string sourceAuthority) =>
        ((boundary is null)
            ? new(kind: Unbounded)
            : (boundary.Classify(caller: caller, sourceAuthority: sourceAuthority) switch {
                WorldTransferParty.Member => new(kind: Member),
                WorldTransferParty.Guest => new(kind: Guest),
                _ => default,
            }));
    /// <summary>Returns the admission of a decision already made live and recorded: a crossing-log record or a taped
    /// arrival is redone as what it was admitted as, a member's when its source is a world of the group and a guest's
    /// otherwise, and is never decided again.</summary>
    /// <param name="boundary">The world's boundary, or <see langword="null"/>.</param>
    /// <param name="sourceAuthority">The source namespace the record names.</param>
    /// <returns>The admission.</returns>
    public static WorldTransferAdmission Recorded(WorldTransferBoundary? boundary, string sourceAuthority) =>
        ((boundary is null)
            ? new(kind: Unbounded)
            : new(kind: (boundary.Contains(authority: sourceAuthority) ? Member : Guest)));
}
