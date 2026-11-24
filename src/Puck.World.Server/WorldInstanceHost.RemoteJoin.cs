using Puck.Commands;

namespace Puck.World;

// A HOST STARTED TO JOIN ANOTHER AUTHORITY SENDS ITS SEATS THERE. The host boots its own copy of the world so its
// seats have bodies to send, and every local seat that stands in that copy crosses to the authority the host names:
// the seats active when the host starts as one party, in one reservation, and a seat that joins later by itself, when
// it joins. A seat is tried once for each body it takes: a refusal is narrated by the transfer and is not retried
// until the seat leaves and joins again, so a full authority is not asked every tick.
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

    /// <summary>Sends every local seat that stands in the boot world to a remote authority, now and as each one
    /// joins.</summary>
    /// <param name="documentPath">The document the boot world was loaded from, which names the world the remote
    /// authority is expected to hold.</param>
    /// <param name="authority">The remote authority's endpoint, an <c>ip:port</c> pair.</param>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> or <paramref name="authority"/> is
    /// empty.</exception>
    public void JoinRemote(string documentPath, string authority) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: authority);
        m_remoteJoin = TransferDestination.Remote(
            authority: authority,
            documentPath: documentPath,
            name: "remote-boot"
        );
        Array.Clear(array: m_remoteJoinTried);
    }

    // Reads the boot world's active local seats and their generations into the scan's arrays; returns how many.
    private int ScanRemoteJoinSeats() {
        if (Boot is not { } boot) { return 0; }
        var population = boot.Server.Population;
        var count = 0;

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
        if (untried == 0) { return 0; }
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
                    scope: TransferScope.Body,
                    sourceInstance: BootInstanceName,
                    sourceSlot: waiting[index]
                );
            }
        }
        return count;
    }
}
