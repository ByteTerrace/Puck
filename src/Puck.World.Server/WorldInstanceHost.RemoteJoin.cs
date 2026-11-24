using Puck.Commands;

namespace Puck.World;

// A HOST STARTED TO JOIN ANOTHER AUTHORITY SENDS ITS SEATS THERE. The host boots its own copy of the world so its
// seats have bodies to send, and every local seat that stands in that copy crosses to the authority the host names:
// the seats the world seats at boot as one party, in one reservation, and a seat that joins later by itself, when it
// joins. The boot world seats its eager seats over its first steps, one a step, so the join waits until every one of
// them stands there before it sends the party. A seat is tried once for each body it takes: a refusal is narrated by
// the transfer and is not retried until the seat leaves and joins again, so a full authority is not asked every tick.
public sealed partial class WorldInstanceHost {
    // Where the boot world's seats go, or null for a host that keeps them.
    private TransferDestination? m_remoteJoin;
    // For each local seat, one more than the body generation the join last enqueued a crossing for; zero for none.
    private readonly int[] m_remoteJoinTried = new int[WorldBodiesLimits.LocalSeatCount];
    // The boot world's active local seats and one more than each one's body generation, as the last scan read them
    // under the authority's gate. The scan is one delegate, made once, so a host step allocates nothing for it.
    private readonly int[] m_remoteJoinSeats = new int[WorldBodiesLimits.LocalSeatCount];
    private readonly int[] m_remoteJoinGenerations = new int[WorldBodiesLimits.LocalSeatCount];
    private Func<int>? m_remoteJoinScan;
    // The seats the boot world's document seats at boot, as the last scan read it, and whether the boot party has been
    // sent: until it has, the join holds back while fewer seats than that stand in the boot world.
    private int m_remoteJoinEager;
    private bool m_remoteJoinPartySent;
    // Whether this host also listens for incoming connections, in which case its boot world keeps stepping even while no
    // local seat stands in it.
    private bool m_remoteJoinListens;

    /// <summary>How long, in seconds of the destination's time, a joining seat's reservation holds its slot. A host
    /// that joins at boot can take seconds to step its own first ticks, and a reservation that lapsed before its commit
    /// arrived decides nothing, so the join asks for a hold that outlasts a slow start.</summary>
    public const double RemoteJoinHoldSeconds = 60.0;

    // Whether this host's boot world is a non-listening client launchpad with no work of its own left: its boot party
    // was sent, nobody stands in it, no local seat presents it, and no transfer, forwarding route or custody holds it.
    internal bool IsIdleClientBoot(WorldInstance boot) {
        if ((m_remoteJoin is null) || m_remoteJoinListens || !m_remoteJoinPartySent) {
            return false;
        }
        if (IsOccupied(instance: boot)) {
            return false;
        }
        if (m_authorityEndpoints.TryGetValue(key: BootInstanceName, value: out var bootEndpoint)) {
            for (var slot = 0; (slot < m_seats.SeatCount); slot++) {
                if (m_seats.IsOccupied(slot: slot) && ReferenceEquals(objA: m_seats.RoutedEndpoint(slot: slot), objB: bootEndpoint)) {
                    return false;
                }
            }
        }
        if (HasForwardingFrom(source: boot.Server) || CustodyHeldBy(instance: boot).Holds || HasQueuedArrival(name: BootInstanceName)) {
            return false;
        }
        foreach (var pending in m_pendingTransfers) {
            if (string.Equals(a: pending.SourceInstance, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                return false;
            }
        }
        foreach (var inDoubt in m_inDoubtTransfers) {
            if (string.Equals(a: inDoubt.Transfer.SourceInstance, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                return false;
            }
        }
        return true;
    }

    // Whether the boot world withholds opening screen sessions because it is a non-listening client launchpad whose
    // seats either have not yet been dispatched on their boot join or have already crossed away. If a join refuses and
    // leaves a seat standing in boot after m_remoteJoinPartySent, this turns false and boot opens its sessions normally.
    internal bool SuppressesClientBootScreens(WorldInstance boot) =>
        ((m_remoteJoin is not null) && !m_remoteJoinListens && (!m_remoteJoinPartySent || IsIdleClientBoot(boot: boot)));

    // Lets the join send a seat again after its crossing came home because the authority no longer held the
    // reservation: the lease lapsed, which decided nothing about the seat. A reservation the authority refused is not
    // retried, since that is the authority's answer.
    private void RetryRemoteJoin(IReadOnlyList<LandedMember> members) {
        if (m_remoteJoin is null) { return; }
        foreach (var member in members) {
            if (((uint)member.SourceSlot) < ((uint)m_remoteJoinTried.Length)) { m_remoteJoinTried[member.SourceSlot] = 0; }
        }
    }

    /// <summary>Sends every local seat that stands in the boot world to a remote authority, now and as each one
    /// joins.</summary>
    /// <param name="documentPath">The document the boot world was loaded from, which names the world the remote
    /// authority is expected to hold.</param>
    /// <param name="authority">The remote authority's endpoint, an <c>ip:port</c> pair.</param>
    /// <param name="listens">Whether this host also binds a listen endpoint of its own.</param>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> or <paramref name="authority"/> is
    /// empty.</exception>
    public void JoinRemote(string documentPath, string authority, bool listens = false) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: authority);
        m_remoteJoin = TransferDestination.Remote(
            authority: authority,
            documentPath: documentPath,
            name: "remote-boot"
        );
        m_remoteJoinListens = listens;
        Array.Clear(array: m_remoteJoinTried);
        m_remoteJoinPartySent = false;
    }

