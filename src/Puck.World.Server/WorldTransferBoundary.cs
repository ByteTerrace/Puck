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
/// <summary>A closed rewind group's boundary: the authorities that are restored together, the issuer whose players the
/// group admits as guests, and how much of a world's crossing ledger those guests may hold. A world carries one from
/// before its first admission, and it never changes during an activation.</summary>
public sealed class WorldTransferBoundary {
    /// <summary>The refusal every crossing operation of an outside caller answers.</summary>
    public const string Refusal = "closed rewind group refuses an external transfer";

    private readonly Func<string, bool> m_contains;

    /// <summary>Initializes a new instance of the <see cref="WorldTransferBoundary"/> class.</summary>
    /// <param name="contains">Whether an authority namespace is a world of the group.</param>
    /// <param name="guestIssuer">The fingerprint of the issuer root whose vouched sessions are admitted as guests, or
    /// <see langword="null"/> for a group that admits none.</param>
    /// <param name="guestKeysPerSession">The ledger rows one guest session may hold in a world of the group.</param>
    /// <param name="guestKeys">The ledger rows every guest session together may hold in a world of the group.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contains"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="guestKeysPerSession"/> is below two (a key and
    /// its source's floor), or <paramref name="guestKeys"/> is below it.</exception>
    public WorldTransferBoundary(Func<string, bool> contains, string? guestIssuer = null, int guestKeysPerSession = WorldTransferLimits.GuestTransferKeysPerSession, int guestKeys = WorldTransferLimits.GuestTransferKeys) {
        ArgumentNullException.ThrowIfNull(argument: contains);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: guestKeysPerSession, other: 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: guestKeys, other: guestKeysPerSession);
        m_contains = contains;
        GuestIssuer = guestIssuer;
        GuestKeysPerSession = guestKeysPerSession;
        GuestKeys = guestKeys;
    }

    /// <summary>Gets the fingerprint of the issuer root whose vouched sessions are admitted as guests, or
    /// <see langword="null"/>.</summary>
    public string? GuestIssuer { get; }
    /// <summary>Gets the ledger rows one guest session may hold in a world of the group, its floor included.</summary>
    public int GuestKeysPerSession { get; }
    /// <summary>Gets the ledger rows every guest session together may hold in a world of the group. They are counted
    /// apart from the rows of the group's own worlds, whose bounds no guest's row counts against.</summary>
    public int GuestKeys { get; }

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
            (caller.Session is { Vouched: true, Subject.Length: > 0 } session) &&
            string.Equals(a: session.Domain, b: issuer, comparisonType: StringComparison.Ordinal) &&
            !Contains(authority: sourceAuthority)
        )
            ? WorldTransferParty.Guest
            : WorldTransferParty.Outside);
    }
    /// <summary>Returns why a federation connection may not hold a credential at a world of the group, or
    /// <see langword="null"/>: the group answers a network identity only when it is a guest's, a session vouched for
    /// under <see cref="GuestIssuer"/> whose namespace is no world of the group. Every federation door then decides
    /// for a guest alone, so a connection whose proved namespace spells a world of the group never reaches a door that
    /// reads the namespace.</summary>
    /// <param name="identity">The connection's verified identity.</param>
    /// <returns>The named refusal, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public string? CredentialRefusal(VerifiedPeerIdentity identity) {
        ArgumentNullException.ThrowIfNull(argument: identity);
        return ((Classify(caller: WorldTransferCaller.Authenticated(identity: identity), sourceAuthority: identity.Authority) == WorldTransferParty.Guest)
            ? null
            : "closed rewind group answers only a player's session its own issuer vouched for");
    }
    /// <summary>Shapes a guest's reservation into the one a closed group leases, or returns why it admits none. Every
    /// member is a seat a person holds (a live source, or an idle one a person set), with no companion, whose
    /// incarnation the guest's own session minted and which carries no title. What the guest states and the group
    /// decides for itself is replaced: the border is this world's own for an arrival that crosses none and carries no
    /// capacity, the body index is this world's to choose, and each identity lands as
    /// <see cref="GuestIdentity"/> leaves it.</summary>
    /// <param name="request">The reservation as the guest sent it.</param>
    /// <param name="subject">The subject the host's issuer vouched for.</param>
    /// <param name="shaped">The reservation the group leases, when it admits one.</param>
    /// <returns>The named refusal, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is empty.</exception>
    public static string? GuestReservationRefusal(WorldTransferReservationRequest request, string subject, out WorldTransferReservationRequest shaped) {
        ArgumentNullException.ThrowIfNull(argument: request);
        ArgumentException.ThrowIfNullOrEmpty(argument: subject);
        shaped = request;
        if (!request.PeerAdmission) {
            return "closed rewind group admits a player's party only through the peer door";
        }
        var count = (request.Members?.Count ?? 0);
        var members = new WorldTransferReservationMember[count];

        for (var index = 0; (index < count); index++) {
            var member = request.Members![index];

            if (member.Companion is not null) {
                return $"closed rewind group refuses traveler {(index + 1)}: it is a companion, and a player's party enters as its seats alone";
            }
            if (!member.LandsHuman || member.Source.IsProducer) {
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
            members[index] = member with {
                Identity = GuestIdentity(sent: member.Identity, subject: subject),
                PreferredSlot = -1,
            };
        }
        shaped = request with {
            Border = WorldTransferReservationRequest.Unbordered,
            BorderCapacity = null,
            Members = members,
        };
        return null;
    }
    /// <summary>Shapes a guest's commit into the one a closed group lands, or returns why it lands none. Every member
    /// lands at the destination's own seat spawn, with no arrival turn, no action register, no continuum, no companion
    /// and no body to drive, and its identity lands as <see cref="GuestIdentity"/> leaves it.</summary>
    /// <param name="members">The commit's members as the guest sent them.</param>
    /// <param name="subject">The subject the host's issuer vouched for.</param>
    /// <param name="shaped">The members the group lands, when it admits the commit.</param>
    /// <returns>The named refusal, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is empty.</exception>
    public static string? GuestCommitRefusal(IReadOnlyList<WorldTransferCommitMember> members, string subject, out IReadOnlyList<WorldTransferCommitMember> shaped) {
        ArgumentNullException.ThrowIfNull(argument: members);
        ArgumentException.ThrowIfNullOrEmpty(argument: subject);
        shaped = members;
        var landing = new WorldTransferCommitMember[members.Count];

        for (var index = 0; (index < members.Count); index++) {
            var member = members[index];

            if (GuestCarries(member: member) is { } carried) {
                return $"closed rewind group refuses traveler {(index + 1)}: it carries {carried}, and a player's seat lands at this world's own spawn carrying nothing";
            }
            landing[index] = member with { Profile = GuestIdentity(sent: member.Profile, subject: subject) };
        }
        shaped = landing;
        return null;
    }
    /// <summary>Returns the identity a guest lands with. Its id is the subject the host's issuer vouched for, so no
    /// guest lands under an id it chose and every session of one player lands under one. Its name and colour are the
    /// player's own, shown and never read as proof of who it is. Its speeds, facts and records are not carried: a
    /// world's rules read them, a guest could state any, and the group holds no copy to check them against, so the seat
    /// moves at this world's kit rates and starts from this world's defaults.</summary>
    /// <param name="sent">The identity projection the guest sent, or <see langword="null"/> for none.</param>
    /// <param name="subject">The subject the host's issuer vouched for.</param>
    /// <returns>The identity the guest lands with, or <see langword="null"/> when it sent none.</returns>
    public static WorldIdentityProjection? GuestIdentity(WorldIdentityProjection? sent, string subject) => ((sent is { } projection)
        ? new WorldIdentityProjection(ColorHex: projection.ColorHex, Id: subject, MoveSpeed: null, Name: projection.Name, TurnSpeed: null)
        : null);

    // What a commit member carries that a player's seat may not, or null, in the order a refusal names it.
    private static string? GuestCarries(WorldTransferCommitMember member) {
        if (member.Arrival != WorldTransferArrival.Spawn) { return $"a {member.Arrival} arrival"; }
        if (member.Companion is not null) { return "a companion"; }
        if (member.Drives is not null) { return "a body to drive"; }
        if (member.Continuum is not null) { return "carried motion"; }
        if (member.ActionContinuity is { Registers.Count: > 0 }) { return "action registers"; }
        return ((member.TravelTurn != default) ? "an arrival turn" : null);
    }
}
/// <summary>The decision the crossing door made about one operation's caller. Every operation that can change a
/// world's crossing ledger runs behind one, and only <see cref="Of"/> and <see cref="Recorded"/> make one that admits
/// anything: the default value refuses.</summary>
public readonly struct WorldTransferAdmission {
    private const byte Admitted = 1;
    private const byte Guest = 2;

    private readonly byte m_kind;

    private WorldTransferAdmission(byte kind, string? guestSubject = null) {
        m_kind = kind;
        GuestSubject = guestSubject;
    }

    /// <summary>Gets a value indicating whether the operation is refused: its caller is outside a closed group.</summary>
    public bool Refused => (m_kind == 0);
    /// <summary>Gets a value indicating whether the caller is a guest of a closed group.</summary>
    public bool IsGuest => (m_kind == Guest);
    /// <summary>Gets the subject the host's issuer vouched for, when a live guest made the call: what the door shapes
    /// the guest's reservation and commit by. <see langword="null"/> for every other caller and for a recorded
    /// decision, whose reservation and commit were shaped when they were admitted.</summary>
    public string? GuestSubject { get; }

    /// <summary>Decides a live operation. A world with no boundary admits every caller, as an open world does.</summary>
    /// <param name="boundary">The world's boundary, or <see langword="null"/> for a world outside any closed group.</param>
    /// <param name="caller">Who made the call, or <see langword="null"/> for a caller that proved nothing.</param>
    /// <param name="sourceAuthority">The source namespace the operation names.</param>
    /// <returns>The admission.</returns>
    public static WorldTransferAdmission Of(WorldTransferBoundary? boundary, WorldTransferCaller? caller, string sourceAuthority) =>
        ((boundary is null)
            ? new(kind: Admitted)
            : (boundary.Classify(caller: caller, sourceAuthority: sourceAuthority) switch {
                WorldTransferParty.Member => new(kind: Admitted),
                WorldTransferParty.Guest => new(guestSubject: caller!.Session!.Subject, kind: Guest),
                _ => default,
            }));
    /// <summary>Returns the admission of a decision already made live and recorded. A crossing-log record and a taped
    /// arrival carry what they were admitted as, so each is redone as that and never decided again: no boundary is
    /// read, and a replay that has none redoes a guest's arrival as a guest's.</summary>
    /// <param name="guest">Whether the record was admitted as a guest's.</param>
    /// <returns>The admission.</returns>
    public static WorldTransferAdmission Recorded(bool guest) => new(kind: (guest ? Guest : Admitted));
}
