using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>The desktop's <see cref="IWorldEmbodiedSeats"/> — the moved <c>WorldInstanceHost</c> engine's one seam
/// into <see cref="WorldClient"/>/<see cref="PlayerRoster"/>/<see cref="WorldSeatAuthorityRouter"/>/<see cref="InputRouter"/>.</summary>
internal sealed class WorldClientSeats(WorldClient client, PlayerRoster roster, WorldSeatAuthorityRouter seatRouter, Func<InputRouter> router) : IWorldEmbodiedSeats {
    /// <inheritdoc/>
    public int SeatCount => WorldSeatBindings.SeatCount;

    /// <inheritdoc/>
    public void AdvanceSeatViews(float deltaSeconds) => client.AdvanceSeatViews(deltaSeconds: deltaSeconds);
    /// <inheritdoc/>
    public void ClearAnalog() => roster.ClearAnalog();
    /// <inheritdoc/>
    public void ClearHeld(int slot) => _ = router().ClearSlotHeld(slot: slot);
    /// <inheritdoc/>
    public void ConfigureLeave(Func<int, WorldPrincipal, bool> leave) => roster.ConfigureLeave(leave: leave);
    /// <inheritdoc/>
    public bool IsOccupied(int slot) => (roster.Seat(slot: slot) is not null);
    /// <inheritdoc/>
    public bool OccupySeat(int slot, WorldIdentity? profile) => roster.OccupySeat(
        profile: profile,
        slot: slot
    );
    /// <inheritdoc/>
    public void PublishRoute(int slot, WorldAuthorityEndpoint endpoint, in WorldEntityAddress entity) => _ = seatRouter.Publish(
        endpoint: endpoint,
        entity: entity,
        slot: slot
    );
    /// <inheritdoc/>
    public WorldAuthorityEndpoint? RoutedEndpoint(int slot) => seatRouter.TryRoute(slot: slot)?.Endpoint;
    /// <inheritdoc/>
    public WorldEntityAddress RoutedEntity(int slot) => seatRouter.Route(slot: slot).Entity;
    /// <inheritdoc/>
    public void SubmitAuthorityIntents(WorldAuthorityEndpoint endpoint, ulong tick) => client.SubmitAuthorityIntents(
        endpoint: endpoint,
        tick: tick
    );
    /// <inheritdoc/>
    public bool TryUpdateRoutedEntity(int slot, WorldAuthorityEndpoint expectedEndpoint, in WorldEntityAddress replacement) {
        var expected = seatRouter.Route(slot: slot);

        if (!ReferenceEquals(
            objA: expected.Endpoint,
            objB: expectedEndpoint
        )) {
            return false;
        }

        return seatRouter.CompareExchangeEntity(
            current: out _,
            entity: replacement,
            expected: expected,
            slot: slot
        );
    }
    /// <inheritdoc/>
    public bool VacateSeat(int slot) => roster.VacateSeat(slot: slot);
}
