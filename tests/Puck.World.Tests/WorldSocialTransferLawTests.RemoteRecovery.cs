using System.Security.Cryptography;
using Puck.Attestation;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    [Fact]
    public async Task RemoteThenLocalForwardingReachesFinalOwnerAndRetiresEveryBranch() {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 1, NetworkPlayers = 1 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var destinationHost = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document);
        using var c = HostRow.Build("c", document); using var d = HostRow.Build("d", document);
        destinationHost.Admit(a.Instance); destinationHost.Admit(b.Instance); destinationHost.Admit(c.Instance); destinationHost.Admit(d.Instance);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        var observer = Seed(destinationHost, a, 4);
        var mobility = a.Server.Population.EnsureMobility(4, a.Server.AuthorityIdentity);
        Transfer(destinationHost, "a", "b", 4);
        using var oracle = new LocalKeySigningOracle(ECDsa.Create(ECCurve.NamedCurves.nistP256), a.Server.AuthorityIdentity, TimeSpan.FromMinutes(5));
        var trust = new WorldAdmissionEntry(oracle.Domain, oracle.Subject, WorldAdmissionTrustMode.SignsDirectly,
            AttestationAlgorithms.EcdsaP256Sha256, Convert.ToBase64String(oracle.PublicKeySubjectPublicKeyInfo), []);
        var security = new WorldAttestedAuthenticator(() => [trust], oracle);
        using var door = new WorldPeerHost(b.Server, authenticator: security); door.Start("127.0.0.1:0");
        var checkpoint = Capture(destinationHost, a);
        var onward = Assert.Single(checkpoint.HostRow.ForwardedBodies) with {
            DestinationEndpoint = door.ListenEndpoint, DestinationDefinitionJson = WorldDefinitionSerialization.Serialize(b.Server.Definition),
        };
        checkpoint = WireRoundTrip(checkpoint with { HostRow = checkpoint.HostRow with { ForwardedBodies = [onward] } });
        foreach (var (from, to) in new[] { (b, c), (c, d) }) {
            destinationHost.EnqueueTransfer(from.Instance.Name, WorldInstanceHost.TransferScope.Body, 4,
                WorldInstanceHost.TransferDestination.Existing(to.Instance.Name), from.Server.Population.PeerPrincipal(4));
            destinationHost.DrainPendingTransfers();
            Assert.True(to.Server.Population.IsActive(4)); Assert.False(from.Server.Population.IsActive(4));
        }
        using var sourceHost = Host(); using var source = HostRow.Build("a", document);
        source.Server.RestoreCheckpoint(checkpoint); source.Instance.Federation = new(security, source.Server.AuthorityIdentity);
        sourceHost.Admit(source.Instance); sourceHost.RestoreRow(source.Instance, checkpoint.HostRow);
        using var deadline = Laws.SocketDeadline();
        async Task<T> ThroughDoor<T>(Func<T> operation) {
            var work = Task.Run(operation, deadline.Token);
            while (!work.IsCompleted) {
                deadline.Token.ThrowIfCancellationRequested(); door.DrainPending(); await Task.Delay(5, deadline.Token);
            }
            return await work;
        }
        var route = await ThroughDoor(() => {
            Assert.True(sourceHost.TryDescribeForwarding(source.Server, in mobility, out var described, out var refusal), refusal);
            return described;
        });
        Assert.Equal(d.Server.AuthorityIdentity, route.Entity.Authority);
        Assert.Equal(d.Server.Population.Generation(4), route.Entity.Generation);
        Assert.True(Memory(destinationHost, d).TryRead(Key(observer), out _));
        var held = new IntentSubmission(1, 4, default(PlayerIntent).WithChannel(0, Puck.Maths.FixedQ4816.One), WorldPrincipal.Console);
        Assert.True(sourceHost.TryForwardIntent(source.Server, in mobility, in held, out reason), reason);
        while (d.Server.Population.EntryBody(4)!.PlanarSpeed == 0) {
            deadline.Token.ThrowIfCancellationRequested(); door.DrainPending(); destinationHost.StepInstances(Fixtures.StepTicks);
            await Task.Delay(5, deadline.Token);
        }
        var leave = new WorldSubmissionPayload.Session(new SessionRequest.Leave(WorldPrincipal.Console, 4));
        var reply = await ThroughDoor(() => {
            Assert.True(sourceHost.TryForwardSubmission(source.Server, in mobility, leave, out var result, out var refusal), refusal);
            return Assert.IsType<WorldSubmissionResult.Session>(result).Reply;
        });
        Assert.True(reply.Accepted);
        Assert.True(d.Server.Population.IsParked(4));
        Assert.Empty(Capture(sourceHost, source).HostRow.ForwardedBodies);
        Assert.Empty(Capture(destinationHost, a).HostRow.ForwardedBodies);
        Assert.Empty(Capture(destinationHost, b).HostRow.ForwardedBodies);
        Assert.Empty(Capture(destinationHost, c).HostRow.ForwardedBodies);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveredRemoteCommitReconstructsForwardingFromTheRetainedTraveler(bool committedBeforeRestart) {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 2, NetworkPlayers = 2 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var original = Host();
        using var a = HostRow.Build("a", document);
        using var b = HostRow.Build("b", document);
        original.Admit(a.Instance); original.Admit(b.Instance); Join(a.Server, 0);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        var observer = Seed(original, a, 4);
        var mobility = a.Server.Population.EnsureMobility(4, a.Server.AuthorityIdentity);
        original.SetPeerCallFault("b", new LostAnswer(b.Server, committedBeforeRestart));
        Transfer(original, "a", "b", 4);
        var checkpoint = Capture(original, a);
        var pending = Assert.Single(checkpoint.HostRow.InDoubtTransfers);
        Assert.True(Assert.Single(pending.Landed).Peer!.Value.Source.IsLive);
        Assert.Equal(committedBeforeRestart, b.Server.Population.IsActive(4));

        using var oracle = new LocalKeySigningOracle(ECDsa.Create(ECCurve.NamedCurves.nistP256),
            a.Server.AuthorityIdentity, TimeSpan.FromMinutes(5));
        var trust = new WorldAdmissionEntry(oracle.Domain, oracle.Subject, WorldAdmissionTrustMode.SignsDirectly,
            AttestationAlgorithms.EcdsaP256Sha256, Convert.ToBase64String(oracle.PublicKeySubjectPublicKeyInfo), []);
        var security = new WorldAttestedAuthenticator(() => [trust], oracle);
        using var door = new WorldPeerHost(b.Server, authenticator: security);
        door.Start("127.0.0.1:0");
        pending = pending with {
            TargetEndpoint = door.ListenEndpoint,
            TargetAuthority = b.Server.AuthorityIdentity,
            TargetDefinitionJson = WorldDefinitionSerialization.Serialize(b.Server.Definition),
        };
        checkpoint = WireRoundTrip(checkpoint with { HostRow = checkpoint.HostRow with { InDoubtTransfers = [pending] } });

        // Only the source joins the restarted host. Its destination is discovered through a fresh QUIC link,
        // which never performed Reserve and therefore has no body-index credential cache to consult.
        using var recovered = Host();
        using var restoredA = HostRow.Build("a", document);
        restoredA.Server.RestoreCheckpoint(checkpoint);
        restoredA.Instance.Federation = new(security, restoredA.Server.AuthorityIdentity);
        recovered.Admit(restoredA.Instance); recovered.RestoreRow(restoredA.Instance, checkpoint.HostRow);
        using var deadline = Laws.SocketDeadline();
        while (Capture(recovered, restoredA).HostRow.InDoubtTransfers.Count != 0) {
            deadline.Token.ThrowIfCancellationRequested();
            door.DrainPending(); recovered.DrainPendingTransfers();
            await Task.Delay(5, deadline.Token);
        }
        Assert.False(restoredA.Server.Population.IsActive(4));
        Assert.Empty(Capture(recovered, restoredA).Server.Social!.FrozenObservers!);
        Assert.True(Memory(original, b).TryRead(Key(observer), out var impression));
        Assert.Equal(1UL, impression.IndependentEvents);

        var query = Task.Run(() => {
            var accepted = recovered.TryDescribeForwarding(restoredA.Server, in mobility, out var route, out var refusal);
            return (accepted, route, refusal);
        });
        while (!query.IsCompleted) {
            deadline.Token.ThrowIfCancellationRequested();
            door.DrainPending();
            await Task.Delay(5, deadline.Token);
        }
        var result = await query;
        Assert.True(result.accepted, result.refusal);
        Assert.Equal(b.Server.AuthorityIdentity, result.route.Entity.Authority);
        Assert.Equal(4, result.route.Entity.Index);
        Assert.Equal(b.Server.Population.Generation(4), result.route.Entity.Generation);

        // Restart again after finalization: no pending transaction remains to recreate this route for us.
        var finalized = WireRoundTrip(Capture(recovered, restoredA));
        Assert.Empty(finalized.HostRow.InDoubtTransfers);
        var forwarding = Assert.Single(finalized.HostRow.ForwardedBodies);
        Assert.Equal(door.ListenEndpoint, forwarding.DestinationEndpoint);
        Assert.Equal(restoredA.Server.AuthorityIdentity, forwarding.SourceAuthority);
        Assert.NotNull(forwarding.DestinationDefinitionJson);
        using var finalHost = Host(); using var finalSource = HostRow.Build("a", document);
        finalSource.Server.RestoreCheckpoint(finalized);
        finalSource.Instance.Federation = new(security, finalSource.Server.AuthorityIdentity);
        finalHost.Admit(finalSource.Instance); finalHost.RestoreRow(finalSource.Instance, finalized.HostRow);
        var finalQuery = Task.Run(() => {
            var accepted = finalHost.TryDescribeForwarding(finalSource.Server, in mobility, out var route, out var refusal);
            return (accepted, route, refusal);
        });
        while (!finalQuery.IsCompleted) {
            deadline.Token.ThrowIfCancellationRequested(); door.DrainPending(); await Task.Delay(5, deadline.Token);
        }
        var finalResult = await finalQuery;
        Assert.True(finalResult.accepted, finalResult.refusal);
        Assert.Equal(result.route.Entity, finalResult.route.Entity);
    }
}
