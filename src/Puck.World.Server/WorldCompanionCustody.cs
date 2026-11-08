using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The custody of one owned traveller's title, as its home's records say.</summary>
public enum WorldCustodyKind : byte {
    /// <summary>Embodied at home, authorized to depart but not yet departed, or detached by home's own departure in
    /// doubt.</summary>
    Home = 0,

    /// <summary>A hop home authorized is not yet settled.</summary>
    Pending = 1,

    /// <summary>A custodian holds the title at an epoch.</summary>
    Away = 2,

    /// <summary>A definition install removed the placement's role: the title has ended.</summary>
    Released = 3,
}
/// <summary>What a custody notice tells the player.</summary>
public enum WorldCustodyNotice : byte {
    /// <summary>Home recalled the companion from its last committed image.</summary>
    Recalled = 0,

    /// <summary>The author's install released the companion's title.</summary>
    Released = 1,
}
/// <summary>Why a custodian retired an embodied copy.</summary>
public enum WorldCustodyRetireCause : byte {
    /// <summary>Home answered that the copy is not the title's current custody.</summary>
    Stale = 0,

    /// <summary>Home answered that the title was released.</summary>
    Released = 1,
}
/// <summary>A custody ticket: the statement "home recorded hop <paramref name="Hop"/> moving
/// <paramref name="Incarnation"/> to its destination at <paramref name="Epoch"/>". It is evidence of nothing on its own:
/// a destination confirms the hop with home before it leases the companion, and home checks a return against its own
/// records.</summary>
/// <param name="Home">The title authority: the authority that minted the incarnation.</param>
/// <param name="Incarnation">The companion's mobility incarnation.</param>
/// <param name="Hop">The key of the crossing that moves custody.</param>
/// <param name="Epoch">The custody epoch the hop lands the companion at.</param>
public readonly record struct WorldCustodyTicket(string Home, WorldEntityAddress Incarnation, WorldTransferKey Hop, ulong Epoch);
/// <summary>What a crossing member carries as a companion: the role it holds, the member it follows, its title
/// authority, its preferred driver, and its custody ticket.</summary>
/// <param name="Role">The companion role name the destination must declare.</param>
/// <param name="FollowsMember">The request-local member id of the player member it follows in the same request, or
/// <see langword="null"/> when it follows none (a return).</param>
/// <param name="Home">The title authority.</param>
/// <param name="Driver">The preferred driver's content pin (<c>sha256/…</c>), or <see langword="null"/> for none.</param>
/// <param name="Ticket">The custody ticket home granted for this hop, or <see langword="null"/> before home has
/// granted one.</param>
public sealed record WorldTransferCompanion(string Role, int? FollowsMember, string Home, string? Driver, WorldCustodyTicket? Ticket);
/// <summary>A companion's recorded binding at the authority it landed in: the member it follows, and the slot and
/// generation that member landed at. The binding is live while that slot holds that generation; a generation of
/// <c>-1</c> is home's placement binding, which follows whoever holds the home seat.</summary>
/// <param name="FollowsMember">The followed member's request-local id.</param>
/// <param name="Slot">The body index the followed member landed at.</param>
/// <param name="Generation">The occupant generation the followed member took there, or <c>-1</c> for the placement
/// binding.</param>
public readonly record struct WorldCompanionBinding(int FollowsMember, int Slot, int Generation);
/// <summary>One companion's entry in a hop home authorized.</summary>
/// <param name="Incarnation">The companion's incarnation.</param>
/// <param name="Epoch">The custody epoch the companion leaves at.</param>
/// <param name="Next">The custody epoch it lands at: one above <paramref name="Epoch"/>.</param>
public readonly record struct WorldCustodyHopMember(WorldEntityAddress Incarnation, ulong Epoch, ulong Next);
/// <summary>One companion's authorized hop, as home's records index it.</summary>
/// <param name="Key">The hop's transfer key.</param>
/// <param name="Incarnation">The companion.</param>
/// <param name="From">The custodian the hop leaves.</param>
/// <param name="To">The destination the hop lands at.</param>
/// <param name="Epoch">The epoch it leaves at.</param>
/// <param name="Next">The epoch it lands at.</param>
public readonly record struct WorldCustodyHop(WorldTransferKey Key, WorldEntityAddress Incarnation, string From, string To, ulong Epoch, ulong Next);
/// <summary>What a source minted a transfer key for, before it sent anything under it: the destination and every
/// companion the crossing set out with, each with its title authority. Whatever becomes of the crossing, each title
/// authority other than this source and the destination is told how that companion's hop under the key settled.</summary>
/// <param name="TransferId">The minted transfer id.</param>
/// <param name="To">The destination authority.</param>
/// <param name="Companions">The companions the crossing set out with.</param>
public sealed record WorldTransferMint(ulong TransferId, string To, IReadOnlyList<WorldCustodyMintMember> Companions);
/// <summary>One companion a minted crossing set out with.</summary>
/// <param name="Incarnation">The companion.</param>
/// <param name="Home">Its title authority.</param>
public readonly record struct WorldCustodyMintMember(WorldEntityAddress Incarnation, string Home);
/// <summary>One title as its home's records say: its custody, the custodian and epoch, its pending hop, every epoch it
/// issued, and whether it was recalled or released.</summary>
/// <param name="Incarnation">The incarnation; its index is the held slot.</param>
/// <param name="Kind">The custody.</param>
/// <param name="Custodian">The authority holding the title (home's own identity for <see cref="WorldCustodyKind.Home"/>).</param>
/// <param name="Epoch">The custody epoch.</param>
/// <param name="PendingKey">The pending hop's key while <see cref="WorldCustodyKind.Pending"/>.</param>
/// <param name="MaxIssued">The highest epoch home ever issued for the title.</param>
/// <param name="Recalls">The epoch of every recall home recorded, in order; each owes its own notice.</param>
/// <param name="Installed">Whether an install released the title (a pending one ends when its hop resolves).</param>
public sealed record WorldCustodyTitle(WorldEntityAddress Incarnation, WorldCustodyKind Kind, string Custodian, ulong Epoch, WorldTransferKey? PendingKey, ulong MaxIssued, IReadOnlyList<ulong> Recalls, bool Installed) {
    /// <summary>Gets the slot the title holds at home: its incarnation's index.</summary>
    public int HeldSlot => Incarnation.Index;
}
/// <summary>The custody reducer of one authority: every durable custody fact is a function of the authority's custody
/// records, folded by <see cref="Apply"/> in the order they became durable. Checkpoint restore, crossing-log recovery
/// and a replay's re-drive all fold through it, so a restarted or replayed authority derives exactly the custody the
/// live one did. Records that are not durable (refused, or uncertain until recovery reads the log) are never folded.
/// Only home holds titles: an incarnation this authority minted is always in one of the four
/// <see cref="WorldCustodyKind"/> states (no record means <see cref="WorldCustodyKind.Home"/> at epoch zero), and one it
/// did not mint has no title here.</summary>
public sealed class WorldCompanionCustody {
    private sealed class Title(WorldEntityAddress incarnation, string home) {
        public WorldEntityAddress Incarnation { get; } = incarnation;
        public WorldCustodyKind Kind { get; set; } = WorldCustodyKind.Home;
        public string Custodian { get; set; } = home;
        public ulong Epoch { get; set; }
        public WorldTransferKey? PendingKey { get; set; }
        public ulong MaxIssued { get; set; }
        public List<ulong> Recalls { get; } = [];
        public bool Installed { get; set; }
    }

