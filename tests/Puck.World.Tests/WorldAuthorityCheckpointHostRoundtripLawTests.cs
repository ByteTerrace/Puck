using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// <c>host-roundtrip-identity</c> (brief O-ORLEANS-V3 §3.5): ONE <see cref="WorldInstanceHost"/>, TWO rows, an
/// in-process <c>world.transfer</c> — exercising escrow, applied-id tables, and the peer-call seam the
/// single-authority <see cref="WorldAuthorityCheckpointLawTests.Activation_roundtrip_identity"/> never reaches. Two
/// scenarios: (i) below, a COMMITTED transfer; (ii)
/// <see cref="Host_roundtrip_identity_in_doubt_transfer"/>, an IN-DOUBT one, reachable in-process only through the
/// faulting <see cref="Puck.World.Server.IWorldPeerCall"/> decorator <see cref="FaultingPeerCall"/> — exercising the
/// forwarding, applied-id, and peer-handle tables (i) never reaches. Both: checkpoint both rows at the relevant
/// boundary, restore both into a FRESH host from the encoded blobs (through <see cref="WorldAuthorityCheckpointCodec"/>),
/// then run both the restored and the uninterrupted host 5000 more ticks with identical (empty) input. PASS =
/// per-row pose-hash agreement every tick AND byte-identical second checkpoints. Scenario and setup:
/// <see cref="HostRoundtripFixture"/>; discriminating controls:
/// <see cref="WorldAuthorityCheckpointHostRoundtripControlTests"/>.
/// </summary>
/// <remarks>
/// The moved seat is a LOCAL one (never a peer-range body), and row-a keeps a second occupant so it is never emptied
/// by the crossing: landing a transfer into the PEER range marks the entry <c>IsRemoteHuman</c> regardless of its
/// intent source (<c>WorldServer.TryAdmitVerifiedParticipant</c>'s reserved-slot arm always calls
/// <c>WorldPopulation.TryAdmitRemotePeerAt</c>), and <c>WorldPopulation.Restore</c> correctly parks a captured
/// non-parked <c>IsRemoteHuman</c> entry with a grace deadline — genuinely correct restart semantics (the socket
/// that fed it is gone), but a deliberate DIVERGENCE from an uninterrupted comparison object once that grace
/// expires, since nothing here ever disconnects the uninterrupted side. Combining that with a multi-thousand-tick
/// identical-trajectory comparison needs either a shorter tail than the compiled grace or a synchronized disconnect
/// on both sides — not built here; the remote-human control proves parking-on-restore on its own, and
/// <see cref="WorldInstanceHostTwoRowTransferLawTests"/> proves <c>ForwardedBodies</c> capture (the one section this
/// scenario therefore does not reach) without a round-trip. And an emptied, non-retained source row auto-reaps
/// (<c>FinalizeCommittedTransfer</c>'s own <c>ReapIfEmpty(transfer.SourceInstance)</c>), after which
/// <c>StepInstances</c> silently stops advancing it — comparing its further trajectory would then be meaningless
/// rather than a genuine round-trip proof, which is the second reason row-a keeps an occupant.
/// </remarks>
public sealed class WorldAuthorityCheckpointHostRoundtripLawTests {
    // Sorts the grant capture's per-row dictionary-derived lists by row key, so a revoke-then-re-grant of the same
    // row (the restore-release/reconnect-re-mint pair) compares equal to a table that never moved. The Principals
    // list and its per-capability sets are left untouched — their order survives a same-row release/re-mint.
    private static WorldAuthorityCheckpoint NormalizeGrantRowOrder(WorldAuthorityCheckpoint checkpoint) {
        static string KeyOf(WorldPrincipal principal, WorldCapability capability, GrantSubject subject) =>
            $"{principal.Describe()}|{capability}|{subject.Describe()}";

        var grants = checkpoint.Grants;

        return (checkpoint with {
            Grants = (grants with {
                Budgets = [.. grants.Budgets.OrderBy(keySelector: static row => KeyOf(capability: row.Capability, principal: row.Principal, subject: row.Subject), comparer: StringComparer.Ordinal)],
                EventBudgets = [.. grants.EventBudgets.OrderBy(keySelector: static row => KeyOf(capability: row.Capability, principal: row.Principal, subject: row.Subject), comparer: StringComparer.Ordinal)],
                HoldCeilings = [.. grants.HoldCeilings.OrderBy(keySelector: static row => KeyOf(capability: row.Capability, principal: row.Principal, subject: row.Subject), comparer: StringComparer.Ordinal)],
                ChannelReach = [.. grants.ChannelReach.OrderBy(keySelector: static row => KeyOf(capability: row.Capability, principal: row.Principal, subject: row.Subject), comparer: StringComparer.Ordinal)],
                KindMasks = [.. grants.KindMasks.OrderBy(keySelector: static row => KeyOf(capability: row.Capability, principal: row.Principal, subject: row.Subject), comparer: StringComparer.Ordinal)],
                WriteMasks = [.. grants.WriteMasks.OrderBy(keySelector: static row => KeyOf(capability: row.Capability, principal: row.Principal, subject: row.Subject), comparer: StringComparer.Ordinal)],
                Exclusive = [.. grants.Exclusive.OrderBy(keySelector: static row => KeyOf(capability: row.Capability, principal: row.Holder, subject: row.Subject), comparer: StringComparer.Ordinal)],
            }),
        });
    }