    // Reads the boot world's active local seats and their generations into the scan's arrays; returns how many.
    private int ScanRemoteJoinSeats() {
        if (Boot is not { } boot) { return 0; }
        var population = boot.Server.Population;
        var count = 0;
        var eager = 0;

        foreach (var policy in boot.Server.Definition.Population.SeatActivation) {
            eager += ((policy == SeatActivationPolicy.Eager) ? 1 : 0);
        }
        m_remoteJoinEager = Math.Min(val1: eager, val2: population.LocalSeatCount);

        for (var slot = 0; (slot < population.LocalSeatCount); slot++) {
            if (!population.IsActive(index: slot)) { continue; }
            m_remoteJoinSeats[count] = slot;
            m_remoteJoinGenerations[count++] = (population.Generation(index: slot) + 1);
        }
        return count;
    }

    /// <summary>Enqueues the crossing of each local seat that stands in the boot world and has not been sent since it
    /// took its body. Call on the host loop's thread, before <see cref="DrainPendingTransfers"/>.</summary>
    /// <returns>The number of seats a crossing was enqueued for.</returns>
    public int FollowRemoteJoin() {
        if ((m_remoteJoin is not { } destination) || (Boot is not { } boot)) { return 0; }
        var active = boot.Server.ExecuteAuthorityOperation(operation: (m_remoteJoinScan ??= ScanRemoteJoinSeats));
        var untried = 0;

        for (var index = 0; (index < active); index++) {
            untried += ((m_remoteJoinTried[m_remoteJoinSeats[index]] == m_remoteJoinGenerations[index]) ? 0 : 1);
        }
        if ((untried == 0) || (!m_remoteJoinPartySent && (active < m_remoteJoinEager))) { return 0; }
        m_remoteJoinPartySent = true;
        var held = HeldCrossingSeats(instance: boot);
        Span<int> waiting = stackalloc int[active];
        var count = 0;

        for (var index = 0; (index < active); index++) {
            var slot = m_remoteJoinSeats[index];

            if (held.ContainsKey(key: slot) || (m_remoteJoinTried[slot] == m_remoteJoinGenerations[index])) { continue; }
            m_remoteJoinTried[slot] = m_remoteJoinGenerations[index];
            waiting[count++] = slot;
        }
        if (count == 0) { return 0; }
        // Every seat standing there travels as one party, in one reservation; a seat that joins while others have
        // gone travels by itself. A member the authority refuses does not hold the others back.
        if ((count == active) && (count > 1)) {
            _ = EnqueueTransfer(
                actingPrincipal: Principal.Console,
                destination: destination,
                holdSeconds: RemoteJoinHoldSeconds,
                partyAllOrNothing: false,
                scope: TransferScope.Party,
                sourceInstance: BootInstanceName,
                sourceSlot: waiting[0]
            );
        } else {
            for (var index = 0; (index < count); index++) {
                _ = EnqueueTransfer(
                    actingPrincipal: Principal.Console,
                    destination: destination,
                    holdSeconds: RemoteJoinHoldSeconds,
                    scope: TransferScope.Body,
                    sourceInstance: BootInstanceName,
                    sourceSlot: waiting[index]
                );
            }
        }
        return count;
    }
}
