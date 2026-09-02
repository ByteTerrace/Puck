using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>
/// The five discriminating controls §3.5 names for <c>host-roundtrip-identity</c>: each corrupts (or, where the
/// captured record has no field to corrupt, simulates the counterfactual directly against) one piece of the
/// checkpoint and proves the restored trajectory or read-back reflects it — the same "the restore path must
/// actually consult the captured value, not silently ignore it" pattern
/// <see cref="WorldAuthorityCheckpointLawTests.Activation_roundtrip_identity_control_corrupted_generation_reads_red"/>
/// already runs for <c>Generation</c>. Every control here is discriminating: reverting the production change it
/// targets turns the assertion red (verified by hand while landing each one; see the report for the transcript).
/// </summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class WorldAuthorityCheckpointHostRoundtripControlTests {
    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
    private static WorldServer Restore(WorldAuthorityCheckpoint checkpoint, string instanceIdentity = "boot") {
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
        var machines = new WorldMachineHost(engines: [], screens: definition.Screens);

        var (server, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: instanceIdentity,
            machines: machines,
            profiles: new WorldOwnedWorlds(
                directory: Directory.CreateTempSubdirectory(prefix: "puck-host-roundtrip-control-tests-").FullName,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        return server;
    }

    // Control 1 — corrupt one IntegrationResidue remainder by one raw unit.
    [Fact]
    public void Control_CorruptedIntegrationResidueRemainder_ReadsRed() {
        using var fixture = Fixtures.FreshServer();

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        for (var tick = 0; (tick < 100); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: EmptyHostRow(), reason: out _));

        var entries = checkpoint!.Population.Entries;
        var original = entries[0].Residue;
        var corruptedResidue = original with { PositionRemainderX = (original.PositionRemainderX + 1) };
        var corrupted = checkpoint with {
            Population = checkpoint.Population with {
                Entries = [entries[0] with { Residue = corruptedResidue }, .. entries.Skip(count: 1)],
            },
        };

        var restored = Restore(checkpoint: corrupted);
        var restoredResidue = restored.Body(index: entries[0].Index)!.CaptureIntegrationResidue();

        Assert.NotEqual(expected: original.PositionRemainderX, actual: restoredResidue.PositionRemainderX);
        Assert.Equal(expected: corruptedResidue.PositionRemainderX, actual: restoredResidue.PositionRemainderX);
    }
    // Control 2 — drop (falsify) m_ruleGateHeld: a rule whose gate has never held is captured with no latch entry;
    // forging a "held" entry for it must show up in the world.rules read-back, proving restore actually installs
    // the captured latch table rather than leaving every gate open by default.
    [Fact]
    public void Control_ForgedRuleGateHeld_ReadsRed() {
        var ruleName = WorldCellName.Parse(candidate: "never-holds");
        // A reserved channel (never a custom State row): WorldOwnedWorlds seeds each authored identity from a
        // TRIMMED copy of the template document that drops State, so a rule gated on a custom state row refuses at
        // load for the identity catalog even though the live server accepts it fine — $population needs no row at
        // all and can never exceed the signed integer carrier's maximum, so this gate is provably always false.
        var definition = Fixtures.BuildDocument() with {
            Rules = [
                new WorldRule(
                    Name: ruleName,
                    Gate: new ActionPredicate.CompareState(State: "$population", Comparison: ActionStateComparison.Greater, Value: long.MaxValue),
                    Effects: [new ActionEffect.Save()]),
            ],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: EmptyHostRow(), reason: out _));
        Assert.DoesNotContain(expected: (ruleName.Value, true), collection: checkpoint!.Server.RuleGateHeld);

        var corrupted = checkpoint with {
            Server = checkpoint.Server with {
                RuleGateHeld = [.. checkpoint.Server.RuleGateHeld, (ruleName.Value, true)],
            },
        };

        var restoredHonest = Restore(checkpoint: checkpoint);
        var restoredCorrupted = Restore(checkpoint: corrupted);
        var honestText = restoredHonest.Answer(query: new WorldQuery.Rules()).Text;
        var corruptedText = restoredCorrupted.Answer(query: new WorldQuery.Rules()).Text;

        Assert.Contains(actualString: honestText, comparisonType: StringComparison.Ordinal, expectedSubstring: "latch=open");
        Assert.Contains(actualString: corruptedText, comparisonType: StringComparison.Ordinal, expectedSubstring: "latch=held");
    }
    // Control 3 — leave a remote-human entry unparked: a captured entry marked IsRemoteHuman with Parked=false must
    // be parked as of the restore tick (WorldPopulation.Restore's own new rule) — proving the field is consulted,
    // not merely round-tripped structurally.
    [Fact]
    public void Control_UnparkedRemoteHumanEntry_ReadsRed() {
        using var fixture = Fixtures.FreshServer();

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: WorldPrincipal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: EmptyHostRow(), reason: out _));

        var entries = checkpoint!.Population.Entries;

        Assert.False(condition: entries[0].IsRemoteHuman);

        var corrupted = checkpoint with {
            Population = checkpoint.Population with {
                Entries = [entries[0] with { IsRemoteHuman = true, Parked = false, ParkedUntilTick = null }, .. entries.Skip(count: 1)],
            },
        };

        var restoredHonest = Restore(checkpoint: checkpoint);
        var restoredCorrupted = Restore(checkpoint: corrupted);

        Assert.False(condition: restoredHonest.Population.IsSeatParked(slot: entries[0].Index));
        Assert.True(condition: restoredCorrupted.Population.IsSeatParked(slot: entries[0].Index));
    }
    // Control 4 — m_federatedIntents is excluded from the checkpoint by design: "restore with one row left Active
    // (the parked body moves)". There is no field to corrupt (the checkpoint carries none). A direct behavioral
    // reproduction is NOT possible either, but not for the reason once supposed here: Parked does not gate a body's
    // advance at all (verified directly — see ReconnectingPeer_ResumesTheSameBody_ControlAFreshAdmissionMintsADuplicate's
    // own remarks), so a corrupted-Active row WOULD move a parked body exactly like an honest one — the exclusion
    // is real, but the reason it cannot be demonstrated this way is that the checkpoint carries no field to force
    // Active in the first place, not that motion is gated. No public read-back exposes the latch itself to assert on
    // directly either. Named, not faked. What this control DOES prove, extending
    // Control_UnparkedRemoteHumanEntry_ReadsRed from the local-seat range control 3 covers into the PEER range: a
    // captured non-parked IsRemoteHuman peer entry — the exact shape a live federated arrival leaves — is parked as
    // of the restore tick too, through WorldPopulation.IsParked (WorldPopulation.IsSeatParked, control 3's own
    // check, only covers slot &lt; LocalSeatCount).
    [Fact]
    public void Control_UnparkedRemoteHumanPeerEntry_ReadsRed() {
        var document = Fixtures.BuildDocument() with {
            PopulationRaw = Fixtures.BuildDocument().Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1),
                NetworkPlayers = 1,
            },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        const int peerSlot = WorldBodiesLimits.LocalSeatCount;

        using var fixture = Fixtures.FreshServer(definition: document);

        Assert.True(condition: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.Population.TryAdmitRemotePeerAt(
            slot: peerSlot,
            source: IntentSource.Live,
            grantTemplates: [],
            identityDomain: string.Empty,
            identitySubject: string.Empty,
            admitted: out _,
            refusal: out _
        )));

        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: EmptyHostRow(), reason: out _));
        Assert.False(condition: checkpoint!.Population.Entries.Single(predicate: e => (e.Index == peerSlot)).Parked);

        var restoredHonest = Restore(checkpoint: checkpoint);
        // The counterfactual: a checkpoint that captured this row as already parked (unchanged by restore's own
        // rule either way) never proves the rule fired — corrupting it to explicitly UNparked is what makes a
        // restore that failed to consult IsRemoteHuman observably different from one that does.
        var forcedUnparked = checkpoint with {
            Population = checkpoint.Population with {
                Entries = [.. checkpoint.Population.Entries.Select(selector: e => ((e.Index == peerSlot) ? (e with { Parked = false, ParkedUntilTick = null }) : e))],
            },
        };
        var restoredCorrupted = Restore(checkpoint: forcedUnparked);

        Assert.True(condition: restoredHonest.Population.IsParked(index: peerSlot));
        Assert.True(condition: restoredCorrupted.Population.IsParked(index: peerSlot));
    }
    // Extends Control 4's own peer-range park proof with the piece it named as missing: a public un-park entry
    // point. WorldPopulation.TryResumeParkedPeer (the peer-range counterpart of TryResumeParkedSeat) resumes a
    // genuinely reconnecting peer's SAME retained body — same slot, same generation, same grant table — in place.
    // The control is what a reconnect does WITHOUT it: the ordinary fresh-admission door
    // (TryAdmitRemotePeer/HighestFreeSlot) never revisits an Active-and-Parked slot, so it mints a SECOND body at a
    // different index for the identical identity, leaving the first one parked and orphaned — one real peer now
    // controlling two bodies, which is the duplication a reconnect must not cause.
    //
    // (Movement is not what discriminates here: a still-parked body's federated intent is NOT gated by Parked at
    // all — WorldServer.ApplyFederatedIntents reads IsAdmittedPeer/principal-match only, and the advance pass
    // (WorldPopulation.AdvanceSimulated) reads only Active. Verified directly: an un-resumed, still-parked body's
    // position integrates a fresh held intent exactly like a resumed one's — Control 4's own "gates on Parked
    // structurally" premise above does not hold. This is why the discriminating claim below is identity/slot
    // preservation, not motion.)
    [Fact]
    public void ReconnectingPeer_ResumesTheSameBody_ControlAFreshAdmissionMintsADuplicate() {
        var document = Fixtures.BuildDocument() with {
            PopulationRaw = Fixtures.BuildDocument().Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 2),
                NetworkPlayers = 2,
            },
        };
        const int peerSlot = WorldBodiesLimits.LocalSeatCount;

        using var fixture = Fixtures.FreshServer(definition: document);
        var admitted = default(WorldPeerEventEntry);

        Assert.True(condition: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.Population.TryAdmitRemotePeerAt(
            slot: peerSlot,
            source: IntentSource.Live,
            grantTemplates: [],
            identityDomain: "example.test",
            identitySubject: "traveler-1",
            admitted: out admitted,
            refusal: out _
        )));

        for (var tick = 0; (tick < 10); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: EmptyHostRow(), reason: out _));

        var entry = checkpoint!.Population.Entries.Single(predicate: e => (e.Index == peerSlot));

        Assert.False(condition: entry.Parked);

        // Positive: a genuine reconnect resumes the SAME slot and the SAME generation — not a fresh mint.
        var restoredHonest = Restore(checkpoint: checkpoint);

        Assert.True(condition: restoredHonest.Population.IsParked(index: peerSlot));
        Assert.True(condition: restoredHonest.Population.TryResumeParkedPeer(
            identityDomain: entry.IdentityDomain,
            identitySubject: entry.IdentitySubject,
            admitted: out var resumed
        ));
        Assert.Equal(expected: peerSlot, actual: resumed.BodyIndex);
        Assert.Equal(expected: admitted.Generation, actual: resumed.Generation);
        Assert.False(condition: restoredHonest.Population.IsParked(index: peerSlot));

        // Control: the SAME restore, but the reconnect goes through the ordinary fresh-admission door instead.
        var restoredNaive = Restore(checkpoint: checkpoint);
        var freshlyMinted = default(WorldPeerEventEntry);

        Assert.True(condition: restoredNaive.ExecuteAuthorityOperation(operation: () => restoredNaive.Population.TryAdmitRemotePeer(
            source: IntentSource.Live,
            grantTemplates: [],
            identityDomain: entry.IdentityDomain,
            identitySubject: entry.IdentitySubject,
            admitted: out freshlyMinted,
            refusal: out _
        )));
        Assert.NotEqual(expected: peerSlot, actual: freshlyMinted.BodyIndex);
        // The original body is still there, still parked, still orphaned — the SAME identity now drives two bodies.
        Assert.True(condition: restoredNaive.Population.IsParked(index: peerSlot));
    }
    // Control 5 — restore the source row with NextTransferId reset: the next minted id collides with one this row
    // already applied, and the drain refuses it by name rather than double-landing a second traveler under the
    // reused id.
    [Fact]
    public void Control_ResetNextTransferId_CollidesWithAnAppliedId_RefusesByName() {
        var (host, rowA, rowB, machineId) = HostRoundtripFixture.BuildCommittedScenario();
        using var disposeA = rowA;
        using var disposeB = rowB;

        var (checkpointA, checkpointB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);

        Assert.Equal(expected: 1UL, actual: checkpointA.HostRow.NextTransferId);
        Assert.Contains(expected: 0UL, collection: checkpointA.HostRow.AppliedTransferIds);

        var corruptedA = checkpointA with {
            HostRow = checkpointA.HostRow with { NextTransferId = 0 },
        };
        var decodedA = HostRoundtripFixture.EncodeDecode(checkpoint: corruptedA);
        var decodedB = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB);

        var (restoredHost, restoredA, restoredB) = HostRoundtripFixture.RestoreBoth(checkpointA: decodedA, checkpointB: decodedB, machineId: machineId);
        using var disposeRestoredA = restoredA;
        using var disposeRestoredB = restoredB;

        Assert.Equal(expected: 0UL, actual: restoredA.Instance.NextTransferId);

        var originalError = Console.Error;
        using var captured = new StringWriter();

        Console.SetError(newError: captured);

        try {
            _ = restoredHost.EnqueueTransfer(
                actingPrincipal: WorldPrincipal.Console,
                destination: WorldInstanceHost.TransferDestination.Existing(name: "row-b"),
                scope: WorldInstanceHost.TransferScope.Body,
                sourceInstance: "row-a",
                sourceSlot: 1
            );
            restoredHost.DrainPendingTransfers();
        } finally {
            Console.SetError(newError: originalError);
        }

        Assert.Contains(expectedSubstring: "already applied", actualString: captured.ToString(), comparisonType: StringComparison.Ordinal);
        // The body never moved: refused before any detach, exactly like an ordinary already-applied replay.
        Assert.True(condition: restoredA.Server.Population.IsActive(index: 1));
    }
    // Control 6 — restore with the in-doubt entry dropped: the reservation is never asked to resolve, so it just
    // sits Reserved at the destination (leaked) while the body is nowhere active on either restored row (lost).
    [Fact]
    public void Control_DroppedInDoubtEntry_LeaksTheReservationAndLosesTheBody() {
        var (host, rowA, rowB, machineId, transferId) = HostRoundtripFixture.BuildInDoubtScenario();
        using var disposeA = rowA;
        using var disposeB = rowB;

        var (checkpointA, checkpointB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);

        Assert.Single(collection: checkpointA.HostRow.InDoubtTransfers);

        var droppedA = checkpointA with {
            HostRow = checkpointA.HostRow with { InDoubtTransfers = [] },
        };
        var decodedA = HostRoundtripFixture.EncodeDecode(checkpoint: droppedA);
        var decodedB = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB);

        var (restoredHost, restoredA, restoredB) = HostRoundtripFixture.RestoreBoth(checkpointA: decodedA, checkpointB: decodedB, machineId: machineId);
        using var disposeRestoredA = restoredA;
        using var disposeRestoredB = restoredB;

        for (var tick = 0; (tick < 200); tick++) {
            restoredHost.DrainPendingTransfers();
            restoredHost.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        Assert.False(condition: restoredA.Server.Population.IsActive(index: 0));
        Assert.False(condition: restoredB.Server.Population.IsActive(index: 0));
        Assert.Equal(
            actual: restoredB.Server.TransferStatus(sourceAuthority: restoredA.Server.AuthorityIdentity, transferId: transferId),
            expected: WorldTransferStatus.Reserved
        );
    }
    // Control 7 — restore with CommitMembers emptied: RestoreRow's own member-count check (WorldInstanceHost's own
    // remarks — a retried Commit's member-count mismatch would otherwise release the destination's lease as a side
    // effect of refusing, silently rolling the whole transfer back) refuses to re-materialize the entry, by name, on
    // the source row's own state channel, before any live call.
    [Fact]
    public void Control_EmptiedCommitMembers_RestoreRefusesByName() {
        var (host, rowA, rowB, machineId, _) = HostRoundtripFixture.BuildInDoubtScenario();
        using var disposeA = rowA;
        using var disposeB = rowB;

        var (checkpointA, checkpointB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);

        var original = Assert.Single(collection: checkpointA.HostRow.InDoubtTransfers);

        Assert.NotEmpty(collection: original.CommitMembers);

        var emptiedA = checkpointA with {
            HostRow = checkpointA.HostRow with {
                InDoubtTransfers = [original with { CommitMembers = [] }],
            },
        };
        var decodedA = HostRoundtripFixture.EncodeDecode(checkpoint: emptiedA);
        var decodedB = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB);

        var originalError = Console.Error;
        using var captured = new StringWriter();

        Console.SetError(newError: captured);

        WorldInstanceHost restoredHost;
        HostRow restoredA;
        HostRow restoredB;

        try {
            (restoredHost, restoredA, restoredB) = HostRoundtripFixture.RestoreBoth(checkpointA: decodedA, checkpointB: decodedB, machineId: machineId);
        } finally {
            Console.SetError(newError: originalError);
        }

        using var disposeRestoredA = restoredA;
        using var disposeRestoredB = restoredB;

        Assert.Contains(
            actualString: captured.ToString(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "commit member count"
        );
        // The refused entry never entered m_inDoubtTransfers, so a full tail never resolves it — the destination's
        // reservation is left exactly as Control_DroppedInDoubtEntry_LeaksTheReservationAndLosesTheBody's own.
        for (var tick = 0; (tick < 50); tick++) {
            restoredHost.DrainPendingTransfers();
            restoredHost.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        }

        Assert.False(condition: restoredA.Server.Population.IsActive(index: 0));
        Assert.False(condition: restoredB.Server.Population.IsActive(index: 0));
    }
}