    [Fact]
    public void Host_roundtrip_identity_committed_transfer() {
        var (host, rowA, rowB, machineId) = HostRoundtripFixture.BuildCommittedScenario();
        using var disposeA = rowA;
        using var disposeB = rowB;

        var (checkpointA, checkpointB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);
        var decodedA = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointA);
        var decodedB = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB);

        var (restoredHost, restoredA, restoredB) = HostRoundtripFixture.RestoreBoth(checkpointA: decodedA, checkpointB: decodedB, machineId: machineId);
        using var disposeRestoredA = restoredA;
        using var disposeRestoredB = restoredB;

        HostRoundtripFixture.RunIdenticalTail(
            restoredA: restoredA,
            restoredB: restoredB,
            restoredHost: restoredHost,
            ticks: 5000,
            uninterruptedA: rowA,
            uninterruptedB: rowB,
            uninterruptedHost: host
        );

        var (finalUninterruptedA, finalUninterruptedB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);
        var (finalRestoredA, finalRestoredB) = HostRoundtripFixture.CaptureBoth(host: restoredHost, rowA: restoredA, rowB: restoredB);

        Assert.True(condition: DeepEqual.Compare(a: finalUninterruptedA, b: finalRestoredA), userMessage: DeepEqual.LastMismatchPath);
        Assert.True(condition: DeepEqual.Compare(a: finalUninterruptedB, b: finalRestoredB), userMessage: DeepEqual.LastMismatchPath);
    }
    /// <summary>Scenario (ii): checkpoint both rows with a crossing reserved at the destination but IN DOUBT at the
    /// source (<see cref="HostRoundtripFixture.BuildInDoubtScenario"/>), restore both into a fresh host, and run the
    /// same identical tail. The FIRST <see cref="WorldInstanceHost.DrainPendingTransfers"/> call on either side
    /// resolves the standing reservation — the uninterrupted host's own faulting decorator only faults ONCE, so its
    /// retry lands for real, and the restored host's re-materialized entry (no fault decorator at all) lands on its
    /// own first retry — so both sides commit on the SAME tail tick and the hash trajectories never diverge.</summary>
    [Fact]
    public void Host_roundtrip_identity_in_doubt_transfer() {
        var (host, rowA, rowB, machineId, _) = HostRoundtripFixture.BuildInDoubtScenario();
        using var disposeA = rowA;
        using var disposeB = rowB;

        var (checkpointA, checkpointB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);

        Assert.Single(collection: checkpointA.HostRow.InDoubtTransfers);

        var decodedA = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointA);
        var decodedB = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB);

        var (restoredHost, restoredA, restoredB) = HostRoundtripFixture.RestoreBoth(checkpointA: decodedA, checkpointB: decodedB, machineId: machineId);
        using var disposeRestoredA = restoredA;
        using var disposeRestoredB = restoredB;

        HostRoundtripFixture.RunIdenticalTail(
            restoredA: restoredA,
            restoredB: restoredB,
            restoredHost: restoredHost,
            ticks: 5000,
            uninterruptedA: rowA,
            uninterruptedB: rowB,
            uninterruptedHost: host
        );

        Assert.True(condition: rowB.Server.Population.IsActive(index: 0));
        Assert.True(condition: restoredB.Server.Population.IsActive(index: 0));

        var (finalUninterruptedA, finalUninterruptedB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);
        var (finalRestoredA, finalRestoredB) = HostRoundtripFixture.CaptureBoth(host: restoredHost, rowA: restoredA, rowB: restoredB);

        Assert.True(condition: DeepEqual.Compare(a: finalUninterruptedA, b: finalRestoredA), userMessage: DeepEqual.LastMismatchPath);
        Assert.True(condition: DeepEqual.Compare(a: finalUninterruptedB, b: finalRestoredB), userMessage: DeepEqual.LastMismatchPath);
    }
    /// <summary>Scenario (i)'s own trade-off, resolved: a PEER-range crossing (never reached by the local-seat
    /// scenario above) restores its destination entry PARKED (<c>WorldPopulation.Restore</c>'s own rule for a
    /// captured non-parked <c>IsRemoteHuman</c> row), which would diverge a multi-thousand-tick pose comparison
    /// against an uninterrupted object nothing ever disconnects — UNLESS the restored side resumes immediately,
    /// matching a genuine reconnect that follows right behind a restart. <see cref="WorldPopulation.TryResumeParkedPeer"/>
    /// (P2e item 3) makes that the third option beside a synchronized disconnect or a tail shorter than the
    /// compiled grace: resume before comparing, so the pose divergence a NEVER-reconnecting peer would introduce
    /// never enters the tail at all — proven over the full 5000 ticks below.
    /// <para><b>The byte-identical second-checkpoint half does NOT extend the same way</b>, and this is not a
    /// checkpoint-completeness gap: <c>TryResumeParkedPeer</c> bumps <c>WorldPopulation</c>'s own revision counter
    /// exactly once, because a genuine reconnect IS one more population-state event than the uninterrupted object's
    /// timeline ever has — nothing disconnected it, so nothing ever resumes it. The captured Revision is honest on
    /// both sides; they legitimately differ by the one real event that occurred on only one of them. Asserted
    /// exactly (the delta is exactly 1, on row B only) rather than silently normalized away, so a second, unrelated
    /// divergence would still fail this law.</para></summary>
    [Fact]
    public void Host_roundtrip_identity_peer_range_transfer() {
        var (host, rowA, rowB, machineId, peerSlot) = HostRoundtripFixture.BuildPeerRangeCommittedScenario();
        using var disposeA = rowA;
        using var disposeB = rowB;

        var (checkpointA, checkpointB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);
        var destinationEntry = checkpointB.Population.Entries.Single(predicate: e => (e.Index == peerSlot));

        Assert.True(condition: destinationEntry.IsRemoteHuman);
        Assert.False(condition: destinationEntry.Parked);

        var decodedA = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointA);
        var decodedB = HostRoundtripFixture.EncodeDecode(checkpoint: checkpointB);

        var (restoredHost, restoredA, restoredB) = HostRoundtripFixture.RestoreBoth(checkpointA: decodedA, checkpointB: decodedB, machineId: machineId);
        using var disposeRestoredA = restoredA;
        using var disposeRestoredB = restoredB;

        Assert.True(condition: restoredB.Server.Population.IsParked(index: peerSlot));

        // The restore released the parked generation's rows (a parked peer's authority never survives the
        // connection it belonged to), so model the genuine reconnect the way the admission door's resume arm now
        // does: resume the body, then re-mint the entry's own admission templates through the ordinary grant door.
        var restoredPeerHeld = restoredB.Server.Grants.Held(principal: WorldPrincipal.Peer(
            index: peerSlot,
            generation: destinationEntry.Generation
        ));

        Assert.Empty(collection: restoredPeerHeld);
        Assert.True(condition: restoredB.Server.Population.TryResumeParkedPeer(
            identityDomain: destinationEntry.IdentityDomain,
            identitySubject: destinationEntry.IdentitySubject,
            admitted: out var resumedEntry
        ));
        Assert.False(condition: restoredB.Server.Population.IsParked(index: peerSlot));

        var remintedRows = 0;

        foreach (var template in destinationEntry.AdmissionInstalledGrantTemplates) {
            restoredB.Server.Grant(
                actor: WorldPrincipal.Console,
                grant: new WorldGrant(
                    Principal: resumedEntry.Identity,
                    Capability: template.Capability,
                    Subject: template.SubjectFor(bodyIndex: peerSlot),
                    Exclusive: template.Exclusive,
                    Budget: template.Budget,
                    EventBudget: template.EventBudget,
                    KindMask: template.KindMask
                )
            );
            remintedRows++;
        }

        Assert.True(condition: (remintedRows > 0), userMessage: "the scenario's arrival verdict must carry at least one template, or the release/re-mint half of this law asserts nothing");

        HostRoundtripFixture.RunIdenticalTail(
            restoredA: restoredA,
            restoredB: restoredB,
            restoredHost: restoredHost,
            ticks: 5000,
            uninterruptedA: rowA,
            uninterruptedB: rowB,
            uninterruptedHost: host
        );

        var (finalUninterruptedA, finalUninterruptedB) = HostRoundtripFixture.CaptureBoth(host: host, rowA: rowA, rowB: rowB);
        var (finalRestoredA, finalRestoredB) = HostRoundtripFixture.CaptureBoth(host: restoredHost, rowA: restoredA, rowB: restoredB);

        Assert.True(condition: DeepEqual.Compare(a: finalUninterruptedA, b: finalRestoredA), userMessage: DeepEqual.LastMismatchPath);

        Assert.Equal(
            actual: finalRestoredB.Population.Revision,
            expected: (finalUninterruptedB.Population.Revision + 1)
        );
        // The grant table's delta is likewise exact: the restore released each of the parked generation's rows and
        // the modeled reconnect re-minted each, two revision moves per row, on row B alone. Asserted rather than
        // silently normalized, so an unrelated grant-table divergence still fails this law.
        Assert.Equal(
            actual: finalRestoredB.Grants.Revision,
            expected: (finalUninterruptedB.Grants.Revision + (2 * remintedRows))
        );
        // The release/re-mint pair moves the re-minted rows to the tail of the capture's dictionary-derived lists,
        // so those lists compare as sets (sorted by row key on both sides); everything else stays order-sensitive.
        var normalizedUninterruptedB = NormalizeGrantRowOrder(checkpoint: finalUninterruptedB);
        var normalizedRestoredB = NormalizeGrantRowOrder(checkpoint: finalRestoredB) with {
            Population = finalRestoredB.Population with { Revision = finalUninterruptedB.Population.Revision },
        };

        normalizedRestoredB = normalizedRestoredB with {
            Grants = (normalizedRestoredB.Grants with { Revision = normalizedUninterruptedB.Grants.Revision }),
        };

        Assert.True(condition: DeepEqual.Compare(a: normalizedUninterruptedB, b: normalizedRestoredB), userMessage: DeepEqual.LastMismatchPath);
    }
}
