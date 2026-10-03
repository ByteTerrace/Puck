using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldReplaySnapshot {
    /// <summary>Re-joins this recording's seats into <paramref name="server"/> and re-seats each profiled one on a
    /// detached handle carrying its pinned identity projection — the recorded locomotion rates and records the
    /// authoritative hash reads, never the live catalog's current ones, which are only read for the drift report.
    /// Shared by the offline <see cref="Drive"/> and the live drive's boot image.</summary>
    /// <param name="server">A server at its boot image, with no seat joined yet.</param>
    /// <param name="population">That server's population.</param>
    /// <param name="definition">The embedded definition, for the pinned handle's player defaults.</param>
    /// <param name="profiles">The live catalog the drift report reads.</param>
    internal void SeatRecordedSeats(WorldServer server, WorldPopulation population, WorldDefinition definition, WorldOwnedWorlds profiles) {
        foreach (var seat in Seats) {
            // Seat(slot) directly: there is no PlayerRoster (and so no claim) behind this join to ask PrincipalOf of.
            _ = server.ApplySession(request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: seat.Slot),
                Slot: seat.Slot,
                IdentityName: seat.Profile?.Name,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            ));

            if (seat.Profile is not { } pin) {
                continue;
            }

            ReportProfileDrift(
                pin: pin,
                profiles: profiles
            );
            population.SetSeatProfile(
                slot: seat.Slot,
                profile: WorldIdentity.FromProjection(
                    projection: pin,
                    defaults: definition.PlayerDefaults
                )
            );
        }
    }

    private static WorldIdentityProjection? ReadProfilePin(ref WireReader reader) => WorldIdentityProjectionWire.ReadOptional(reader: ref reader);
}