    private readonly Func<string> m_authority;
    private readonly List<WorldCrossingRecord> m_history = [];
    private readonly Dictionary<WorldEntityAddress, Title> m_titles = new();
    private readonly Dictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), WorldCustodyHop> m_hops = new();
    private readonly HashSet<(WorldTransferKey Key, WorldEntityAddress Incarnation)> m_hopsDone = new();
    private readonly HashSet<(WorldTransferKey Key, WorldEntityAddress Incarnation)> m_superseded = new();
    private readonly HashSet<(WorldTransferKey Key, WorldEntityAddress Incarnation)> m_acked = new();
    private readonly HashSet<(WorldEntityAddress Incarnation, WorldCustodyNotice What, ulong Epoch)> m_noticed = new();
    private readonly Dictionary<(WorldEntityAddress Incarnation, int Visit), ulong> m_stranded = new();
    private readonly HashSet<(WorldEntityAddress Incarnation, int Visit)> m_graceElapsed = new();
    private readonly HashSet<WorldEntityAddress> m_retired = new();
    private readonly Dictionary<WorldEntityAddress, (WorldTransferKey Arrival, WorldCompanionBinding Binding, WorldEntityAddress Follows, WorldTransferCompanion Carried)> m_bindings = new();
    // This authority's departures that carried companions, by transfer id: the authority each went to, each companion's
    // member id, incarnation and title authority, and the body index every member left; and the landed member ids of
    // each one's settlement.
    private readonly Dictionary<ulong, (string Target, List<(int MemberId, WorldEntityAddress Incarnation, string Home)> Carried, (int Slot, string? PlacementId)[] Left)> m_ownDepartures = new();
    // The placement each title's companion left home from, by its latest departure: what its re-adoption binds it to,
    // and the placement its held slot is counted for while it is away.
    private readonly Dictionary<WorldEntityAddress, string> m_placements = new();
    // The companion leaf each visiting companion last landed here with: its role, title authority, driver and ticket.
    private readonly Dictionary<WorldEntityAddress, WorldTransferCompanion> m_carried = new();
    // Every arrival here that carried companions, by key: the companions it landed. A destination answers its title
    // authorities from these, whatever became of the transaction since.
    private readonly Dictionary<WorldTransferKey, HashSet<WorldEntityAddress>> m_arrivals = new();
    // The request-local ids each such arrival landed: the partial verdict its source settles by, kept however the
    // transaction's receipt is later retired or superseded.
    private readonly Dictionary<WorldTransferKey, int[]> m_arrivalLanded = new();
    private readonly Dictionary<ulong, int[]> m_ownSettlements = new();
    // Every crossing this authority minted with companions, by transfer id.
    private readonly Dictionary<ulong, WorldTransferMint> m_mints = new();

    /// <summary>Initializes a new instance of the <see cref="WorldCompanionCustody"/> class for one authority.</summary>
    /// <param name="authority">Reads the authority's identity: the namespace whose incarnations it holds titles
    /// for.</param>
    public WorldCompanionCustody(Func<string> authority) {
        ArgumentNullException.ThrowIfNull(argument: authority);
        m_authority = authority;
    }

    /// <summary>Gets a copy of every custody record this authority folded, in fold order: what a checkpoint captures. Each
    /// call returns records of its own, so nothing a caller does to them reaches this authority's history.</summary>
    public IReadOnlyList<WorldCrossingRecord> History => [.. m_history.Select(selector: Owned)];
    /// <summary>Returns every title this home holds abroad: away at a custodian, or pending a hop. Only that custodian's
    /// return, a recall or the hop's resolution ends one, so an authority holding one must stay reachable or carry it in
    /// a durable image. An empty population is not evidence that its companions are gone.</summary>
    /// <returns>The titles, by held slot.</returns>
    public IReadOnlyList<WorldCustodyTitle> TitlesAbroad() => [.. m_titles.Values
        .Where(predicate: static title => title.Kind is WorldCustodyKind.Pending or WorldCustodyKind.Away)
        .OrderBy(keySelector: static title => title.Incarnation.Index)
        .Select(selector: title => TitleOf(incarnation: title.Incarnation)!)];
    /// <summary>Gets whether this authority still owes a custody step its host's drain makes: a departure with companions
    /// not yet settled, a key not yet abandoned, a settlement notice home has not acknowledged, or a settlement of its own
    /// hop not yet recorded.</summary>
    public bool OwesCustodySteps => m_ownDepartures.Keys.Any(predicate: key => !m_ownSettlements.ContainsKey(key: key)) ||
        (UnsettledOwnKeys().Count > 0) || (OwedHopNotices().Count > 0) || (OwedOwnHopSettlements().Count > 0);

    /// <summary>Gets every incarnation a custodian retired here on home's word.</summary>
    public IReadOnlyCollection<WorldEntityAddress> Retired => m_retired;

    /// <summary>Returns whether <paramref name="record"/> is of a shape this reducer can fold: a custody kind, an
    /// allocation that minted a crossing with companions, an arrival that landed or re-adopted a companion, a departure
    /// that carried one, or a settlement.</summary>
    /// <param name="record">The record.</param>
    /// <returns><see langword="true"/> when <see cref="Apply"/> may derive custody from it.</returns>
    public static bool Folds(WorldCrossingRecord record) => record switch {
        WorldCrossingRecord.Hop or WorldCrossingRecord.HopDone or WorldCrossingRecord.HopAcked or WorldCrossingRecord.Recall or
        WorldCrossingRecord.Noticed or WorldCrossingRecord.Release or WorldCrossingRecord.Retire or WorldCrossingRecord.Stranded or
        WorldCrossingRecord.GraceElapsed or WorldCrossingRecord.Settlement => true,
        WorldCrossingRecord.Allocation allocation => (allocation.Mint is { Companions.Count: > 0 }),
        WorldCrossingRecord.Arrival arrival => ((arrival.Value.Readopted.Count > 0) || (arrival.Value.Leased.Count > 0) || arrival.Value.Request.Members.Any(predicate: static member => member.Companion is not null)),
        WorldCrossingRecord.Departure departure => departure.Transfer.CommitMembers.Any(predicate: static member => member.Companion is not null),
        _ => false,
    };
    /// <summary>Returns whether this authority's custody depends on <paramref name="record"/>: one that
    /// <see cref="Folds"/>, where a settlement counts only when it settles a crossing minted with companions or a
    /// departure that carried one.</summary>
    /// <param name="record">The record.</param>
    /// <returns><see langword="true"/> when <see cref="Apply"/> folds it.</returns>
    public bool Concerns(WorldCrossingRecord record) => record switch {
        WorldCrossingRecord.Settlement settlement => (m_ownDepartures.ContainsKey(key: settlement.TransferId) || m_mints.ContainsKey(key: settlement.TransferId) || IsOwnHopKey(transferId: settlement.TransferId)),
        _ => Folds(record: record),
    };

    /// <summary>Folds one durable record. A record this reducer does not fold (<see cref="Folds"/>) changes nothing.
    /// The fold is total: any prefix of an authority's records folds to a custody state.</summary>
    /// <param name="record">The durable record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public void Apply(WorldCrossingRecord record) {
        ArgumentNullException.ThrowIfNull(argument: record);

        if (!Concerns(record: record)) {
            return;
        }
        // The history and every fact derived from it read this authority's own copy, never the caller's collections.
        record = Owned(record: record);
        m_history.Add(item: record);
        switch (record) {
            case WorldCrossingRecord.Allocation { Mint: { } mint }:
                m_mints[mint.TransferId] = mint;
                break;
            case WorldCrossingRecord.Hop hop:
                foreach (var member in hop.Members) {
                    var key = (hop.Key, member.Incarnation);

                    m_hops[key] = new WorldCustodyHop(
                        Epoch: member.Epoch,
                        From: hop.From,
                        Incarnation: member.Incarnation,
                        Key: hop.Key,
                        Next: member.Next,
                        To: hop.To
                    );
                    // A hop whose settlement reached home before the hop itself was recorded is cancelled on arrival.
                    if (TitleFor(incarnation: member.Incarnation) is { } title && !m_hopsDone.Contains(item: key)) {
                        title.Kind = WorldCustodyKind.Pending;
                        title.PendingKey = hop.Key;
                        title.MaxIssued = Math.Max(val1: title.MaxIssued, val2: member.Next);
                    }
                }
                break;
            case WorldCrossingRecord.HopDone done: {
                var key = (done.Key, done.Incarnation);

                _ = m_hopsDone.Add(item: key);
                if (TitleFor(incarnation: done.Incarnation) is not { } title) {
                    break;
                }
                // A release installed while this hop was pending ends the title once the hop resolves.
                if (title.Installed) {
                    if ((title.Kind == WorldCustodyKind.Pending) && (title.PendingKey == done.Key)) {
                        title.Kind = WorldCustodyKind.Released;
                        title.PendingKey = null;
                    }
                    break;
                }
                if (!m_hops.TryGetValue(key: key, value: out var settled) || m_superseded.Contains(item: key)) {
                    break;
                }
                if (done.Arrived) {
                    title.Kind = WorldCustodyKind.Away;
                    title.Custodian = settled.To;
                    title.Epoch = settled.Next;
                } else {
                    title.Kind = (IsHome(authority: settled.From) ? WorldCustodyKind.Home : WorldCustodyKind.Away);
                    title.Custodian = settled.From;
                    title.Epoch = settled.Epoch;
                }
                title.PendingKey = null;
                break;
            }
            case WorldCrossingRecord.HopAcked acked:
                _ = m_acked.Add(item: (acked.Key, acked.Incarnation));
                break;
            case WorldCrossingRecord.Recall recall:
                if (TitleFor(incarnation: recall.Incarnation) is { } recalled) {
                    foreach (var pending in m_hops.Keys) {
                        if ((pending.Incarnation == recall.Incarnation) && !m_hopsDone.Contains(item: pending)) {
                            _ = m_superseded.Add(item: pending);
                        }
                    }
                    recalled.Kind = WorldCustodyKind.Home;
                    recalled.Custodian = m_authority();
                    recalled.Epoch = recall.Epoch;
                    recalled.MaxIssued = Math.Max(val1: recalled.MaxIssued, val2: recall.Epoch);
                    recalled.PendingKey = null;
                    recalled.Recalls.Add(item: recall.Epoch);
                }
                break;
            case WorldCrossingRecord.Noticed noticed:
                _ = m_noticed.Add(item: (noticed.Incarnation, noticed.What, noticed.Epoch));
                break;
            case WorldCrossingRecord.Release release:
                if (TitleFor(incarnation: release.Incarnation) is { } released) {
                    released.Installed = true;
                    // A pending hop's destination may hold home's confirmation for its commit: the title stays pending
                    // until that hop resolves.
                    if (released.Kind != WorldCustodyKind.Pending) {
                        released.Kind = WorldCustodyKind.Released;
                        released.PendingKey = null;
                    }
                }
                break;
            case WorldCrossingRecord.Retire retire:
                _ = m_retired.Add(item: retire.Incarnation);
                break;
            case WorldCrossingRecord.Stranded stranded:
                m_stranded[(stranded.Incarnation, stranded.Visit)] = stranded.DeadlineTick;
                break;
            case WorldCrossingRecord.GraceElapsed grace:
                _ = m_graceElapsed.Add(item: (grace.Incarnation, grace.Visit));
                break;
            case WorldCrossingRecord.Departure departure: {
                var carried = new List<(int, WorldEntityAddress, string)>();
                var transfer = departure.Transfer;

                for (var index = 0; (index < transfer.CommitMembers.Count); index++) {
                    var member = transfer.CommitMembers[index];

                    if (member.Companion?.Ticket is { } ticket) {
                        carried.Add(item: (((member.MemberId < 0) ? index : member.MemberId), ticket.Incarnation, ticket.Home));
                    }
                    if ((member.Companion is not null) && (index < transfer.Landed.Count) && (transfer.Landed[index].Peer?.PlacementId is { } placementId) && IsHome(authority: transfer.Landed[index].Mobility.Incarnation.Authority)) {
                        m_placements[transfer.Landed[index].Mobility.Incarnation] = placementId;
                    }
                }
                m_ownDepartures[transfer.TransferId] = (transfer.TargetAuthority, carried, [.. transfer.Landed.Select(selector: static member => (member.SourceSlot, member.Peer?.PlacementId))]);
                break;
            }
            case WorldCrossingRecord.Settlement settlement:
                m_ownSettlements[settlement.TransferId] = [.. settlement.Landed];
                break;
            case WorldCrossingRecord.Arrival arrival:
                m_arrivals[arrival.Value.Key] = [.. arrival.Value.Request.Members.Where(predicate: static member => member.Companion is not null).Select(selector: static member => member.Mobility!.Value.Incarnation)];
                m_arrivalLanded[arrival.Value.Key] = [.. arrival.Value.Request.Members.Select(selector: static (member, ordinal) => member.IdAt(ordinal: ordinal))];
                foreach (var member in arrival.Value.Request.Members) {
                    if ((member.Companion is { } leaf) && (member.Mobility is { } landed) && !IsHome(authority: landed.Incarnation.Authority)) {
                        m_carried[landed.Incarnation] = leaf;
                    }
                }
                foreach (var binding in Bindings(arrival: arrival.Value)) {
                    m_bindings[binding.Companion] = (arrival.Value.Key, binding.Binding, binding.Follows, binding.Carried);
                }
                foreach (var incarnation in arrival.Value.Readopted) {
                    if (TitleFor(incarnation: incarnation) is not { } home) {
                        continue;
                    }
                    var landed = arrival.Value.Request.Members.FirstOrDefault(predicate: member => (member.Mobility?.Incarnation == incarnation));

                    if (landed.Mobility is not { } mobility) {
                        continue;
                    }
                    home.Kind = WorldCustodyKind.Home;
                    home.Custodian = m_authority();
                    home.Epoch = checked((mobility.Epoch + 1UL));
                    home.MaxIssued = Math.Max(val1: home.MaxIssued, val2: home.Epoch);
                    home.PendingKey = null;
                }
                break;
        }
    }
    /// <summary>Replaces this reducer's state with the fold of <paramref name="history"/>: a checkpoint's restore.</summary>
    /// <param name="history">The custody records a checkpoint captured (<see cref="History"/>), in fold order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is <see langword="null"/>.</exception>
    public void Restore(IReadOnlyList<WorldCrossingRecord> history) {
        ArgumentNullException.ThrowIfNull(argument: history);

        m_history.Clear();
        m_titles.Clear();
        m_hops.Clear();
        m_hopsDone.Clear();
        m_superseded.Clear();
        m_acked.Clear();
        m_noticed.Clear();
        m_stranded.Clear();
        m_graceElapsed.Clear();
        m_retired.Clear();
        m_bindings.Clear();
        m_ownDepartures.Clear();
        m_placements.Clear();
        m_carried.Clear();
        m_arrivalLanded.Clear();
        m_ownSettlements.Clear();
        m_arrivals.Clear();
        m_mints.Clear();
        foreach (var record in history) {
            Apply(record: record);
        }
    }

    /// <summary>Returns the title this authority holds for <paramref name="incarnation"/>, or <see langword="null"/>
    /// when it did not mint the incarnation as a titled one (<c>NotMyTitle</c>): another authority's, or one without the
    /// title mark (<see cref="WorldEntityAddress.Titled"/>). A titled incarnation it minted with no custody record is at
    /// home at epoch zero.</summary>
    /// <param name="incarnation">The incarnation.</param>
    /// <returns>The title, or <see langword="null"/>.</returns>
    public WorldCustodyTitle? TitleOf(WorldEntityAddress incarnation) {
        if (!incarnation.Titled || !IsHome(authority: incarnation.Authority)) {
            return null;
        }
        if (!m_titles.TryGetValue(key: incarnation, value: out var title)) {
            return new WorldCustodyTitle(
                Custodian: m_authority(),
                Epoch: 0UL,
                Incarnation: incarnation,
                Installed: false,
                Kind: WorldCustodyKind.Home,
                MaxIssued: 0UL,
                PendingKey: null,
                Recalls: []
            );
        }
        return new WorldCustodyTitle(
            Custodian: title.Custodian,
            Epoch: title.Epoch,
            Incarnation: title.Incarnation,
            Installed: title.Installed,
            Kind: title.Kind,
            MaxIssued: title.MaxIssued,
            PendingKey: title.PendingKey,
            Recalls: [.. title.Recalls]
        );
    }
    /// <summary>Returns the hop home recorded for <paramref name="incarnation"/> under <paramref name="key"/>, or
    /// <see langword="null"/> when it recorded none.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <returns>The hop, or <see langword="null"/>.</returns>
    public WorldCustodyHop? HopOf(WorldTransferKey key, WorldEntityAddress incarnation) => (m_hops.TryGetValue(key: (key, incarnation), value: out var hop) ? hop : null);
    /// <summary>Returns whether home recorded the settlement of <paramref name="incarnation"/>'s hop under
    /// <paramref name="key"/> (or a settlement notice that reached it before the hop).</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <returns><see langword="true"/> once the hop is settled here.</returns>
    public bool IsHopDone(WorldTransferKey key, WorldEntityAddress incarnation) => m_hopsDone.Contains(item: (key, incarnation));
    /// <summary>Returns whether home's grant of <paramref name="incarnation"/>'s hop under <paramref name="key"/> to
    /// <paramref name="destination"/> is current: it is that title's pending hop now, not settled, superseded or
    /// released. A historical record is not a grant.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <param name="destination">The authority asking.</param>
    /// <param name="next">The epoch the grant lands the companion at, when it is current.</param>
    /// <returns><see langword="true"/> when the grant is current.</returns>
    public bool IsGrantCurrent(WorldTransferKey key, WorldEntityAddress incarnation, string destination, out ulong next) {
        next = 0UL;
        if (
            !m_hops.TryGetValue(key: (key, incarnation), value: out var hop) ||
            !string.Equals(a: hop.To, b: destination, comparisonType: StringComparison.Ordinal) ||
            m_hopsDone.Contains(item: (key, incarnation)) ||
            !m_titles.TryGetValue(key: incarnation, value: out var title) ||
            (title.Kind != WorldCustodyKind.Pending) ||
            (title.PendingKey != key) ||
            title.Installed
        ) {
            return false;
        }
        next = hop.Next;
        return true;
    }
    /// <summary>Returns whether home acknowledged this custodian's settlement notice for one companion's hop.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <returns><see langword="true"/> once the acknowledgement is recorded.</returns>
    public bool IsAcked(WorldTransferKey key, WorldEntityAddress incarnation) => m_acked.Contains(item: (key, incarnation));
    /// <summary>Returns whether the companion was recorded stranded during the player's <paramref name="visit"/>th
    /// visit here, and the grace window's deadline tick.</summary>
    /// <param name="incarnation">The companion.</param>
    /// <param name="visit">The visit.</param>
    /// <param name="deadlineTick">The authority tick the grace window ends at.</param>
    /// <returns><see langword="true"/> when the stranding is recorded.</returns>
    public bool IsStranded(WorldEntityAddress incarnation, int visit, out ulong deadlineTick) => m_stranded.TryGetValue(key: (incarnation, visit), value: out deadlineTick);
    /// <summary>Returns whether the grace window of the companion's stranding during <paramref name="visit"/> has
    /// elapsed on record.</summary>
    /// <param name="incarnation">The companion.</param>
    /// <param name="visit">The visit.</param>
    /// <returns><see langword="true"/> once the elapse is recorded.</returns>
    public bool IsGraceElapsed(WorldEntityAddress incarnation, int visit) => m_graceElapsed.Contains(item: (incarnation, visit));
    /// <summary>Returns the settlements home owes its own hops, each a record of its own: once the key's crossing is
    /// settled, a hop arrived when the settlement landed the companion's member and did not otherwise (a settlement with
    /// no departure is an abandoned key, which landed nothing). A key still in flight or in doubt owes nothing yet.</summary>
    /// <returns>Each owed settlement: the hop's key, the companion, and whether it arrived.</returns>
    public IReadOnlyList<(WorldTransferKey Key, WorldEntityAddress Incarnation, bool Arrived)> OwedOwnHopSettlements() {
        List<(WorldTransferKey Key, WorldEntityAddress Incarnation, bool Arrived)>? owed = null;
        var home = m_authority();

        // Filtered first and ordered after, so a home that owes nothing (every steady tick) sorts and allocates nothing.
        foreach (var ((key, incarnation), hop) in m_hops) {
            if (
                !string.Equals(a: hop.From, b: home, comparisonType: StringComparison.Ordinal) ||
                !string.Equals(a: key.SourceAuthority, b: home, comparisonType: StringComparison.Ordinal) ||
                m_hopsDone.Contains(item: (key, incarnation)) ||
                !m_ownSettlements.TryGetValue(key: key.TransferId, value: out var landed)
            ) {
                continue;
            }
            // A companion the departure never carried (left out at the reservation, or an abandoned key) did not arrive.
            var memberId = (m_ownDepartures.TryGetValue(key: key.TransferId, value: out var departed)
                ? departed.Carried.Where(predicate: carried => (carried.Incarnation == incarnation)).Select(selector: static carried => (int?)carried.MemberId).FirstOrDefault()
                : null);

            (owed ??= []).Add(item: (key, incarnation, ((memberId is { } member) && landed.Contains(value: member))));
        }
        return (owed is null)
            ? []
            : [.. owed.OrderBy(keySelector: static entry => entry.Key.TransferId).ThenBy(keySelector: static entry => entry.Incarnation.Index)];
    }
    /// <summary>Returns the transfer ids of this authority's own keys that a crossing with companions was minted under,
    /// or that it granted a hop of its own under, and that neither departed with a companion nor settled: once no
    /// attempt under one is queued or in doubt it is abandoned on record (a settlement landing nothing), so its hops
    /// settle as not arrived and every other title authority its companions belong to is told.</summary>
    /// <returns>The transfer ids, lowest first.</returns>
    public IReadOnlyList<ulong> UnsettledOwnKeys() {
        var home = m_authority();
        SortedSet<ulong>? unsettled = null;

        // Gathered without a query, so a home with nothing unsettled (every steady tick) allocates nothing.
        foreach (var pair in m_hops.Keys) {
            if (string.Equals(a: pair.Key.SourceAuthority, b: home, comparisonType: StringComparison.Ordinal) && !m_hopsDone.Contains(item: pair) && Unsettled(transferId: pair.Key.TransferId)) {
                (unsettled ??= []).Add(item: pair.Key.TransferId);
            }
        }
        foreach (var transferId in m_mints.Keys) {
            if (Unsettled(transferId: transferId)) { (unsettled ??= []).Add(item: transferId); }
        }
        return (unsettled is null) ? [] : [.. unsettled];

        bool Unsettled(ulong transferId) => !m_ownDepartures.ContainsKey(key: transferId) && !m_ownSettlements.ContainsKey(key: transferId);
    }
    /// <summary>Returns whether this authority recorded the mint of a crossing with companions under
    /// <paramref name="transferId"/>.</summary>
    /// <param name="transferId">The transfer id.</param>
    /// <returns><see langword="true"/> once the mint is durable.</returns>
    public bool IsMinted(ulong transferId) => m_mints.ContainsKey(key: transferId);
    private bool IsOwnHopKey(ulong transferId) {
        var home = m_authority();

        return m_hops.Keys.Any(predicate: pair => (pair.Key.TransferId == transferId) && string.Equals(a: pair.Key.SourceAuthority, b: home, comparisonType: StringComparison.Ordinal));
    }
    /// <summary>Returns whether the arrival under <paramref name="key"/> landed <paramref name="incarnation"/> here, or
    /// <see langword="null"/> when no arrival carrying companions landed under the key.</summary>
    /// <param name="key">The arrival's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <returns>Whether it landed, or <see langword="null"/>.</returns>
    public bool? ArrivalHolds(WorldTransferKey key, WorldEntityAddress incarnation) => (m_arrivals.TryGetValue(key: key, value: out var landed)
        ? landed.Contains(item: incarnation)
        : null);
    /// <summary>Returns the request-local ids the arrival under <paramref name="key"/> landed here, for an arrival that
    /// carried companions (the only kind that lands part of its cohort).</summary>
    /// <param name="key">The arrival's key.</param>
    /// <returns>The landed ids, or <see langword="null"/> when no such arrival landed under the key.</returns>
    public IReadOnlyList<int>? ArrivalLanded(WorldTransferKey key) => m_arrivalLanded.GetValueOrDefault(key: key);
    /// <summary>Returns the verdict home's records give a companion coming back from <paramref name="custodian"/> at
    /// <paramref name="epoch"/>: the one door every return reads custody through, at its reservation and again at its
    /// commit. <see langword="null"/> accepts it: the title is away with exactly that custodian at exactly that epoch.
    /// A pending title answers <see cref="WorldCompanionRefusal.Pending"/> to a party to its pending hop and
    /// <see cref="WorldCompanionRefusal.Stale"/> to anyone else; a title at home, or away with another custodian or at
    /// another epoch, is <see cref="WorldCompanionRefusal.Stale"/>.</summary>
    /// <param name="incarnation">The returning companion.</param>
    /// <param name="custodian">The authenticated authority the return comes from.</param>
    /// <param name="epoch">The custody epoch the returning copy holds.</param>
    /// <returns>The refusal, or <see langword="null"/> when home takes the companion back.</returns>
    public WorldCompanionRefusal? VerifyReturn(WorldEntityAddress incarnation, string custodian, ulong epoch) {
        ArgumentNullException.ThrowIfNull(argument: custodian);

        if (!incarnation.Titled || !IsHome(authority: incarnation.Authority)) {
            return WorldCompanionRefusal.NotMyTitle;
        }
        if (!m_titles.TryGetValue(key: incarnation, value: out var title)) {
            return WorldCompanionRefusal.Stale;
        }
        switch (title.Kind) {
            case WorldCustodyKind.Released:
                return WorldCompanionRefusal.Released;
            case WorldCustodyKind.Pending: {
                var hop = m_hops[(title.PendingKey!.Value, incarnation)];

                return ((string.Equals(a: hop.From, b: custodian, comparisonType: StringComparison.Ordinal) || string.Equals(a: hop.To, b: custodian, comparisonType: StringComparison.Ordinal))
                    ? WorldCompanionRefusal.Pending
                    : WorldCompanionRefusal.Stale);
            }
            case WorldCustodyKind.Away:
                return ((string.Equals(a: title.Custodian, b: custodian, comparisonType: StringComparison.Ordinal) && (title.Epoch == epoch))
                    ? null
                    : WorldCompanionRefusal.Stale);
            default:
                return WorldCompanionRefusal.Stale;
        }
    }
    /// <summary>Returns what holds body index <paramref name="slot"/> for a body that may come back to it: the held slot
    /// of a title whose hop is pending or that a custodian holds, or a slot one of this authority's departures in doubt
    /// left. A held slot is never allocated to another body; the census, inhabitant reconciliation and admission all
    /// read this one predicate.</summary>
    /// <param name="slot">The body index.</param>
    /// <returns>The placement the held body belongs to (empty when it belongs to none), or <see langword="null"/> when
    /// the slot is not held.</returns>
    public string? HeldPlacement(int slot) {
        foreach (var title in m_titles.Values) {
            if ((title.Incarnation.Index == slot) && (title.Kind is WorldCustodyKind.Pending or WorldCustodyKind.Away)) {
                return m_placements.GetValueOrDefault(key: title.Incarnation, defaultValue: string.Empty);
            }
        }
        foreach (var (transferId, departed) in m_ownDepartures) {
            if (m_ownSettlements.ContainsKey(key: transferId)) {
                continue;
            }
            foreach (var (left, placementId) in departed.Left) {
                if (left == slot) {
                    return (placementId ?? string.Empty);
                }
            }
        }
        return null;
    }
    /// <summary>Returns the placement a title's companion left home from, which its re-adoption binds it to again.</summary>
    /// <param name="incarnation">The companion.</param>
    /// <returns>The placement id, or <see langword="null"/> when no departure of this authority carried it from one.</returns>
    public string? PlacementOf(WorldEntityAddress incarnation) => m_placements.GetValueOrDefault(key: incarnation);
    /// <summary>Returns the companion leaf a visiting companion last landed here with (its role, title authority, driver
    /// and the ticket of the hop that brought it), whether or not it was bound to a player.</summary>
    /// <param name="incarnation">The visiting companion.</param>
    /// <returns>The leaf, or <see langword="null"/> when no arrival here landed it as a companion.</returns>
    public WorldTransferCompanion? CarriedOf(WorldEntityAddress incarnation) => m_carried.GetValueOrDefault(key: incarnation);
    /// <summary>Returns the settlement notices this custodian owes other title authorities: every companion a settled
    /// crossing of its own was minted with under another authority's title, bound for a third authority, until the
    /// title authority acknowledged the notice, whether or not the companion departed with it. A return to the title
    /// authority owes none: home's own arrival record settles it.</summary>
    /// <returns>Each owed notice: the hop's key, the companion, and its title authority.</returns>
    public IReadOnlyList<(WorldTransferKey Key, WorldEntityAddress Incarnation, string Home)> OwedHopNotices() {
        var owed = new List<(WorldTransferKey, WorldEntityAddress, string)>();
        var self = m_authority();

        foreach (var (transferId, mint) in m_mints.OrderBy(keySelector: static pair => pair.Key)) {
            if (!m_ownSettlements.ContainsKey(key: transferId)) {
                continue;
            }
            var key = new WorldTransferKey(SourceAuthority: self, TransferId: transferId);

            foreach (var (incarnation, home) in mint.Companions) {
                if (!string.Equals(a: home, b: self, comparisonType: StringComparison.Ordinal) && !string.Equals(a: home, b: mint.To, comparisonType: StringComparison.Ordinal) && !m_acked.Contains(item: (key, incarnation))) {
                    owed.Add(item: (key, incarnation, home));
                }
            }
        }
        return owed;
    }
    /// <summary>Returns the notices home owes and has not recorded: one per recall, and one per release.</summary>
    /// <returns>Each owed notice: the incarnation, what it tells, and the recall's epoch (zero for a release).</returns>
    public IReadOnlyList<(WorldEntityAddress Incarnation, WorldCustodyNotice What, ulong Epoch)> OwedNotices() {
        var owed = new List<(WorldEntityAddress, WorldCustodyNotice, ulong)>();

        foreach (var title in m_titles.Values.OrderBy(keySelector: static title => title.Incarnation.Index).ThenBy(keySelector: static title => title.Incarnation.Generation)) {
            foreach (var epoch in title.Recalls) {
                if (!m_noticed.Contains(item: (title.Incarnation, WorldCustodyNotice.Recalled, epoch))) {
                    owed.Add(item: (title.Incarnation, WorldCustodyNotice.Recalled, epoch));
                }
            }
            if (title.Installed && !m_noticed.Contains(item: (title.Incarnation, WorldCustodyNotice.Released, 0UL))) {
                owed.Add(item: (title.Incarnation, WorldCustodyNotice.Released, 0UL));
            }
        }
        return owed;
    }
    /// <summary>Folds this authority's custody into a state hash: the count of folded records, then each one in
    /// order.</summary>
    /// <param name="hash">The running hash.</param>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_history.Count));
        foreach (var record in m_history) {
            hash.Add(values: WorldAuthorityCheckpointCodec.EncodeCrossingRecord(record: record));
        }
    }

    /// <summary>Returns the binding the arrival that landed <paramref name="incarnation"/> here recorded: the player
    /// member it follows, the slot and generation that member landed at, and the followed member's incarnation.</summary>
    /// <param name="incarnation">The companion.</param>
    /// <param name="binding">The binding, when one is recorded.</param>
    /// <param name="follows">The followed member's incarnation.</param>
    /// <param name="arrival">The key of the arrival that recorded it.</param>
    /// <param name="carried">The companion leaf it arrived with (its role, home and driver).</param>
    /// <returns><see langword="true"/> when an arrival here bound the companion to a member landed with it.</returns>
    public bool TryBinding(WorldEntityAddress incarnation, out WorldCompanionBinding binding, out WorldEntityAddress follows, out WorldTransferKey arrival, out WorldTransferCompanion? carried) {
        if (m_bindings.TryGetValue(key: incarnation, value: out var bound)) {
            (arrival, binding, follows, carried) = bound;
            return true;
        }
        binding = default;
        follows = default;
        arrival = default;
        carried = null;
        return false;
    }
    /// <summary>Derives each landed companion's binding from an arrival: the member it follows by request-local id, bound
    /// to the slot and generation that member landed at in the same arrival. A companion whose followed member did not
    /// land with it is bound to nothing.</summary>
    /// <param name="arrival">The arrival as it landed.</param>
    /// <returns>Each bound companion, in member order, with its binding, the followed member's incarnation and the
    /// companion leaf it carried.</returns>
    public static IReadOnlyList<(WorldEntityAddress Companion, WorldCompanionBinding Binding, WorldEntityAddress Follows, WorldTransferCompanion Carried)> Bindings(WorldCrossingArrival arrival) {
        ArgumentNullException.ThrowIfNull(argument: arrival);

        var bound = new List<(WorldEntityAddress, WorldCompanionBinding, WorldEntityAddress, WorldTransferCompanion)>();
        var members = arrival.Request.Members;

        for (var index = 0; (index < members.Count); index++) {
            if ((members[index].Companion is not { FollowsMember: { } followsId } carried) || (members[index].Mobility is not { } mobility)) {
                continue;
            }
            for (var followed = 0; (followed < members.Count); followed++) {
                if (
                    (members[followed].IdAt(ordinal: followed) != followsId) ||
                    (members[followed].Companion is not null) ||
                    (members[followed].Mobility is not { } leader) ||
                    (followed >= arrival.Generations.Count)
                ) {
                    continue;
                }
                bound.Add(item: (mobility.Incarnation, new WorldCompanionBinding(
                    FollowsMember: followsId,
                    Generation: arrival.Generations[followed],
                    Slot: arrival.Slots[followed]
                ), leader.Incarnation, carried));
                break;
            }
        }
        return bound;
    }

    private static WorldCrossingRecord Owned(WorldCrossingRecord record) => (WorldAuthorityCheckpointCodec.TryDecodeCrossingRecord(
        bytes: WorldAuthorityCheckpointCodec.EncodeCrossingRecord(record: record),
        reason: out var reason,
        record: out var owned
    )
        ? owned!
        : throw new ArgumentException(message: $"a custody record does not survive its own encoding — {reason}", paramName: nameof(record)));
    private bool IsHome(string authority) => string.Equals(a: authority, b: m_authority(), comparisonType: StringComparison.Ordinal);
    private Title? TitleFor(WorldEntityAddress incarnation) {
        if (!incarnation.Titled || !IsHome(authority: incarnation.Authority)) {
            return null;
        }
        if (!m_titles.TryGetValue(key: incarnation, value: out var title)) {
            title = new Title(home: m_authority(), incarnation: incarnation);
            m_titles[incarnation] = title;
        }
        return title;
    }
}
