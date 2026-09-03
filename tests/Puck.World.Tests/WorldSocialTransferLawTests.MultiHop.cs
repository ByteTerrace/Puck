using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    [Fact]
    public void LocalForwardingFollowsRepeatedAuthorityCircuitsForRoutesIntentsAndLeave() {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 2, NetworkPlayers = 2 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var host = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document); using var c = HostRow.Build("c", document);
        host.Admit(a.Instance); host.Admit(b.Instance); host.Admit(c.Instance);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        var observer = Seed(host, a, 4);
        var original = a.Server.Population.EnsureMobility(4, a.Server.AuthorityIdentity);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(5, IntentSource.Live, [], "test", "other", out _, out reason), reason);
        var other = a.Server.Population.EnsureMobility(5, a.Server.AuthorityIdentity);
        Transfer(host, "a", "b", 5);
        void Move(HostRow from, HostRow to) {
            using var trace = new StringWriter();
            var previous = Console.Error;
            try {
                Console.SetError(trace);
                host.EnqueueTransfer(from.Instance.Name, WorldInstanceHost.TransferScope.Body, 4,
                    WorldInstanceHost.TransferDestination.Existing(to.Instance.Name), from.Server.Population.PeerPrincipal(4));
                host.DrainPendingTransfers();
            }
            finally { Console.SetError(previous); }
            Assert.True(to.Server.Population.IsActive(4), trace.ToString());
            Assert.False(from.Server.Population.IsActive(4), trace.ToString());
        }
        Transfer(host, "a", "b", 4); Move(b, c);
        var onwardRouter = b.Server.TransferForwarder;
        try {
            b.Server.TransferForwarder = null;
            Assert.False(host.TryDescribeForwarding(a.Server, in original, out _, out reason));
            Assert.Contains("no longer live", reason);
        } finally { b.Server.TransferForwarder = onwardRouter; }
        for (var circuit = 0; circuit < 16; circuit++) {
            Assert.True(host.TryDescribeForwarding(a.Server, in original, out var route, out reason), reason);
            Assert.Equal(c.Server.AuthorityIdentity, route.Entity.Authority);
            Assert.Equal(c.Server.Population.Generation(4), route.Entity.Generation);
            Assert.True(Memory(host, c).TryRead(Key(observer), out _));
            var held = new IntentSubmission((ulong)circuit + 1, 4, default(PlayerIntent).WithChannel(0, FixedQ4816.One), WorldPrincipal.Console);
            Assert.True(host.TryForwardIntent(a.Server, in original, in held, out reason), reason);
            for (var tick = 0; tick < 30; tick++) { host.StepInstances(Fixtures.StepTicks); }
            Assert.True(c.Server.Population.EntryBody(4)!.PlanarSpeed > 0);
            Move(c, a); Move(a, b); Move(b, c);
        }
        var hop = Capture(host, a).HostRow.ForwardedBodies.Single(row => row.SourceIncarnation == original.Incarnation);
        using var forged = new WorldLocalForwardedAuthority(b.Server, "b", "untrusted-authority", hop.Mobility);
        Assert.False(forged.TryDescribeRoute(out _, out reason));
        Assert.Contains("no committed credential", reason);
        var leave = new WorldSubmissionPayload.Session(new SessionRequest.Leave(WorldPrincipal.Console, 4));
        Assert.False(forged.TryForwardSubmission(leave, out _, out _));
        Assert.True(host.TryForwardSubmission(a.Server, in original, leave, out var result, out reason), reason);
        Assert.True(Assert.IsType<WorldSubmissionResult.Session>(result).Reply.Accepted);
        Assert.True(c.Server.Population.IsParked(4)); // The authored reconnect grace retains the body, not its route authority.
        Assert.Equal(other.Incarnation, Assert.Single(Capture(host, a).HostRow.ForwardedBodies).SourceIncarnation);
        Assert.Empty(Capture(host, b).HostRow.ForwardedBodies);
        Assert.Empty(Capture(host, c).HostRow.ForwardedBodies);
        Assert.False(host.TryDescribeForwarding(a.Server, in original, out _, out _));
        Assert.True(host.TryDescribeForwarding(a.Server, in other, out var otherRoute, out reason), reason);
        Assert.Equal(b.Server.AuthorityIdentity, otherRoute.Entity.Authority); Assert.Equal(5, otherRoute.Entity.Index);
    }

    [Fact]
    public void LocalForwardingCycleRefusesWithoutLeakingTraversalDepth() {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 1, NetworkPlayers = 1 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var host = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document);
        host.Admit(a.Instance); host.Admit(b.Instance);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        var original = a.Server.Population.EnsureMobility(4, a.Server.AuthorityIdentity);
        Transfer(host, "a", "b", 4);
        host.EnqueueTransfer("b", WorldInstanceHost.TransferScope.Body, 4,
            WorldInstanceHost.TransferDestination.Existing("a"), b.Server.Population.PeerPrincipal(4));
        host.DrainPendingTransfers();
        Assert.True(a.Server.Population.IsActive(4));
        var beforeDetach = Capture(host, a);
        Assert.True(a.Server.Population.TryDetachSeatForTransfer(4, out _));
        var held = new IntentSubmission(1, 4, default(PlayerIntent).WithChannel(0, FixedQ4816.One), WorldPrincipal.Console);
        var leave = new WorldSubmissionPayload.Session(new SessionRequest.Leave(WorldPrincipal.Console, 4));
        for (var attempt = 0; attempt < 128; attempt++) {
            Assert.False(host.TryDescribeForwarding(a.Server, in original, out _, out reason));
            Assert.Contains("exceeds 64 hops", reason);
            Assert.False(host.TryForwardIntent(a.Server, in original, in held, out reason));
            Assert.Contains("exceeds 64 hops", reason);
            Assert.False(host.TryForwardSubmission(a.Server, in original, leave, out _, out reason));
            Assert.Contains("exceeds 64 hops", reason);
        }
        a.Server.RestoreCheckpoint(beforeDetach);
        Assert.True(host.TryDescribeForwarding(b.Server, in original, out var route, out reason), reason);
        Assert.Equal(a.Server.AuthorityIdentity, route.Entity.Authority);
    }
}
