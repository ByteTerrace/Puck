using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The local seats a host embodies through the moved <see cref="WorldInstanceHost"/> engine — the ONLY seam between
/// that engine and a client's roster/seat-router/input-router. The desktop implements it over
/// <c>WorldClient</c>/<c>PlayerRoster</c>/<c>WorldSeatAuthorityRouter</c>/<c>InputRouter</c> (<c>Puck.World.Client.WorldClientSeats</c>);
/// a hosted silo embodies no local seats and passes <see cref="WorldEmbodiedSeats.None"/>. Exactly the shape 24
/// client-coupled call sites in the moved engine used before this seam existed — no more.
/// </summary>
public interface IWorldEmbodiedSeats {
    /// <summary>The number of local seats this host embodies — the desktop's authored seat count; 0 for a host with
    /// no local seats (a hosted silo).</summary>
    int SeatCount { get; }

    /// <summary>The endpoint currently routed for local seat <paramref name="slot"/>, or <see langword="null"/> when
    /// this host embodies no seats.</summary>
    WorldAuthorityEndpoint? RoutedEndpoint(int slot);
    /// <summary>The entity address currently routed for local seat <paramref name="slot"/> — <see langword="default"/>
    /// when this host embodies no seats.</summary>
    WorldEntityAddress RoutedEntity(int slot);
    /// <summary>Whether a roster participant currently occupies local seat <paramref name="slot"/>.</summary>
    bool IsOccupied(int slot);
    /// <summary>Publishes a new complete authority claim for local seat <paramref name="slot"/>.</summary>
    void PublishRoute(int slot, WorldAuthorityEndpoint endpoint, in WorldEntityAddress entity);
    /// <summary>Retargets local seat <paramref name="slot"/>'s routed entity only if it is still routed through
    /// <paramref name="expectedEndpoint"/> — the route-level CAS a background federation callback uses so a stale
    /// callback cannot overwrite a newer authority claim.</summary>
    /// <returns><see langword="true"/> when the retarget landed.</returns>
    bool TryUpdateRoutedEntity(int slot, WorldAuthorityEndpoint expectedEndpoint, in WorldEntityAddress replacement);
    /// <summary>Clears a departed local seat's roster occupancy.</summary>
    bool VacateSeat(int slot);
    /// <summary>Marks a local seat occupied by a newly arrived traveler.</summary>
    bool OccupySeat(int slot, WorldIdentity? profile);
    /// <summary>Clears a departed local seat's held input-layer state.</summary>
    void ClearHeld(int slot);
    /// <summary>Clears every local seat's tick-local analog samples.</summary>
    void ClearAnalog();
    /// <summary>Advances every local seat's own camera/view state.</summary>
    void AdvanceSeatViews(float deltaSeconds);
    /// <summary>Submits the intents of every local seat currently routed to <paramref name="endpoint"/> for
    /// <paramref name="tick"/>.</summary>
    void SubmitAuthorityIntents(WorldAuthorityEndpoint endpoint, ulong tick);
    /// <summary>Wires the roster's one leave-transaction door.</summary>
    void ConfigureLeave(Func<int, WorldPrincipal, bool> leave);
}
/// <summary>The inert <see cref="IWorldEmbodiedSeats"/> a host with no local seats passes — every member no-op or
/// answering the empty/absent case.</summary>
public static class WorldEmbodiedSeats {
    /// <summary>The one inert instance — stateless, so a single shared instance is correct.</summary>
    public static IWorldEmbodiedSeats None { get; } = new NoneSeats();

    private sealed class NoneSeats : IWorldEmbodiedSeats {
        public int SeatCount => 0;

        public WorldAuthorityEndpoint? RoutedEndpoint(int slot) => null;
        public WorldEntityAddress RoutedEntity(int slot) => default;
        public bool IsOccupied(int slot) => false;
        public void PublishRoute(int slot, WorldAuthorityEndpoint endpoint, in WorldEntityAddress entity) { }
        public bool TryUpdateRoutedEntity(int slot, WorldAuthorityEndpoint expectedEndpoint, in WorldEntityAddress replacement) => false;
        public bool VacateSeat(int slot) => false;
        public bool OccupySeat(int slot, WorldIdentity? profile) => false;
        public void ClearHeld(int slot) { }
        public void ClearAnalog() { }
        public void AdvanceSeatViews(float deltaSeconds) { }
        public void SubmitAuthorityIntents(WorldAuthorityEndpoint endpoint, ulong tick) { }
        public void ConfigureLeave(Func<int, WorldPrincipal, bool> leave) { }
    }
}
