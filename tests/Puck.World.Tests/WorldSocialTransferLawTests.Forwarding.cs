using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialTransferLawTests {
    [Fact]
    public void FinalizedLocalRouteSurvivesSixteenRestartsAndLateAuthorityAdmission() {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 2, NetworkPlayers = 2 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var original = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document);
        original.Admit(a.Instance); original.Admit(b.Instance);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        var observer = Seed(original, a, 4);
        var mobility = a.Server.Population.EnsureMobility(4, a.Server.AuthorityIdentity);
        Transfer(original, "a", "b", 4);
        Assert.False(original.ReapIfEmpty("a")); // A forwarding authority is not disposable merely because its body left.
        Assert.True(original.TryDescribeForwarding(a.Server, in mobility, out var route, out reason), reason);
        Assert.Equal(b.Server.AuthorityIdentity, route.Entity.Authority);
        var checkpoint = WireRoundTrip(Capture(original, a));
        var targetCheckpoint = WireRoundTrip(Capture(original, b));
        Assert.Empty(checkpoint.HostRow.InDoubtTransfers);
        Assert.Equal(a.Server.AuthorityIdentity, Assert.Single(checkpoint.HostRow.ForwardedBodies).SourceAuthority);
        for (var restart = 0; restart < 16; restart++) {
            using var waiting = Host(); using var source = HostRow.Build("a", document);
            source.Server.RestoreCheckpoint(checkpoint); waiting.Admit(source.Instance); waiting.RestoreRow(source.Instance, checkpoint.HostRow);
            Assert.False(waiting.TryDescribeForwarding(source.Server, in mobility, out _, out reason));
            Assert.Contains("not yet available", reason);
            var recaptured = WireRoundTrip(Capture(waiting, source));
            Assert.Equal(WorldAuthorityCheckpointCodec.Encode(checkpoint), WorldAuthorityCheckpointCodec.Encode(recaptured));
            checkpoint = recaptured;
        }
        using var resumed = Host(); using var resumedA = HostRow.Build("a", document);
        resumedA.Server.RestoreCheckpoint(checkpoint); resumed.Admit(resumedA.Instance); resumed.RestoreRow(resumedA.Instance, checkpoint.HostRow);
        using var impostor = HostRow.Build("wrong-authority", document); using var sameName = HostRow.Wrap("b", impostor.Server, impostor.Machines);
        resumed.Admit(sameName.Instance);
        Assert.False(resumed.TryDescribeForwarding(resumedA.Server, in mobility, out _, out _));
        using var actual = HostRow.Build("b", document); actual.Server.RestoreCheckpoint(targetCheckpoint);
        using var renamed = HostRow.Wrap("late-b", actual.Server, actual.Machines); resumed.Admit(renamed.Instance);
        Assert.True(resumed.TryDescribeForwarding(resumedA.Server, in mobility, out route, out reason), reason);
        Assert.Equal(actual.Server.AuthorityIdentity, route.Entity.Authority); Assert.Equal(4, route.Entity.Index);
        Assert.True(Memory(resumed, actual).TryRead(Key(observer), out _));

        // Reinstall replaces the old lease instead of accumulating one, and an empty slice really clears it.
        resumed.RestoreRow(resumedA.Instance, checkpoint.HostRow);
        Assert.Single(Capture(resumed, resumedA).HostRow.ForwardedBodies);
        Assert.True(resumed.TryStop("late-b", out reason), reason);
        Assert.False(resumed.TryDescribeForwarding(resumedA.Server, in mobility, out _, out reason));
        Assert.Contains("not yet available", reason);
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(checkpoint), WorldAuthorityCheckpointCodec.Encode(Capture(resumed, resumedA)));
        using var replacement = HostRow.Build("b", document); replacement.Server.RestoreCheckpoint(targetCheckpoint);
        using var replacementRow = HostRow.Wrap("replacement-b", replacement.Server, replacement.Machines); resumed.Admit(replacementRow.Instance);
        Assert.True(resumed.TryDescribeForwarding(resumedA.Server, in mobility, out route, out reason), reason);
        Assert.Equal(replacement.Server.AuthorityIdentity, route.Entity.Authority);
        resumed.RestoreRow(resumedA.Instance, checkpoint.HostRow with { ForwardedBodies = [] });
        Assert.False(resumed.TryDescribeForwarding(resumedA.Server, in mobility, out _, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void MalformedFinalizedRouteRefusesBeforeAnyHostWrite(int fault) {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 2, NetworkPlayers = 2 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var host = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document);
        host.Admit(a.Instance); host.Admit(b.Instance);
        for (var slot = 4; slot < 6; slot++) {
            Assert.True(a.Server.Population.TryAdmitRemotePeerAt(slot, IntentSource.Live, [], "test", $"traveler-{slot}", out _, out var refusal), refusal);
            Transfer(host, "a", "b", slot);
        }
        var checkpoint = WireRoundTrip(Capture(host, a));
        Assert.Equal(2, checkpoint.HostRow.ForwardedBodies.Count);
        var first = checkpoint.HostRow.ForwardedBodies[0]; var second = checkpoint.HostRow.ForwardedBodies[1];
        var bad = fault switch {
            0 => second with { SourceAuthority = "wrong-source" },
            1 => second with { Mobility = second.Mobility with { Epoch = 0 } },
            2 => second with { DestinationBodyIndex = -1 },
            3 => second with { DestinationAddress = second.DestinationAddress with { Generation = 1 } },
            4 => second with { DestinationEndpoint = "127.0.0.1:49123" },
            5 => second with { DestinationDefinitionJson = WorldDefinitionSerialization.Serialize(b.Server.Definition) },
            6 => second with { DestinationEndpoint = "127.0.0.1:49123", DestinationDefinitionJson = "{"u8.ToArray() },
            7 => first,
            8 => second with { SourceIncarnation = first.SourceIncarnation },
            _ => second with { DestinationEndpoint = "not-an-endpoint", DestinationDefinitionJson = WorldDefinitionSerialization.Serialize(b.Server.Definition) },
        };
        var invalid = checkpoint.HostRow with { IsPaused = true, NextTransferId = 9876, ForwardedBodies = [first, bad] };
        Assert.Throws<ArgumentException>(() => host.RestoreRow(a.Instance, invalid));
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(checkpoint), WorldAuthorityCheckpointCodec.Encode(Capture(host, a)));
        var incoming = first.Mobility with { Epoch = first.Mobility.Epoch - 1 };
        Assert.True(host.TryDescribeForwarding(a.Server, in incoming, out _, out var reason), reason);
        host.RestoreRow(a.Instance, checkpoint.HostRow);
        Assert.True(host.TryDescribeForwarding(a.Server, in incoming, out _, out reason), reason);
    }

    [Fact]
    public void RetiredLocalForwardingLeaseCannotRepublishHeldInput() {
        var document = Document() with {
            PopulationRaw = Document().Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 1, NetworkPlayers = 1 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        using var host = Host(); using var a = HostRow.Build("a", document); using var b = HostRow.Build("b", document);
        host.Admit(a.Instance); host.Admit(b.Instance);
        Assert.True(a.Server.Population.TryAdmitRemotePeerAt(4, IntentSource.Live, [], "test", "traveler", out _, out var reason), reason);
        Transfer(host, "a", "b", 4);
        var description = Assert.Single(Capture(host, a).HostRow.ForwardedBodies);
        using var arm = new WorldLocalForwardedAuthority(b.Server, "b", description.SourceAuthority, description.Mobility);
        var held = new IntentSubmission(1, 4, default(PlayerIntent).WithChannel(0, FixedQ4816.One), WorldPrincipal.Console);
        Assert.True(arm.TryForwardIntent(in held, out reason), reason);
        for (var tick = 0; tick < 120; tick++) { host.StepInstances(Fixtures.StepTicks); }
        var body = b.Server.Population.EntryBody(4)!;
        Assert.True(body.PlanarSpeed > 1);
        arm.Dispose();
        Assert.False(arm.TryForwardIntent(in held, out reason));
        Assert.Contains("lease is closed", reason);
        for (var tick = 0; tick < 30; tick++) { host.StepInstances(Fixtures.StepTicks); }
        Assert.Equal(0f, body.PlanarSpeed, precision: 2);
    }
}
