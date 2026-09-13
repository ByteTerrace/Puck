using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Automation;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.World;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseFixtureBuilderTests {
    [Fact]
    public async Task BootstrapAndCapturedExportsPreserveInventoryAndRefuseIncompleteState() {
        var temporary = Directory.CreateTempSubdirectory("puck-fixture-builder-");
        try {
            var services = new ServiceCollection();
            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
            using var provider = services.BuildServiceProvider();
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var owner = Guid.NewGuid();
            var remote = new DirectoryObjectStorageTarget(Path.Combine(temporary.FullName, "remote"));
            var archive = new WorldReleaseArchive(blobs, remote, owner);
            var snapshots = new WorldReleaseFixtureArchive(blobs, remote, owner);
            var bytes = WorldDefinitionSerialization.Serialize(new WorldDefinition(HostRaw: WorldHostDefaults.Absent with {
                Width = 320, Height = 200, TargetHertz = 60, Authority = "localhost:7825",
            }));
            var package = Directory.CreateDirectory(Path.Combine(temporary.FullName, "package"));
            File.WriteAllBytes(Path.Combine(package.FullName, "world.json"), bytes);
            var token = TestContext.Current.CancellationToken;
            var image = Environment.GetEnvironmentVariable("PUCK_TEST_WORLD_IMAGE");
            var digest = image is null ? "sha256:" + new string('a', 64) : JsonNode.Parse(await CliProcess.RunCheckedAsync(Environment.CurrentDirectory,
                "docker", ["image", "inspect", image], capture: true, cancellationToken: token))![0]!["Id"]!.GetValue<string>();
            var release = new WorldReleaseManifest {
                Label = "fixture", SourceRevision = "test", EngineImageDigest = digest, PersistenceContract = "test", PeerProtocolContract = "test",
                Definitions = new[] { "alpha", "beta" }.ToDictionary(world => $"{owner:D}/{world}", _ => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes))),
                DefinitionFiles = new[] { "alpha", "beta" }.ToDictionary(world => $"{owner:D}/{world}", _ => "world.json"),
            };
            await archive.SaveAsync(release, package.FullName, token);
            var output = Path.Combine(temporary.FullName, "fixture");
            var builder = new WorldReleaseFixtureBuilder(blobs, archive, snapshots);
            await builder.BuildAsync(release, owner, null, output, token);
            Assert.Equal("puck.world.qualification.v1", File.ReadAllText(Path.Combine(output, "qualification.fixture")));
            Assert.True(WorldSiloDefinitionSerialization.TryLoadFile(Path.Combine(output, "silo.json"), out var silo, out var reason), reason);
            Assert.Null(silo!.Release);
            Assert.Equal(2, silo.Worlds.Count);
            Assert.Equal(2, silo.Worlds.Select(row => row.Federation.KeyFile).Distinct().Count());
            var local = new DirectoryObjectStorageTarget(Path.Combine(output, "store"));
            var authority = new WorldAuthorityBlobStore(blobs, local);
            foreach (var row in silo.Worlds) {
                Assert.True(row.Pinned);
                Assert.Null(row.Federation.Authentication);
                Assert.StartsWith(output.Replace('\\', '/'), row.Federation.KeyFile);
                Assert.Null(await authority.LoadRootAsync(new(owner, row.World), token));
                Assert.Equal(bytes, (await blobs.ReadAsync(local, WorldOwnedWorldSync.HostedAddressFor(owner, row.World, "definition.json"), token))!.Value.Content.ToArray());
            }
            await Assert.ThrowsAsync<IOException>(() => builder.BuildAsync(release, owner, null, output, token));
            var groups = new WorldReleaseGroupStore(blobs, local, owner);
            Assert.True((await groups.CreateAsync("official", release.Identity, token)).Ok);
            using var console = new BufferedConsoleOutput();
            var commands = new TextCommandSource(new CommandRegistry(modules: []));
            var routing = new SiloConsoleRouting(() => commands, new SiloConsoleTagging(console));
            var host = new WorldSiloHost(silo with { Release = new("official", owner, release.Identity) }, blobs, routing, local);
            using var instances = host.Instances;
            foreach (var row in silo.Worlds) {
                var activating = host.ActivateAsync(new(owner, row.World), token);
                await PumpAsync(host, activating, token);
                Assert.True(await activating);
            }
            var publishing = host.PublishManagedReleaseAdmissionAsync(token);
            await PumpAsync(host, publishing, token);
            Assert.Equal(WorldReleaseAdmissionPublication.Opened, await publishing);
            var capturing = host.ExportReleaseFixtureAsync(Guid.NewGuid(), token);
            await PumpAsync(host, capturing, token);
            var capture = await capturing;
            var capturedArchive = new WorldReleaseFixtureArchive(blobs, local, owner);
            var complete = Path.Combine(temporary.FullName, "captured");
            await new WorldReleaseFixtureBuilder(blobs, archive, capturedArchive).BuildAsync(release, owner, capture, complete, token);
            var restored = new WorldAuthorityBlobStore(blobs, new DirectoryObjectStorageTarget(Path.Combine(complete, "store")));
            foreach (var row in silo.Worlds) {
                var saved = await restored.LoadRecoveryAsync(new(owner, row.World), token);
                Assert.NotNull(saved);
                Assert.Equal((await capturedArchive.ReadCheckpointAsync(capture, row.World.Value, token)).ToArray(), saved.Value.Checkpoint!.Value.Encoded.ToArray());
            }
            Assert.Equal(capture.MachineId.ToString("D"), File.ReadAllText(Path.Combine(complete, "state", "silo-machine.id")));
            if (image is not null) {
                var evidence = Environment.GetEnvironmentVariable("PUCK_TEST_RELEASE_EVIDENCE_DIRECTORY") ?? Path.Combine(temporary.FullName, "evidence");
                var runner = new WorldReleaseQualificationRunner(complete, Path.GetFullPath(evidence), image, image, 4);
                Assert.NotNull(await runner.RunAsync(release, release with { Label = "candidate" }, token));
            }
            await PumpAsync(host, host.DrainAsync(token), token);
            var invalid = await snapshots.SaveAsync(Guid.NewGuid(), "official", release.Identity, Guid.NewGuid(),
                new[] { "alpha", "beta" }.ToDictionary(world => world, _ => new WorldReleaseFixtureCheckpoint("invalid checkpoint"u8.ToArray(), 10)), token);
            var partial = Path.Combine(temporary.FullName, "partial");
            await Assert.ThrowsAsync<InvalidDataException>(() => builder.BuildAsync(release, owner, invalid, partial, token));
            Assert.False(File.Exists(Path.Combine(partial, "qualification.fixture")));
        } finally { temporary.Delete(recursive: true); }
    }

    private static async Task PumpAsync(WorldSiloHost host, Task operation, CancellationToken token) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!operation.IsCompleted) { host.DrainActivationMailbox(); await Task.Delay(1, deadline.Token); }
        await operation;
        host.DrainActivationMailbox();
    }
}
