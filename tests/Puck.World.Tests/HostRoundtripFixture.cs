using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Shared setup for the <c>host-roundtrip-identity</c> law and its discriminating controls: a two-row
/// <see cref="WorldInstanceHost"/> with a committed cross-row transfer, checkpoint capture, and restore into a
/// fresh host — see <see cref="WorldAuthorityCheckpointHostRoundtripLawTests"/> for the scenario's own remarks on
/// why the moved seat stays local (never peer-range) and why row-a keeps a second occupant.</summary>
internal static class HostRoundtripFixture {
    /// <summary>Builds the two-row host, joins two local seats on row-a, steps both rows, drains a transfer of
    /// slot 0 to row-b, and steps a settled tail.</summary>
    public static (WorldInstanceHost Host, HostRow RowA, HostRow RowB, Guid MachineId) BuildCommittedScenario() {
        var machineId = Guid.NewGuid();
        var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: Directory.CreateTempSubdirectory(prefix: "puck-host-roundtrip-tests-").FullName
        );
        var rowA = HostRow.Build(name: "row-a");
        var rowB = HostRow.Build(name: "row-b");

        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);

        Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 1),
            Slot: 1,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        for (var tick = 0; (tick < 50); tick++) {
            host.DrainPendingTransfers();
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        _ = host.EnqueueTransfer(
            actingPrincipal: WorldPrincipal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0
        );
        host.DrainPendingTransfers();

        Assert.True(condition: rowB.Server.Population.IsActive(index: 0));

        for (var tick = 0; (tick < 50); tick++) {
            host.DrainPendingTransfers();
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        return (host, rowA, rowB, machineId);
    }
    /// <summary>Builds the two-row host, joins two local seats on row-a, drains a transfer of slot 0 to row-b through
    /// a <see cref="FaultingPeerCall"/> so the FIRST commit attempt reports <c>Unreachable</c> (a lease held,
    /// uncommitted, at the destination) and stops there — every caller must checkpoint immediately, before any
    /// further <see cref="WorldInstanceHost.DrainPendingTransfers"/> call, because that same call is what would
    /// retry the commit for real and resolve the in-doubt entry this scenario exists to capture.</summary>
    public static (WorldInstanceHost Host, HostRow RowA, HostRow RowB, Guid MachineId, ulong TransferId) BuildInDoubtScenario() {
        var machineId = Guid.NewGuid();
        var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: Directory.CreateTempSubdirectory(prefix: "puck-host-roundtrip-in-doubt-tests-").FullName
        );
        var rowA = HostRow.Build(name: "row-a");
        var rowB = HostRow.Build(name: "row-b");

        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);
        host.SetPeerCallFault(
            fault: new FaultingPeerCall(destination: rowB.Server),
            instanceName: "row-b"
        );

        Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 1),
            Slot: 1,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        for (var tick = 0; (tick < 50); tick++) {
            host.DrainPendingTransfers();
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        var transferId = host.EnqueueTransfer(
            actingPrincipal: WorldPrincipal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: 0
        );

        host.DrainPendingTransfers();

        Assert.False(condition: rowA.Server.Population.IsActive(index: 0));
        Assert.False(condition: rowB.Server.Population.IsActive(index: 0));
        Assert.Equal(expected: WorldTransferStatus.Reserved, actual: rowB.Server.TransferStatus(
            sourceAuthority: rowA.Server.AuthorityIdentity,
            transferId: transferId
        ));

        return (host, rowA, rowB, machineId, transferId);
    }
    /// <summary>The peer-range counterpart of <see cref="BuildCommittedScenario"/>: the crossing seat lands in the
    /// PEER range (<see cref="WorldPopulationLimits.LocalSeatCount"/>) rather than a local seat, so the destination's own
    /// entry captures <c>IsRemoteHuman</c> and restores PARKED (<c>WorldPopulation.Restore</c>'s own rule) — the
    /// divergence <see cref="WorldAuthorityCheckpointHostRoundtripLawTests"/>'s scenario (i) remarks name as the
    /// reason a peer-range crossing is not what that law drives. A caller resumes the destination's parked entry
    /// (<see cref="WorldPopulation.TryResumeParkedPeer"/>) before comparing tails — see
    /// <see cref="WorldAuthorityCheckpointHostRoundtripLawTests.Host_roundtrip_identity_peer_range_transfer"/> for
    /// why that resume is what makes the comparison hold.</summary>
    public static (WorldInstanceHost Host, HostRow RowA, HostRow RowB, Guid MachineId, int PeerSlot) BuildPeerRangeCommittedScenario() {
        var machineId = Guid.NewGuid();
        var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: Directory.CreateTempSubdirectory(prefix: "puck-host-roundtrip-peer-tests-").FullName
        );
        var document = Fixtures.BuildDocument() with {
            PopulationRaw = Fixtures.BuildDocument().Population with {
                CapacityRaw = (WorldPopulationLimits.LocalSeatCount + 1),
                NetworkPlayers = 1,
            },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        const int peerSlot = WorldPopulationLimits.LocalSeatCount;
        var rowA = HostRow.Build(definition: document, name: "row-a");
        var rowB = HostRow.Build(definition: document, name: "row-b");

        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);

        // A local seat that never leaves — otherwise the peer's own departure below empties row-a entirely and the
        // host reaps it (FinalizeCommittedTransfer's own ReapIfEmpty), which stops StepInstances from advancing it
        // any further and would make the identical-tail comparison meaningless (BuildCommittedScenario's own
        // remarks name the identical reason for keeping row-a a second occupant).
        Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(condition: rowA.Server.ExecuteAuthorityOperation(operation: () => rowA.Server.Population.TryAdmitRemotePeerAt(
            slot: peerSlot,
            source: IntentSource.Live,
            grantTemplates: [],
            identityDomain: "example.test",
            identitySubject: "traveler-1",
            admitted: out _,
            refusal: out _
        )));

        for (var tick = 0; (tick < 50); tick++) {
            host.DrainPendingTransfers();
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        _ = host.EnqueueTransfer(
            actingPrincipal: WorldPrincipal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "row-a",
            sourceSlot: peerSlot
        );
        host.DrainPendingTransfers();

        Assert.True(condition: rowB.Server.Population.IsActive(index: peerSlot));

        for (var tick = 0; (tick < 50); tick++) {
            host.DrainPendingTransfers();
            host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        return (host, rowA, rowB, machineId, peerSlot);
    }
    public static (WorldAuthorityCheckpoint A, WorldAuthorityCheckpoint B) CaptureBoth(WorldInstanceHost host, HostRow rowA, HostRow rowB) {
        Assert.True(condition: rowA.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpointA,
            hostRow: host.CaptureRow(row: rowA.Instance),
            reason: out var reasonA
        ), userMessage: reasonA);
        Assert.True(condition: rowB.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpointB,
            hostRow: host.CaptureRow(row: rowB.Instance),
            reason: out var reasonB
        ), userMessage: reasonB);

        return (checkpointA!, checkpointB!);
    }
    /// <summary>Round-trips a checkpoint through the wire codec exactly like a store write/read would — the
    /// structural-equality law <see cref="WorldAuthorityCheckpointCodecLawTests"/> already proves is applied here so
    /// a restore is always driven from the SAME bytes a real activation would load.</summary>
    public static WorldAuthorityCheckpoint EncodeDecode(WorldAuthorityCheckpoint checkpoint) {
        var encoded = WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: encoded,
            checkpoint: out var decoded,
            reason: out var reason
        ), userMessage: reason);

        return decoded!;
    }
    /// <summary>Restores both rows into a fresh host built with the SAME machine id — a re-materialized forwarded
    /// arm's <c>sourceAuthority</c> text is machine-id-derived (see <see cref="WorldInstanceHost.RestoreRow"/>), so a
    /// different id here would diverge a byte-identity comparison on a field the law itself introduces, never one the
    /// checkpoint completeness rule is about.</summary>
    public static (WorldInstanceHost Host, HostRow RowA, HostRow RowB) RestoreBoth(Guid machineId, WorldAuthorityCheckpoint checkpointA, WorldAuthorityCheckpoint checkpointB) {
        var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineId: machineId,
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: Directory.CreateTempSubdirectory(prefix: "puck-host-roundtrip-restore-tests-").FullName
        );

        static HostRow RestoredRow(WorldAuthorityCheckpoint checkpoint, string name) {
            var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
            var machines = new WorldMachineHost(engines: [], screens: definition.Screens);

            var (server, _) = WorldServer.FromCheckpoint(
                checkpoint: checkpoint,
                instanceIdentity: name,
                machines: machines,
                profiles: new WorldOwnedWorlds(
                    directory: Directory.CreateTempSubdirectory(prefix: $"puck-host-roundtrip-restore-tests-{name}-").FullName,
                    machineId: Guid.NewGuid(),
                    template: definition
                )
            );

            return HostRow.Wrap(machines: machines, name: name, server: server);
        }

        var rowA = RestoredRow(checkpoint: checkpointA, name: "row-a");
        var rowB = RestoredRow(checkpoint: checkpointB, name: "row-b");

        host.Admit(row: rowA.Instance);
        host.Admit(row: rowB.Instance);

        // Both rows are admitted before EITHER restore runs — a co-hosted forwarded arm's destination row must
        // already be in the registry (RestoreRow's own remarks).
        host.RestoreRow(row: rowA.Instance, slice: checkpointA.HostRow);
        host.RestoreRow(row: rowB.Instance, slice: checkpointB.HostRow);

        return (host, rowA, rowB);
    }
    public static void RunIdenticalTail(WorldInstanceHost uninterruptedHost, HostRow uninterruptedA, HostRow uninterruptedB, WorldInstanceHost restoredHost, HostRow restoredA, HostRow restoredB, int ticks) {
        for (var tick = 0; (tick < ticks); tick++) {
            uninterruptedHost.DrainPendingTransfers();
            uninterruptedHost.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            restoredHost.DrainPendingTransfers();
            restoredHost.StepInstances(masterDeltaTicks: Fixtures.StepTicks);

            Assert.Equal(
                expected: WorldReplaySnapshot.HashState(population: uninterruptedA.Server.Population),
                actual: WorldReplaySnapshot.HashState(population: restoredA.Server.Population)
            );
            Assert.Equal(
                expected: WorldReplaySnapshot.HashState(population: uninterruptedB.Server.Population),
                actual: WorldReplaySnapshot.HashState(population: restoredB.Server.Population)
            );
        }
    }
}
