using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldReplaySnapshot {
    /// <summary>Re-joins this recording's seats into <paramref name="server"/> and re-seats each profiled one on a
    /// detached handle carrying its pinned locomotion rates — the recorded values, never the live catalog's current
    /// ones, which are only read for the drift report. Shared by the offline <see cref="Drive"/> and the live drive's
    /// boot image.</summary>
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
                profile: WorldIdentity.Pinned(
                    name: pin.Name,
                    moveSpeed: pin.MoveSpeed,
                    turnSpeed: pin.TurnSpeed,
                    defaults: definition.PlayerDefaults
                )
            );
        }
    }

    private static WorldReplayProfilePin? ReadProfilePin(ref WireReader reader) => reader.ReadOptional(readValue: static (ref WireReader r) => {
        var name = r.ReadString(field: "seat profile name");
        var moveSpeed = r.ReadNullableFixed();
        var turnSpeed = r.ReadNullableFixed();

        return new WorldReplayProfilePin(
            MoveSpeed: moveSpeed,
            Name: name,
            TurnSpeed: turnSpeed
        );
    });
    // The arrival a re-drive lands: the recorded one, its profile re-seated on the pinned rates against the embedded
    // definition's player defaults, exactly as SeatRecordedSeats re-seats a recorded seat.
    private static WorldArrival Landed(WorldReplayEntry.Arrival arrival, WorldPlayerDefaults defaults) => (arrival.Value with {
        Profile = ((arrival.Profile is { } pin)
        ? WorldIdentity.Pinned(
            defaults: defaults,
            moveSpeed: pin.MoveSpeed,
            name: pin.Name,
            turnSpeed: pin.TurnSpeed
        )
        : null),
    });
    private static void WriteProfilePin(WireWriter writer, WorldReplayProfilePin? pin) => writer.WriteOptional(
        value: pin,
        writeValue: static (pinWriter, value) => {
            pinWriter.WriteString(value: value.Name);
            pinWriter.WriteNullableFixed(value: value.MoveSpeed);
            pinWriter.WriteNullableFixed(value: value.TurnSpeed);
        }
    );
    private static void WriteArrivalEntry(WireWriter writer, WorldReplayEntry.Arrival arrival) {
        var value = arrival.Value;

        writer.WriteByte(value: 19);
        writer.WriteInt32(value: value.Slot);

        if (!WorldWireCodec.TryWritePrincipal(
            principal: value.Principal,
            writer: writer
        )) {
            throw new WorldReplayCodecException(message: $"arrival at body:{value.Slot} names principal {value.Principal.Describe()}, which has no wire value.");
        }

        writer.WriteBoolean(value: value.Peer);
        writer.WriteBoolean(value: arrival.RolledBack);
        WriteProfilePin(
            pin: arrival.Profile,
            writer: writer
        );
        writer.WriteVector(value: value.BodyColor);
        writer.WriteByte(value: value.CatalogRig);
        WorldWireLeaves.WriteMobility(
            mobility: value.Mobility,
            writer: writer
        );
        writer.WriteString(value: value.Border);
        WorldWireLeaves.WriteCommitMemberMotion(
            member: value.Member,
            writer: writer
        );
    }
    private static WorldReplayEntry.Arrival ReadArrivalEntry(ref WireReader reader) {
        var slot = reader.ReadInt32();
        var principal = WorldWireCodec.ReadPrincipal(reader: ref reader);
        var peer = reader.ReadBoolean();
        var rolledBack = reader.ReadBoolean();
        var profile = ReadProfilePin(reader: ref reader);
        var bodyColor = reader.ReadFiniteVector(field: "arrival body color");
        var catalogRig = reader.ReadByte();
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        var border = reader.ReadString(field: "arrival border");
        var member = WorldWireLeaves.ReadCommitMemberMotion(reader: ref reader);

        if (
            !reader.Failed &&
            (catalogRig >= WorldLookSource.Catalog.RigCount)
        ) {
            reader.Fail(
                detail: $"arrival catalog rig {catalogRig} is outside 0..{(WorldLookSource.Catalog.RigCount - 1)}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return new WorldReplayEntry.Arrival(
            Profile: profile,
            RolledBack: rolledBack,
            Value: new WorldArrival(
                BodyColor: bodyColor,
                Border: border,
                CatalogRig: catalogRig,
                Member: member,
                Mobility: mobility,
                Peer: peer,
                Principal: principal,
                Profile: null,
                Slot: slot
            )
        );
    }
}
