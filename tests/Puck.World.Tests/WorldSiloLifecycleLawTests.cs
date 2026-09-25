using Puck.Testing;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldSiloLifecycleLawTests {
    private static WorldSiloHost Host(string directory, IObjectBlobStore store, BufferedConsoleOutput output, WorldSiloWorldRow[] worlds) {
        var source = new TextCommandSource(new CommandRegistry(modules: []));

        return new(
            new(
                worlds,
                new(Budget: 1),
                new(
                    "directory",
                    JsonElement.Parse("{}")
                ),
                directory,
                new(Kind: "Localhost")
            ),
            store,
            new(
                source: () => source,
                tagging: new SiloConsoleTagging(output: output)
            ),
            new DirectoryObjectStorageTarget(directory)
        );
    }
    private static async Task PublishAsync(WorldSiloHost host, WorldAuthorityIdentity identity, WorldDefinition definition) {
        var publication = host.PublishDefinitionAsync(
            identity,
            definition,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host,
            publication
        );
        var result = await publication;

        Assert.True(
            condition: result.Ok,
            userMessage: result.Detail
        );
    }
    // A stepping pump advances the rows one master step per pass, so an operation that completes at a step boundary
    // is driven by steps taken, never by time waited.
    private static async Task PumpAsync(WorldSiloHost host, Task operation, bool step = false) {
        if (!step) {
            await WorldSiloHost.PumpActivationMailboxesAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                hosts: [host],
                operation: operation
            );

            return;
        }

        while (!operation.IsCompleted) {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            host.DrainActivationMailbox();
            if (!host.IsDraining) { host.Instances.StepInstances(masterDeltaTicks: Fixtures.StepTicks); host.NoteMasterStep(stepTicks: Fixtures.StepTicks); }
            await Task.Yield();
        }
        await operation;
        host.DrainActivationMailbox();
    }

    // Law: a row whose published listen endpoint another socket already holds fails its activation with the endpoint
    // named, the row does not stay admitted, and the host keeps that failure for the silo's unsupported-environment exit.
    [Fact]
    public async Task ARowDoorThatCannotBindItsEndpointFailsActivationAndIsKeptForTheExit() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var occupant = new System.Net.Sockets.Socket(
            addressFamily: System.Net.Sockets.AddressFamily.InterNetwork,
            protocolType: System.Net.Sockets.ProtocolType.Udp,
            socketType: System.Net.Sockets.SocketType.Dgram
        );

        occupant.Bind(localEP: new System.Net.IPEndPoint(
            address: System.Net.IPAddress.Loopback,
            port: 0
        ));
        var endpoint = occupant.LocalEndPoint!.ToString()!;
        var keyFile = Path.Combine(
            path1: directory.RootPath,
            path2: "world.key"
        );

        File.WriteAllBytes(
            keyFile,
            key.ExportPkcs8PrivateKey()
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "row")
        );
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: new DirectoryObjectStorageTarget(directory.RootPath)
        );
        var definition = Fixtures.BuildDocument() with {
            HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = endpoint, Presentation = WorldHostPresentation.None },
        };

        Assert.True(condition: (await backend.PublishDefinitionAsync(
            identity,
            definition,
            TestContext.Current.CancellationToken
        )).Ok);
        var host = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var instances = host.Instances;

        Assert.Null(@object: host.HostUnavailable);
        var refused = await Assert.ThrowsAsync<Puck.Abstractions.ListenEndpointUnavailableException>(testCode: () => PumpAsync(
            host,
            host.ActivateAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        ));

        Assert.Equal(
            endpoint,
            refused.Endpoint
        );
        Assert.Same(
            refused,
            host.HostUnavailable
        );
        Assert.False(condition: host.Instances.TryGet(
            identity.World.Value,
            out _
        ));
    }
    [Fact]
    public async Task CancellationBeforeThePumpDoesNotFreezeTheHost() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var host = Host(
            directory.RootPath,
            PuckStorageTestComposition.BuildStore(),
            output,
            []
        );
        using var instances = host.Instances;
        using var cancellation = new CancellationTokenSource();
        var drain = host.DrainAsync(ct: cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => drain);
        host.DrainActivationMailbox();
        Assert.False(condition: host.IsDraining);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ConcurrentDrainCallersObserveTheirOwnCancellationAndCanRetry(bool cancelFirst) {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var host = Host(
            directory.RootPath,
            PuckStorageTestComposition.BuildStore(),
            output,
            []
        );
        using var instances = host.Instances;
        using var cancellation = new CancellationTokenSource();
        var first = host.DrainAsync(ct: (cancelFirst
            ? cancellation.Token
            : TestContext.Current.CancellationToken));
        var second = host.DrainAsync(ct: (cancelFirst
            ? TestContext.Current.CancellationToken
            : cancellation.Token));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => (cancelFirst
            ? first
            : second));
        await PumpAsync(
            host: host,
            operation: (cancelFirst
            ? second
            : first)
        );
        Assert.True(condition: host.IsDraining);
        await host.DrainAsync(ct: TestContext.Current.CancellationToken);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task DuplicateActivationKeepsItsFenceAndReplacementRejectsTheOldWriter(bool colocated) {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(
            path1: directory.RootPath,
            path2: "world.key"
        );

        File.WriteAllBytes(
            keyFile,
            key.ExportPkcs8PrivateKey()
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "row")
        );
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: new DirectoryObjectStorageTarget(directory.RootPath)
        );
        var definition = Fixtures.BuildDocument() with {
            HostRaw = Fixtures.StandardHost with {
                Authority = (colocated
            ? null
            : "localhost:7825"),
                Listen = null,
                Presentation = WorldHostPresentation.None,
            },
        };

        Assert.True(condition: (await backend.PublishDefinitionAsync(
            identity,
            definition,
            TestContext.Current.CancellationToken
        )).Ok);
        var original = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var originalInstances = original.Instances;

        await PumpAsync(
            original,
            original.ActivateAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        );
        Assert.True(condition: original.Instances.TryGet(
            identity.World.Value,
            out var active
        ));
        Assert.Equal(
            (colocated
            ? identity.World.Value
            : "localhost:7825"),
            active!.Federation.Subject
        );
        Assert.Equal(
            active.Server.AuthorityIdentity,
            active.Federation.Subject
        );
        Assert.Null(@object: active.ListenEndpoint);
        var first = (await backend.LoadRootAsync(
            identity,
            TestContext.Current.CancellationToken
        ))!.Value;

        Assert.NotNull(value: await backend.LoadLatestAsync(
            identity,
            TestContext.Current.CancellationToken
        ));
        await PumpAsync(
            original,
            original.ActivateAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            first.Root.Epoch,
            (await backend.LoadRootAsync(
                identity,
                TestContext.Current.CancellationToken
            ))!.Value.Root.Epoch
        );

        var replacement = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var replacementInstances = replacement.Instances;

        await PumpAsync(
            replacement,
            replacement.ActivateAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        );
        var current = (await backend.LoadRootAsync(
            identity,
            TestContext.Current.CancellationToken
        ))!.Value;

        Assert.True(condition: (current.Root.Epoch > first.Root.Epoch));
        var stale = original.CheckpointNowAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            original,
            stale
        );
        Assert.False(condition: await stale);
        Assert.Equal(
            current,
            (await backend.LoadRootAsync(
                identity,
                TestContext.Current.CancellationToken
            ))!.Value
        );
        var live = replacement.CheckpointNowAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            replacement,
            live
        );
        Assert.True(condition: await live);
        await PumpAsync(
            replacement,
            replacement.DrainAsync(ct: TestContext.Current.CancellationToken)
        );
    }
    [Fact]
    public async Task FailedDeactivationAndFinalSaveRetainTheRowAndAllowDurableDrainRetry() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(
            path1: directory.RootPath,
            path2: "world.key"
        );

        File.WriteAllBytes(
            keyFile,
            key.ExportPkcs8PrivateKey()
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "row")
        );
        var store = new FailingWrites(inner: PuckStorageTestComposition.BuildStore());
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: target
        );
        var definition = Fixtures.BuildDocument();

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Presentation = WorldHostPresentation.None } };
        var published = await backend.PublishDefinitionAsync(
            identity,
            definition,
            TestContext.Current.CancellationToken
        );

        Assert.True(
            condition: published.Ok,
            userMessage: published.Detail
        );
        var host = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var instances = host.Instances;
        var activation = host.ActivateAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: host,
            operation: activation
        );
        Assert.True(condition: await activation);

        store.Fail = true;
        var deactivation = host.DeactivateAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await Assert.ThrowsAsync<IOException>(testCode: () => PumpAsync(
            host: host,
            operation: deactivation
        ));
        Assert.NotNull(@object: host.TryDescribeRow(worldId: "row"));
        var failedDrain = host.DrainAsync(ct: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(testCode: () => PumpAsync(
            host: host,
            operation: failedDrain
        ));
        Assert.True(condition: host.IsDraining);
        Assert.NotNull(@object: host.TryDescribeRow(worldId: "row"));

        store.Fail = false;
        await PumpAsync(
            host: host,
            operation: host.DrainAsync(ct: TestContext.Current.CancellationToken)
        );
        Assert.NotNull(value: await backend.LoadLatestAsync(
            identity,
            TestContext.Current.CancellationToken
        ));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task PublishedReloadUsesExistingRebuildAndCommitsOnlyAfterCheckpoint(bool failCheckpoint) {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(
            path1: directory.RootPath,
            path2: "world.key"
        );

        File.WriteAllBytes(
            keyFile,
            key.ExportPkcs8PrivateKey()
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "row")
        );
        var store = new FailingWrites(inner: PuckStorageTestComposition.BuildStore());
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: target
        );
        var definition = Fixtures.BuildDocument();

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Presentation = WorldHostPresentation.None } };
        Assert.True(condition: (await backend.PublishDefinitionAsync(
            identity,
            definition,
            TestContext.Current.CancellationToken
        )).Ok);
        var host = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var instances = host.Instances;
        var activation = host.ActivateAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: host,
            operation: activation
        );
        Assert.True(condition: await activation);
        var reload = host.ReloadAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: host,
            operation: reload,
            step: true
        );
        var hash = await reload;
        var first = await backend.LoadLatestAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(value: first);
        var again = host.ReloadAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: host,
            operation: again,
            step: true
        );
        Assert.Equal(
            hash,
            await again
        );
        Assert.Equal(
            first.Value.Ordinal,
            (await backend.LoadLatestAsync(
                identity,
                TestContext.Current.CancellationToken
            ))!.Value.Ordinal
        );
        var changed = definition with { Metadata = new(Title: "Published update") };

        await PublishAsync(
            definition: changed,
            host: host,
            identity: identity
        );
        Assert.True(condition: host.Instances.TryGet(
            identity.World.Value,
            out var active
        ));
        var rebuilds = 0;

        active!.Server.EchoTap += echo => {
            if (
            (echo.Kind == WorldEditEchoKind.Rebuild) &&
            !echo.Rejected
        ) { rebuilds++; }
        };
        store.Fail = failCheckpoint;
        var update = host.ReloadAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        if (failCheckpoint) {
            await Assert.ThrowsAsync<IOException>(testCode: () => PumpAsync(
                host,
                update,
                step: true
            ));
            Assert.Equal(
                first.Value.Ordinal,
                (await backend.LoadLatestAsync(
                    identity,
                    TestContext.Current.CancellationToken
                ))!.Value.Ordinal
            );
            Assert.Equal(
                "Published update",
                active.Server.Definition.Metadata!.Title
            );
            store.Fail = false;
            update = host.ReloadAsync(
                identity,
                TestContext.Current.CancellationToken
            );
        }
        await PumpAsync(
            host,
            update,
            step: true
        );
        Assert.NotEqual(
            hash,
            await update
        );
        Assert.Equal(
            actual: rebuilds,
            expected: 1
        );
        Assert.Equal(
            "Published update",
            active!.Server.Definition.Metadata!.Title
        );
        var saved = (await backend.LoadLatestAsync(
            identity,
            TestContext.Current.CancellationToken
        ))!.Value;

        Assert.True(condition: (saved.Ordinal > first.Value.Ordinal));
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: saved.Encoded.Span,
                checkpoint: out var decoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            "Published update",
            WorldDefinitionSerialization.Deserialize(utf8Json: decoded!.Server.DefinitionJson).Metadata!.Title
        );

        var refused = changed with { HostRaw = changed.Host with { Authority = "another-host:7825" } };

        await PublishAsync(
            definition: refused,
            host: host,
            identity: identity
        );
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => PumpAsync(
            host,
            host.ReloadAsync(
                identity,
                TestContext.Current.CancellationToken
            ),
            step: true
        ));
        Assert.Equal(
            saved.Ordinal,
            (await backend.LoadLatestAsync(
                identity,
                TestContext.Current.CancellationToken
            ))!.Value.Ordinal
        );
        // Simulate losing the release-marker write after the rebuilt checkpoint became durable.
        await PublishAsync(
            definition: changed,
            host: host,
            identity: identity
        );
        await store.WriteAsync(
            target,
            WorldOwnedWorldSync.HostedAddressFor(
                identity.Owner,
                identity.World,
                "release"
            ),
            System.Text.Encoding.UTF8.GetBytes(s: hash),
            ObjectBlobWriteMode.Overwrite,
            cancellationToken: TestContext.Current.CancellationToken
        );
        await PumpAsync(
            host,
            host.DrainAsync(ct: TestContext.Current.CancellationToken)
        );
        var recovered = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var recoveredInstances = recovered.Instances;

        await PumpAsync(
            recovered,
            recovered.ActivateAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        );
        Assert.True(condition: recovered.Instances.TryGet(
            identity.World.Value,
            out var restored
        ));
        var recoveryRebuilds = 0;

        restored!.Server.EchoTap += echo => { if (echo.Kind == WorldEditEchoKind.Rebuild) { recoveryRebuilds++; } };
        // Marker reconciliation must complete without a simulation step or another rebuild.
        await PumpAsync(
            recovered,
            recovered.ReloadAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            actual: recoveryRebuilds,
            expected: 0
        );
        Assert.Equal(
            "Published update",
            restored.Server.Definition.Metadata!.Title
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ReplacementActivationUsesPublishedNetworkBindingAfterCheckpointRecovery(bool listen) {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(
            path1: directory.RootPath,
            path2: "world.key"
        );

        File.WriteAllBytes(
            keyFile,
            key.ExportPkcs8PrivateKey()
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "row")
        );
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: new DirectoryObjectStorageTarget(directory.RootPath)
        );
        var definition = Fixtures.BuildDocument();

        definition = definition with { HostRaw = Fixtures.StandardHost with { Authority = "old.example:33333", Listen = null, Presentation = WorldHostPresentation.None } };
        Assert.True(condition: (await backend.PublishDefinitionAsync(
            identity,
            definition,
            TestContext.Current.CancellationToken
        )).Ok);
        var original = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var originalInstances = original.Instances;

        await PumpAsync(
            original,
            original.ActivateAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        );
        await PumpAsync(
            original,
            original.DrainAsync(ct: TestContext.Current.CancellationToken)
        );
        // Port 0 has the door's own bind pick a free port, so no other process can take a port this law reserved and
        // released before the replacement binds it.
        var endpoint = (listen
            ? "127.0.0.1:0"
            : null
        );
        var published = definition with { HostRaw = definition.Host with { Authority = "play.puck.byteterrace.com:7825", Listen = endpoint } };

        Assert.True(condition: (await backend.PublishDefinitionAsync(
            identity,
            published,
            TestContext.Current.CancellationToken
        )).Ok);
        var replacement = Host(
            directory.RootPath,
            store,
            output,
            [new(
                    identity.Owner,
                    identity.World,
                    new(KeyFile: keyFile)
                )]
        );
        using var replacementInstances = replacement.Instances;

        await PumpAsync(
            replacement,
            replacement.ActivateAsync(
                identity,
                TestContext.Current.CancellationToken
            )
        );
        Assert.True(condition: replacement.Instances.TryGet(
            identity.World.Value,
            out var row
        ));
        Assert.Equal(
            definition.Host.Authority,
            row!.Server.Definition.Host.Authority
        );
        Assert.Equal(
            published.Host.Authority,
            row.Federation.Subject
        );
        Assert.Equal(
            endpoint,
            row.ListenEndpoint
        );
        if (listen) {
            var bound = System.Net.IPEndPoint.Parse(s: row.Door!.ListenEndpoint!);

            Assert.Equal(
                System.Net.IPAddress.Loopback,
                bound.Address
            );
            Assert.NotEqual(
                0,
                bound.Port
            );
        } else {
            Assert.Null(@object: row.Door!.ListenEndpoint);
        }
        await PumpAsync(
            replacement,
            replacement.ReloadAsync(
                identity,
                TestContext.Current.CancellationToken
            ),
            step: true
        );
        Assert.Equal(
            published.Host.Authority,
            row.Server.Definition.Host.Authority
        );
        Assert.Equal(
            endpoint,
            row.Server.Definition.Host.Listen
        );
        Assert.NotNull(value: await backend.LoadLatestAsync(
            identity,
            TestContext.Current.CancellationToken
        ));
        await PumpAsync(
            replacement,
            replacement.DrainAsync(ct: TestContext.Current.CancellationToken)
        );
    }
    // Law: an activated row resolves views.pipelines rows against the executable's own directory — a hosted
    // world's definition arrives from cloud storage, never a local file, so it has no document directory of its
    // own — rather than refusing every override with SourcesUnattached, and the same boot check the desktop host
    // runs right after loading a document runs here too: a bad override value in the activated document is
    // refused BY NAME at activation, before the row ever becomes active.
    [Fact]
    public async Task AnActivatedRowAttachesPipelineSourcesAndRunsTheBootBindCheck() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        // A hosted document has no directory, so it names its pipeline by an absolute path.
        var pipelinePath = PuckPaths.Normalize(path: Path.Combine(
            path1: directory.RootPath,
            path2: "silo.graph.json"
        ));

        File.Copy(
            destFileName: pipelinePath,
            sourceFileName: Path.Combine(
                path1: AuthoredGameFixtures.Root,
                path2: "src/Puck.World/Assets/pipelines/ink.graph.json"
            )
        );

        {
            static WorldDefinition WithPipelineRow(string source, double exposure) {
                var definition = Fixtures.BuildDocument();

                return (definition with {
                    HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Presentation = WorldHostPresentation.None },
                    ViewsRaw = (definition.Views with {
                        Pipelines = [
                            new WorldViewPipeline(
                                Name: "left",
                                Source: source,
                                Overrides: new Dictionary<string, JsonElement> {
                                    ["visualize"] = JsonDocument.Parse(json: $"{{\"exposure\":{exposure.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)}}}").RootElement.Clone(),
                                }
                            ),
                        ],
                    }),
                });
            }

            var store = PuckStorageTestComposition.BuildStore();
            var backend = new WorldAuthorityBlobStore(
                store: store,
                target: new DirectoryObjectStorageTarget(directory.RootPath)
            );

            async Task<(bool Activated, WorldSiloHost Host, WorldAuthorityIdentity Identity)> TryActivateAsync(string worldName, double exposure, string? source = null) {
                using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
                var keyFile = Path.Combine(
                    path1: directory.RootPath,
                    path2: $"{worldName}.key"
                );

                File.WriteAllBytes(
                    keyFile,
                    key.ExportPkcs8PrivateKey()
                );

                var identity = new WorldAuthorityIdentity(
                    Owner: Guid.NewGuid(),
                    World: SafeName.Parse(candidate: worldName)
                );

                Assert.True(condition: (await backend.PublishDefinitionAsync(
                    identity,
                    WithPipelineRow(exposure: exposure, source: (source ?? pipelinePath)),
                    TestContext.Current.CancellationToken
                )).Ok);

                var host = Host(
                    directory.RootPath,
                    store,
                    output,
                    [new(identity.Owner, identity.World, new(KeyFile: keyFile))]
                );
                var activation = host.ActivateAsync(
                    identity,
                    TestContext.Current.CancellationToken
                );

                await PumpAsync(
                    host: host,
                    operation: activation
                );

                return (await activation, host, identity);
            }

            // Control: a valid override activates, and the reader is attached with no directory of its own.
            var (controlActivated, controlHost, controlIdentity) = await TryActivateAsync(exposure: 4, worldName: "row-good");

            using (controlHost.Instances) {
                Assert.True(condition: controlActivated);
                Assert.True(condition: controlHost.Instances.TryGet(
                    controlIdentity.World.Value,
                    out var row
                ));
                Assert.NotNull(@object: row!.Server.PipelineSources);
                Assert.Null(@object: row.Server.PipelineSources!.DocumentDirectory);
            }

            // Denied: a relative source in a document with no directory resolves nowhere, and is refused by name.
            var (relativeActivated, relativeHost, _) = await TryActivateAsync(exposure: 4, source: "silo.graph.json", worldName: "row-relative");

            using (relativeHost.Instances) {
                Assert.False(condition: relativeActivated);

            }

            // Denied: an out-of-range override is refused at activation — the boot check — rather than installing
            // an instance that can never accept it.
            var (deniedActivated, deniedHost, deniedIdentity) = await TryActivateAsync(exposure: 4.5, worldName: "row-bad");

            using (deniedHost.Instances) {
                Assert.False(condition: deniedActivated);
                Assert.False(condition: deniedHost.Instances.TryGet(
                    deniedIdentity.World.Value,
                    out _
                ));
            }
        }
    }

    private sealed class FailingWrites(IObjectBlobStore inner) : IObjectBlobStore {
        internal bool Fail { get; set; }

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) => inner.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: keyPrefix,
            objectId: objectId,
            target: target
        );
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) =>
            (Fail
                ? ValueTask.FromException<ObjectBlobWriteResult>(exception: new IOException(message: "injected storage failure"))
                : inner.WriteAsync(
                    address: address,
                    cancellationToken: cancellationToken,
                    content: content,
                    ifMatchVersion: ifMatchVersion,
                    mode: mode,
                    target: target
                )
            );
    }
}
