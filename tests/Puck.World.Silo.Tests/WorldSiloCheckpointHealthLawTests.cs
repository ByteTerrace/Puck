using System.Security.Cryptography;
using System.Text.Json;
using Puck.Commands;
using Puck.Launcher;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Silo.Tests;

/// <summary>Checkpoint age counts only state the landed checkpoint does not cover: a row frozen at its checkpoint stays
/// healthy however long ago that checkpoint landed, and a row that steps past it is overdue once
/// <c>lifecycle.checkpointTimeoutSeconds</c> passes on the silo clock without a covering checkpoint.</summary>
public sealed class WorldSiloCheckpointHealthLawTests {
    private const int CheckpointTimeoutSeconds = 180;

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static async Task<string> HealthAsync(WorldSiloHost host) {
        var health = host.CheckHealthAsync(cancellationToken: TestToken);

        await WorldSiloHost.PumpActivationMailboxesAsync(
            cancellationToken: TestToken,
            hosts: [host],
            operation: health
        );
        return await health;
    }

    [Fact]
    public async Task AFrozenRowStaysHealthyPastTheTimeoutAndASteppedRowGoesOverdue() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(
            path1: directory.RootPath,
            path2: "world.key"
        );

        File.WriteAllBytes(
            bytes: key.ExportPkcs8PrivateKey(),
            path: keyFile
        );
        var identity = new WorldAuthorityIdentity(
            Owner: Guid.NewGuid(),
            World: SafeName.Parse(candidate: "row")
        );
        var clock = new VirtualClock();
        var store = PuckStorageTestComposition.BuildStore();
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath);

        Assert.True(condition: (await new WorldAuthorityBlobStore(
            store: store,
            target: target,
            timeProvider: clock,
            documentDirectory: null
        ).PublishDefinitionAsync(
            identity,
            Fixtures.BuildDocument(),
            TestToken
        )).Ok);
        var source = new TextCommandSource(new CommandRegistry(modules: []));
        var host = new WorldSiloHost(
            blobStore: store,
            definition: new(
                Worlds: [new(
                    Federation: new(KeyFile: keyFile),
                    Owner: identity.Owner,
                    Pinned: true,
                    World: identity.World
                )],
                Doors: new(Budget: 1),
                Store: new(
                    Settings: JsonElement.Parse(json: "{}"),
                    Type: "directory"
                ),
                StateDir: directory.RootPath,
                Clustering: new(Kind: "Localhost"),
                Lifecycle: new(
                    CheckpointTimeoutSeconds: CheckpointTimeoutSeconds,
                    HealthPort: 0,
                    ShutdownSeconds: 30
                )
            ),
            routing: new(
                source: () => source,
                tagging: new SiloConsoleTagging(output: output)
            ),
            storageTarget: target,
            timeProvider: clock
        );
        using var instances = host.Instances;
        var activation = host.ActivateAsync(
            ct: TestToken,
            identity: identity
        );

        await WorldSiloHost.PumpActivationMailboxesAsync(
            cancellationToken: TestToken,
            hosts: [host],
            operation: activation
        );
        Assert.True(condition: await activation);
        host.Ready = true;
        Assert.Equal(
            actual: await HealthAsync(host: host),
            expected: ""
        );

        // Frozen at its checkpoint: the master pump keeps beating, the row never steps, nothing is journaled.
        clock.Advance(by: TimeSpan.FromSeconds(seconds: (CheckpointTimeoutSeconds + 20)));
        host.NoteMasterStep(stepTicks: Fixtures.StepTicks);
        Assert.Equal(
            actual: await HealthAsync(host: host),
            expected: ""
        );

        // One step past the checkpoint starts the age; the timeout then passes without a covering checkpoint.
        Assert.True(condition: host.Instances.TryGet(
            instance: out var row,
            name: identity.World.Value
        ));
        var tick = row!.CompletedTicks;

        host.Instances.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
        host.NoteMasterStep(stepTicks: Fixtures.StepTicks);
        Assert.True(condition: (row.CompletedTicks > tick));
        Assert.Equal(
            actual: await HealthAsync(host: host),
            expected: ""
        );
        clock.Advance(by: TimeSpan.FromSeconds(seconds: (CheckpointTimeoutSeconds + 1)));
        host.NoteMasterStep(stepTicks: Fixtures.StepTicks);
        Assert.Equal(
            actual: await HealthAsync(host: host),
            expected: $"{identity.World}: checkpoint overdue"
        );
    }
}
