using System.Security.Cryptography;
using System.Text.Json;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Silo.Tests;

public sealed class SiloOperatorLawTests {
    private static void InstallDefinition(WorldFixture fixture, WorldDefinition candidate) {
        fixture.Server.EnqueueRebuild(
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Load,
                Definition: candidate,
                Origin: new WorldRebuildOrigin.File(Path: "silo-operator.world.json"),
                Force: true,
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate))
            ),
            principal: Principal.Console
        );
        fixture.Step();
    }

    [Fact]
    public void RemoteSurfaceUsesLiveOperateAndNeverAdmitsHostCommands() {
        WorldAdmissionEntry entry = new(Domain: "https://issuer.test", Subject: "owner", Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica, Grants: [new(WorldCapability.Operate, GrantSubject.All)]);
        var document = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(document with { Admission = [entry], PopulationRaw = document.Population with { CapacityRaw = 8, NetworkPlayers = 4 } });
        var server = fixture.Server;
        Assert.True(WorldAdmissionDoor.TryMatchOAuthEntry([entry], entry.Domain, entry.Subject!, out var verdict));
        Assert.True(server.TryAdmitPeerConnection(verdict, server.Definition.Admission, out var peer, out var reason), reason);
        var registry = new CommandRegistry([new ProbeModule()]);
        var world = Assert.Single(registry.Definitions, row => row.Name == "world.peers");
        var host = Assert.Single(registry.Definitions, row => row.Name == "host.probe");
        var identity = new ControlIdentity(entry.Domain, entry.Subject!);
        Assert.True(SiloControlAdmission.Describes(world, verdict));
        Assert.False(SiloControlAdmission.Describes(host, verdict));
        Assert.True(SiloControlAdmission.Allows(world, server, peer, identity));
        Assert.False(SiloControlAdmission.Allows(host, server, peer, identity));
        Assert.True(server.GrantTable.Revoke(peer.Identity, WorldCapability.Operate, GrantSubject.All, Principal.Console));
        Assert.False(SiloControlAdmission.Allows(world, server, peer, identity));
        Assert.True(server.GrantTable.TryGrant(new(peer.Identity, WorldCapability.Operate, GrantSubject.All, false), Principal.Console, out reason), reason);
        Assert.True(SiloControlAdmission.Allows(world, server, peer, identity));
        Assert.True(server.DisconnectPeer(peer.Identity, Principal.Console));
        Assert.False(SiloControlAdmission.Allows(world, server, peer, identity));
    }
    [Fact]
    public void RebuildRefreshesPeerAdmissionTierAcrossWideningAndNarrowing() {
        var presentationEntry = new WorldAdmissionEntry(
            Domain: "https://issuer.test",
            Subject: "owner",
            Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Presentation,
            Grants: []
        );
        var replicaOperatorEntry = new WorldAdmissionEntry(
            Domain: presentationEntry.Domain,
            Subject: presentationEntry.Subject,
            Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica,
            Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Operate, Subject: GrantSubject.All)]
        );
        var baseDocument = Fixtures.BuildDocument();
        var initial = (baseDocument with {
            Admission = [presentationEntry],
            PopulationRaw = (baseDocument.Population with { CapacityRaw = 8, NetworkPlayers = 4 }),
        });
        using var fixture = Fixtures.FreshServer(definition: initial);
        var server = fixture.Server;

        Assert.True(condition: WorldAdmissionDoor.TryMatchOAuthEntry(entries: [presentationEntry], issuer: presentationEntry.Domain, subject: presentationEntry.Subject!, verdict: out var verdict));
        Assert.True(condition: server.TryAdmitPeerConnection(verdict: verdict, expectedAdmissionEntries: server.Definition.Admission, admitted: out var peer, refusal: out var reason), userMessage: reason);
        Assert.Equal(expected: WorldDisclosureTier.Presentation, actual: server.Population.PeerAdmissionTier(bodyIndex: peer.BodyIndex));

        var registry = new CommandRegistry(modules: [new ProbeModule()]);
        var worldCommand = Assert.Single(collection: registry.Definitions, predicate: static row => (row.Name == "world.peers"));
        var identity = new ControlIdentity(Issuer: presentationEntry.Domain, Subject: presentationEntry.Subject!);

        Assert.False(condition: server.Grants.IsOperator(principal: peer.Identity));
        Assert.False(condition: SiloControlAdmission.Allows(command: worldCommand, identity: identity, peer: peer, server: server));

        // Widening to Replica + Operate must update PeerAdmissionTier before TryGrant(Operate) runs.
        InstallDefinition(fixture: fixture, candidate: (server.Definition with { Admission = [replicaOperatorEntry] }));
        Assert.Equal(expected: WorldDisclosureTier.Replica, actual: server.Population.PeerAdmissionTier(bodyIndex: peer.BodyIndex));
        Assert.True(condition: server.Grants.IsOperator(principal: peer.Identity));
        Assert.True(condition: SiloControlAdmission.Allows(command: worldCommand, identity: identity, peer: peer, server: server));

        // Narrowing back to Presentation must update PeerAdmissionTier and end operator eligibility.
        InstallDefinition(fixture: fixture, candidate: (server.Definition with { Admission = [presentationEntry] }));
        Assert.Equal(expected: WorldDisclosureTier.Presentation, actual: server.Population.PeerAdmissionTier(bodyIndex: peer.BodyIndex));
        Assert.False(condition: server.Grants.IsOperator(principal: peer.Identity));
        Assert.False(condition: SiloControlAdmission.Allows(command: worldCommand, identity: identity, peer: peer, server: server));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ControlSessionHostEvictsAndDisconnectsPeerWhenOAuthAdmissionIsRemovedOrDowngraded(bool removeEntry) {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(path1: directory.RootPath, path2: "world.key");

        File.WriteAllBytes(path: keyFile, bytes: key.ExportPkcs8PrivateKey());
        var rowIdentity = new WorldAuthorityIdentity(Owner: Guid.NewGuid(), World: SafeName.Parse(candidate: "row"));
        var oauthEntry = new WorldAdmissionEntry(
            Domain: "https://issuer.test",
            Subject: "owner",
            Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica,
            Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Operate, Subject: GrantSubject.All)]
        );
        var otherEntry = new WorldAdmissionEntry(
            Domain: "https://issuer.test",
            Subject: "replacement",
            Mode: WorldAdmissionTrustMode.OAuth,
            Disclosure: WorldDisclosureTier.Replica,
            Grants: []
        );
        var baseDocument = Fixtures.BuildDocument();
        var definition = (baseDocument with {
            Admission = [oauthEntry, otherEntry],
            HostRaw = (Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None }),
            PopulationRaw = (baseDocument.Population with { CapacityRaw = 6, NetworkPlayers = 2 }),
        });
        var clock = new VirtualClock();
        var store = new FakeObjectBlobStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath),
            timeProvider: clock
        );
        var ct = TestContext.Current.CancellationToken;

        Assert.True(condition: (await backend.PublishDefinitionAsync(rowIdentity, definition, ct)).Ok);
        var source = new TextCommandSource(registry: new CommandRegistry(modules: [new ProbeModule()]));
        var routing = new SiloConsoleRouting(source: () => source, tagging: new SiloConsoleTagging(output: output));
        var silo = new WorldSiloHost(
            blobStore: store,
            definition: new(
                Worlds: [new(Federation: new(KeyFile: keyFile), Owner: rowIdentity.Owner, Pinned: true, World: rowIdentity.World)],
                Doors: new(Budget: 1),
                Store: new(Settings: JsonElement.Parse(json: "{}"), Type: "directory"),
                StateDir: directory.RootPath,
                Clustering: new(Kind: "Localhost")
            ),
            routing: routing,
            storageTarget: WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath),
            timeProvider: clock
        );
        using var instances = silo.Instances;

        await WorldSiloHost.PumpActivationMailboxesAsync(
            cancellationToken: ct,
            hosts: [silo],
            operation: silo.ActivateAsync(identity: rowIdentity, ct: ct)
        );
        silo.Ready = true;
        silo.NoteMasterStep(stepTicks: Fixtures.StepTicks);

        var controlHost = new SiloControlSessionHost(silo: silo, routing: routing);
        var ownerIdentity = new ControlIdentity(Issuer: oauthEntry.Domain, Subject: oauthEntry.Subject!);
        var attachTask = controlHost.AttachAsync(target: "row", identity: ownerIdentity, cancellationToken: ct).AsTask();

        while (!attachTask.IsCompleted) {
            source.Collect();
            await Task.Yield();
        }
        using var attached = await attachTask;
        Assert.True(condition: silo.Instances.TryGet(name: "row", instance: out var instance));
        var server = instance!.Server;
        var peer = server.Population.PeerPrincipal(index: 5);

        Assert.True(condition: server.IsLivePeer(principal: peer));

        var updatedAdmission = (removeEntry
            ? new[] { otherEntry }
            : new[] {
                new WorldAdmissionEntry(
                    Domain: oauthEntry.Domain,
                    Subject: oauthEntry.Subject,
                    Mode: WorldAdmissionTrustMode.OAuth,
                    Disclosure: WorldDisclosureTier.Presentation,
                    Grants: []
                ),
                otherEntry,
            });
        var candidate = (server.Definition with { Admission = updatedAdmission });

        server.EnqueueRebuild(
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Load,
                Definition: candidate,
                Origin: new WorldRebuildOrigin.File(Path: "silo-eviction.world.json"),
                Force: true,
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate))
            ),
            principal: Principal.Console
        );
        silo.Instances.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        silo.NoteMasterStep(stepTicks: Fixtures.StepTicks);

        // The session closes off the tick thread, so a call racing the close may be canceled before the session
        // refuses calls outright; the law is that it ends up refusing them.
        var closed = false;

        for (var attempt = 0; (!closed && (attempt < 400)); attempt++) {
            try {
                _ = await attached.ExecuteAsync(
                    request: new ControlRequest(Command: "world.peers", Id: (attempt + 1), Operation: "exec", TimeoutMilliseconds: 1000),
                    cancellationToken: ct
                );
            } catch (ObjectDisposedException) {
                closed = true;
            } catch (OperationCanceledException) {
            }
            if (!closed) { await Task.Delay(millisecondsDelay: 10, cancellationToken: ct); }
        }
        Assert.True(condition: closed, userMessage: "the control session stayed open after its OAuth admission was removed or downgraded");

        // Drain the queued disconnect on the command pump and step once so the single NetworkPlayers slot frees.
        for (var pass = 0; ((pass < 8) && server.IsLivePeer(principal: peer)); pass++) {
            source.Collect();
            silo.Instances.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            await Task.Yield();
        }
        Assert.False(condition: server.IsLivePeer(principal: peer));

        var replacementIdentity = new ControlIdentity(Issuer: otherEntry.Domain, Subject: otherEntry.Subject!);
        var replacementTask = controlHost.AttachAsync(target: "row", identity: replacementIdentity, cancellationToken: ct).AsTask();

        while (!replacementTask.IsCompleted) {
            source.Collect();
            await Task.Yield();
        }
        using var replacement = await replacementTask;
        Assert.True(condition: (server.IsLivePeer(principal: server.Population.PeerPrincipal(index: 4)) || server.IsLivePeer(principal: server.Population.PeerPrincipal(index: 5))));
    }
    private sealed class ProbeModule : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            foreach (var (name, audience) in new[] { ("world.peers", CommandAudience.Operator), ("host.probe", CommandAudience.Host) }) {
                yield return CommandDefinition.Verb(name: name, audience: audience, bindability: CommandBindability.Unbindable,
                    description: "Exercises remote command admission.", valueKind: CommandValueKind.Digital, handler: _ => CommandResult.None);
            }
        }
    }
}
