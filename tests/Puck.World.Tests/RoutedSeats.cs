using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// An <see cref="IWorldEmbodiedSeats"/> over a real <see cref="WorldSeatAuthorityRouter"/>, mapped exactly as the
/// desktop's <c>WorldClientSeats</c> maps it, with no client, roster or input router behind it. A host composed over it
/// publishes, retargets and turns its local seats' routes as the desktop's does; this fake records each view turn and
/// each route delivery so a law can count them.
/// </summary>
internal sealed class RoutedSeats : IWorldEmbodiedSeats {
    private readonly Lock m_gate = new();
    private readonly List<(int Slot, FixedQ4816 YawDelta)> m_crossings = [];
    private readonly bool[] m_occupied = new bool[WorldSeatBindings.SeatCount];

    private int m_deliveries;

    /// <summary>The real seat router this fake publishes into.</summary>
    public WorldSeatAuthorityRouter Router { get; } = new();

    /// <summary>Each view turn the host asked for, in order, as (seat, yaw delta).</summary>
    public IReadOnlyList<(int Slot, FixedQ4816 YawDelta)> Crossings {
        get {
            lock (m_gate) {
                return [.. m_crossings];
            }
        }
    }
    /// <summary>The number of route retargets that landed (<see cref="TryUpdateRoutedEntity"/> answered
    /// <see langword="true"/>).</summary>
    public int Deliveries => Volatile.Read(location: ref m_deliveries);
    /// <summary>Runs inside each <see cref="TryUpdateRoutedEntity"/>, before the retarget, on the thread delivering the
    /// route — under the route gate when a route wrapper delivers it. Null runs nothing.</summary>
    public Action<int>? Retargeting { get; set; }
    /// <inheritdoc/>
    public int SeatCount => WorldSeatBindings.SeatCount;

    /// <inheritdoc/>
    public void AdvanceSeatViews(float deltaSeconds) { }
    /// <inheritdoc/>
    public void ClearAnalog() { }
    /// <inheritdoc/>
    public void ClearHeld(int slot) { }
    /// <inheritdoc/>
    public void ConfigureLeave(Func<int, Principal, bool> leave) { }
    /// <inheritdoc/>
    public void CrossView(int slot, FixedQ4816 yawDelta, WorldSeatYawReference yawReference) {
        lock (m_gate) {
            m_crossings.Add(item: (slot, yawDelta));
        }
    }
    /// <inheritdoc/>
    public bool IsOccupied(int slot) => Volatile.Read(location: ref m_occupied[slot]);
    /// <inheritdoc/>
    public bool OccupySeat(int slot, WorldIdentity? profile) {
        Volatile.Write(
            location: ref m_occupied[slot],
            value: true
        );

        return true;
    }
    /// <inheritdoc/>
    public void PublishRoute(int slot, WorldAuthorityEndpoint endpoint, in WorldEntityAddress entity) => _ = Router.Publish(
        endpoint: endpoint,
        entity: entity,
        slot: slot
    );
    /// <inheritdoc/>
    public WorldAuthorityEndpoint? RoutedEndpoint(int slot) => Router.TryRoute(slot: slot)?.Endpoint;
    /// <inheritdoc/>
    public WorldEntityAddress RoutedEntity(int slot) => Router.Route(slot: slot).Entity;
    /// <inheritdoc/>
    public void SubmitAuthorityIntents(WorldAuthorityEndpoint endpoint, ulong tick) { }
    /// <inheritdoc/>
    public bool TryUpdateRoutedEntity(int slot, WorldAuthorityEndpoint expectedEndpoint, in WorldEntityAddress replacement) {
        Retargeting?.Invoke(obj: slot);

        var expected = Router.Route(slot: slot);

        if (!ReferenceEquals(
            objA: expected.Endpoint,
            objB: expectedEndpoint
        )) {
            return false;
        }

        if (!Router.CompareExchangeEntity(
            current: out _,
            entity: replacement,
            expected: expected,
            slot: slot
        )) {
            return false;
        }

        _ = Interlocked.Increment(location: ref m_deliveries);

        return true;
    }
    /// <inheritdoc/>
    public bool VacateSeat(int slot) {
        Volatile.Write(
            location: ref m_occupied[slot],
            value: false
        );

        return true;
    }
}
