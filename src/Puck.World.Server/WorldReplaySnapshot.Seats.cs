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
    // Arrival identity is the same projection a federation crossing or checkpoint preserves, including owned records.
    private static WorldArrival Landed(WorldReplayEntry.Arrival arrival, WorldPlayerDefaults defaults) {
        var profile = ((arrival.Profile is { } pin) ? WorldIdentity.FromProjection(defaults: defaults, projection: pin) : null);

        if (arrival.ProfileDocument.Length > 0) {
            profile!.ReplaceDocument(document: ReadArrivalProfileDocument(bytes: arrival.ProfileDocument));
        }
        return arrival.Value with { Profile = profile };
    }
    private static WorldDefinition ReadArrivalProfileDocument(byte[] bytes) {
        try {
            return WorldDefinitionSerialization.Deserialize(utf8Json: bytes);
        } catch (Exception exception) when ((exception is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)) {
            throw new InvalidDataException(innerException: exception, message: "arrival profile document is malformed");
        }
    }
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
        writer.WriteInt32(value: value.Generation);

        if (!WorldWireCodec.TryWritePrincipal(
            principal: value.Principal,
            writer: writer
        )) {
            throw new WorldReplayCodecException(message: $"arrival at body:{value.Slot} names principal {value.Principal.Describe()}, which has no wire value.");
        }

        writer.WriteBoolean(value: value.Peer);
        writer.WriteBoolean(value: arrival.RolledBack);
        writer.WriteOptional(
            value: arrival.Profile,
            writeValue: WorldAuthorityCheckpointCodec.WriteIdentityProjection
        );
        writer.WriteBlock(value: arrival.ProfileDocument);
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
        var generation = reader.ReadInt32();
        var principal = WorldWireCodec.ReadPrincipal(reader: ref reader);
        var peer = reader.ReadBoolean();
        var rolledBack = reader.ReadBoolean();
        var profile = reader.ReadOptional(readValue: static (ref WireReader r) => WorldAuthorityCheckpointCodec.ReadIdentityProjection(reader: ref r));
        var profileDocument = reader.ReadBlock(field: "arrival profile document", maxBytes: WireLimits.MaxDocumentBytes);

        if (!reader.Failed && (profileDocument.Length > 0)) {
            var definition = ReadArrivalProfileDocument(bytes: profileDocument);

            if ((profile is not { } projected) || (definition.Identity is not { } identity) || (identity.Id.ToString() != projected.Id)) {
                reader.Fail(detail: "arrival profile document has no matching identity", refusal: WireRefusal.PayloadMalformed);
            }
        }
        var bodyColor = reader.ReadFiniteVector(field: "arrival body color");
        var catalogRig = reader.ReadByte();
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        var border = reader.ReadString(field: "arrival border");
        var member = WorldWireLeaves.ReadCommitMemberMotion(reader: ref reader);

        if (!reader.Failed && ((((uint)slot) >= WorldBodiesLimits.CapacityCeiling) || (generation <= 0) ||
            (mobility.Epoch == 0) || (mobility.Incarnation.Index < 0) || (mobility.Incarnation.Generation < 0) ||
            (mobility.DepartedFrom.Index < 0) || (mobility.DepartedFrom.Generation < 0) ||
            (member.HasMappedArrival && string.IsNullOrWhiteSpace(value: member.BodyMotionProgramName)) ||
            ((member.Continuum is { } continuum) && (!member.HasMappedArrival ||
                (continuum.ContinuumEndEngineTick <= continuum.ContinuumStartEngineTick) ||
                (continuum.ConsumedThroughEngineTick < continuum.ContinuumEndEngineTick) ||
                (continuum.BoundaryEvents == 0) || (continuum.BoundaryEvents > WorldContinuumTrajectory.MaxBoundaryEvents))))) {
            reader.Fail(detail: "arrival carries an invalid slot, mobility or motion", refusal: WireRefusal.PayloadMalformed);
        }

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
            ProfileDocument: profileDocument,
            RolledBack: rolledBack,
            Value: new WorldArrival(
                BodyColor: bodyColor,
                Border: border,
                CatalogRig: catalogRig,
                Generation: generation,
                Member: member,
                Mobility: mobility,
                Peer: peer,
                Principal: principal,
                Profile: null,
                Slot: slot
            )
        );
    }
    private static void ValidateArrivalOrder(IReadOnlyList<WorldReplayEntry> entries) {
        var admitted = new Dictionary<int, WorldPeerEventEntry>();

        foreach (var entry in entries) {
            switch (entry) {
                case WorldReplayEntry.PeerAdmitted peers:
                    foreach (var peer in peers.Value.Entries) {
                        if (peer.AuthorityTransferred) {
                            admitted[peer.BodyIndex] = peer;
                        }
                    }
                    break;
                case WorldReplayEntry.PeerDisconnected peers:
                    foreach (var peer in peers.Value.Entries) {
                        admitted.Remove(key: peer.BodyIndex);
                    }
                    break;
                case WorldReplayEntry.Transfer transfer:
                    foreach (var slot in transfer.DepartedBootSlots) {
                        admitted.Remove(key: slot);
                    }
                    break;
                case WorldReplayEntry.Arrival { Value.Peer: true } arrival:
                    if (!admitted.Remove(key: arrival.Value.Slot, value: out var admission) ||
                        (admission.Generation != arrival.Value.Generation) ||
                        (admission.CatalogRig != arrival.Value.CatalogRig) || (admission.TravelTurn != arrival.Value.Member.TravelTurn)) {
                        throw new InvalidDataException(message: $"arrival at body:{arrival.Value.Slot} has no preceding matching PeerAdmitted entry");
                    }
                    break;
            }
        }
    }
}
