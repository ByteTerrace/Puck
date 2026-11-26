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
    // Host steps completed while the non-listening client's boot world was idle: keeps the host's script/capture clock
    // monotonic without advancing the idle boot server's own simulation state.
    private ulong m_idleClientTicks;
    private ulong m_lastBootInputTick;
    // Local seats whose traveler a remote authority answered had ended there, queued by the route's observer thread and
    // returned home on the host loop.
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> m_endedSeatRoutes = new();
    // For each local seat that follows a traveler at a remote authority, the identity it travelled as, which it is
    // seated as again when it returns as a new body.
    private readonly string?[] m_routedSeatIdentities = new string?[WorldBodiesLimits.LocalSeatCount];

    /// <summary>How long, in seconds of the destination's time, a joining seat's reservation holds its slot. A host
    /// that joins at boot can take seconds to step its own first ticks, and a reservation that lapsed before its commit
    /// arrived decides nothing, so the join asks for a hold that outlasts a slow start.</summary>
    public const double RemoteJoinHoldSeconds = 60.0;

    /// <summary>Gets the host's completed script tick: the boot authority's completed simulation tick plus any fixed host
    /// steps completed while an idle non-listening client kept its boot world paused.</summary>
    /// <param name="bootServer">The boot authority server.</param>
    /// <returns>The monotonic completed host script tick.</returns>
    public ulong HostScriptTick(Server.WorldServer bootServer) {
        ArgumentNullException.ThrowIfNull(argument: bootServer);
        var nextInput = bootServer.NextInputTick;

        if (nextInput < m_lastBootInputTick) {
            m_idleClientTicks = 0UL;
        }
        m_lastBootInputTick = nextInput;
        return (((nextInput > 0UL) ? (nextInput - 1UL) : 0UL) + m_idleClientTicks);
    }

    /// <summary>Advances the host's idle-client script clock by one fixed step when the boot world is withheld solely
    /// because it is an idle non-listening client launchpad, without stepping the boot authority's simulation.</summary>
    /// <param name="stepTicks">This call's pump-supplied step width.</param>
    /// <param name="scriptTick">The completed host script tick after advancing.</param>
    /// <returns><see langword="true"/> when the host clock advanced over an idle client boot step.</returns>
    public bool TryAdvanceIdleClientBoot(ulong stepTicks, out ulong scriptTick) {
        scriptTick = 0UL;
        if (Boot is not { } boot) {
            return false;
        }
        var rateHz = boot.Server.Configuration.SimulationRateHz;

        if (
            boot.IsPaused ||
            !IsIdleClientBoot(boot: boot) ||
            (rateHz <= 0) ||
            (stepTicks != Puck.Maths.FixedTickConversion.PerRate(ratePerSecond: ((uint)rateHz)))
        ) {
            return false;
        }
        m_idleClientTicks++;
        scriptTick = HostScriptTick(bootServer: boot.Server);
        return true;
    }

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

    // Whether a boot-launchpad crossing is still queued or in flight, before commit or refusal has settled it.
    private bool HasPendingBootJoin(WorldInstance boot) {
        foreach (var pending in m_pendingTransfers) {
            if (string.Equals(a: pending.SourceInstance, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }
        foreach (var inDoubt in m_inDoubtTransfers) {
            if (string.Equals(a: inDoubt.Transfer.SourceInstance, b: BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }
        return (HeldCrossingSeats(instance: boot).Count > 0);
    }

    // Whether the boot world withholds opening screen sessions because it is a non-listening client launchpad whose
    // seats have not yet been dispatched on their boot join, are still crossing, or have already crossed away. If a join
    // refuses and leaves a seat standing in boot after the crossing settles, this turns false and boot opens its sessions
    // normally.
    internal bool SuppressesClientBootScreens(WorldInstance boot) =>
        ((m_remoteJoin is not null) && !m_remoteJoinListens && (!m_remoteJoinPartySent || HasPendingBootJoin(boot: boot) || IsIdleClientBoot(boot: boot)));

    // Lets the join send a seat again after its crossing came home because the authority no longer held the
    // reservation: the lease lapsed, which decided nothing about the seat. A reservation the authority refused is not
    // retried, since that is the authority's answer.
    private void RetryRemoteJoin(IReadOnlyList<LandedMember> members) {
        if (m_remoteJoin is null) { return; }
        foreach (var member in members) {
            if (((uint)member.SourceSlot) < ((uint)m_remoteJoinTried.Length)) { m_remoteJoinTried[member.SourceSlot] = 0; }
        }
    }

    // Returns home every local seat whose remote authority answered that its traveler had ended. Runs on the host loop.
    private void ReturnEndedSeats() {
        while (m_endedSeatRoutes.TryDequeue(result: out var slot)) {
            ReturnSeatHome(seat: true, slot: slot);
        }
    }

    /// <summary>Brings a local seat back from a remote authority that no longer holds its traveler
    /// (<see cref="Server.WorldTransferStatus.Ended"/>, <see cref="Server.WorldFederationRefusal.SessionEnded"/>): retires the route
    /// the seat followed and points the seat at the boot world again. The seat's next join there is a first entry.
    /// A seat that already stands in the boot world is left as it is. Call on the host loop's thread.</summary>
    /// <param name="slot">The local seat.</param>
    /// <param name="seat">Whether to seat the player in the boot world as a new body: <see langword="true"/> when the
    /// seat's body had been handed over for good, <see langword="false"/> when the caller restored the body the seat
    /// left with.</param>
    public void ReturnSeatHome(int slot, bool seat) {
        if (
            (Boot is not { } boot) ||
            (((uint)slot) >= ((uint)Math.Min(val1: m_seats.SeatCount, val2: m_routedSeatIdentities.Length))) ||
            (m_seats.RoutedEndpoint(slot: slot) is not { } location) ||
            string.Equals(a: location.Identity, b: BootInstanceName, comparisonType: StringComparison.Ordinal)
        ) {
            return;
        }
        m_seats.ClearHeld(slot: slot);

        var seated = string.Empty;

        if (seat) {
            var reply = boot.Server.ApplySession(request: new Protocol.SessionRequest.Join(
                IdentityName: m_routedSeatIdentities[slot],
                Principal: Principal.Seat(slot: slot),
                Slot: slot,
                WireProtocolKey: Protocol.WorldProtocol.WireProtocolKey
            ));

            seated = (reply.Accepted ? ", seated again as a new body" : $", and its seat could not be taken again ({reply.Reason})");
        }
        m_seats.PublishRoute(
            endpoint: EndpointFor(instance: boot),
            entity: new Protocol.WorldEntityAddress(
                Authority: boot.Server.AuthorityIdentity,
                Generation: boot.Server.Population.Generation(index: slot),
                Index: slot
            ),
            slot: slot
        );
        RetireTravelerRoute(location: location);
        m_routedSeatIdentities[slot] = null;
        m_remoteJoinTried[slot] = 0;
        if (m_narration.HasNarrationSink) {
            m_narration.Narrate(
                channel: "world.transfer",
                text: $"[world.transfer: seat {(slot + 1)} returned to '{BootInstanceName}': its traveler ended at '{location.Identity}'{seated}]"
            );
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
        if (Boot is { } boot) {
            SettleScreenSessions(instance: boot, stepped: false);
        }
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
