using System.Numerics;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class BodyContinuationHashLawTests {
    private static void Join(WorldServer server, string? identityName = null) => Assert.True(condition: server.ApplySession(
        request: new SessionRequest.Join(IdentityName: identityName, Principal: Principal.Seat(slot: 0), Slot: 0,
            WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

    [Fact]
    public void RenderedAppearanceDoesNotEnterAuthoritativeState() {
        using var fixture = Fixtures.FreshServer();

        Join(server: fixture.Server);
        var before = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0UL);
        var pose = WorldReplaySnapshot.HashState(population: fixture.Server.Population);

        fixture.Server.Population.SetBodyColor(slot: 0, color: new Vector3(x: 0.125f, y: 0.25f, z: 0.5f));
        fixture.Server.Population.SetCatalogRig(catalogRig: 7, slot: 0);

        Assert.Equal(expected: pose, actual: WorldReplaySnapshot.HashState(population: fixture.Server.Population));
        Assert.Equal(expected: before, actual: WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0UL));
        var captured = fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 0));

        Assert.Equal(expected: new Vector3(x: 0.125f, y: 0.25f, z: 0.5f), actual: captured.BodyColor);
        Assert.Equal(expected: ((byte)7), actual: captured.CatalogRig);
    }
    [Fact]
    public void RevokedAdmissionKeysHaveCanonicalOrder() {
        using var fixture = Fixtures.FreshServer();

        Join(server: fixture.Server);
        var first = (WorldCapability.Edit, GrantSubject.State(name: "alpha"));
        var second = (WorldCapability.Edit, GrantSubject.State(name: "beta"));

        fixture.Server.Population.SetPeerAdmissionRevokedKeys(bodyIndex: 0, revokedKeys: new HashSet<(WorldCapability, GrantSubject)> { first, second });
        var before = WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0UL);
        var captured = fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 0));

        fixture.Server.Population.SetPeerAdmissionRevokedKeys(bodyIndex: 0, revokedKeys: new HashSet<(WorldCapability, GrantSubject)> { second, first });

        Assert.Equal(expected: before, actual: WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0UL));
        Assert.Equal(expected: captured.AdmissionRevokedKeys, actual: fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 0)).AdmissionRevokedKeys);
        fixture.Server.Population.SetPeerAdmissionRevokedKeys(bodyIndex: 0, revokedKeys: new HashSet<(WorldCapability, GrantSubject)> { first });
        Assert.NotEqual(expected: before, actual: WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: 0UL));
    }
    [Fact]
    public void ReplayRestoresTheHashedIdentityProjection() {
        using var directory = new TemporaryDirectory(prefix: "lappy-review-replay-");
        var definition = Fixtures.BuildDocument();

        definition = definition with {
            PlayerDefaultsRaw = definition.PlayerDefaults with {
                IdentitiesRaw = [new WorldIdentitySeed(Id: SafeName.Parse(candidate: "stable-id"), Name: "amber", Color: "#ED8530")],
            },
        };
        using var fixture = Fixtures.FreshServer(definition: definition);

        Join(server: fixture.Server, identityName: "amber");
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: directory.RootPath), liveServer: fixture.Server,
            profiles: fixture.Server.Profiles, transport: new LoopbackTransport(server: fixture.Server), engines: [],
            machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost());

        Assert.True(condition: tape.TryBeginRecording(name: "projection", refusal: out var refusal), userMessage: refusal);
        fixture.Step();
        tape.NoteTick();
        var stopped = tape.StopRecording();

        Assert.Null(@object: stopped.VerifyFault);
        Assert.NotNull(@object: stopped.Verdict);
        Assert.True(condition: stopped.Verdict.Match);
        using var stream = File.OpenRead(path: stopped.Path);
        var recorded = WorldReplaySnapshot.Read(stream: stream);
        var replayed = recorded.DriveTraces(
            profiles: fixture.Server.Profiles, engines: [], machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost());

        Assert.Equal(expected: recorded.RecordedAuthoritativeHashes, actual: replayed.Authoritative);
    }
}
