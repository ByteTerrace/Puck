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
            var candidateDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName, "candidate"));
            var candidateDefinition = WorldDefinitionSerialization.Deserialize(bytes) with { Metadata = new(Title: "Release B") };
            var candidateBytes = WorldDefinitionSerialization.Serialize(candidateDefinition);
            File.WriteAllBytes(Path.Combine(candidateDirectory.FullName, "world.json"), candidateBytes);
            var candidate = release with {
                Label = "metadata candidate", CoordinatorContract = WorldReleaseManifest.MetadataCoordinatorContract,
                Definitions = release.Definitions.ToDictionary(row => row.Key, _ => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(candidateBytes))),
            };
            await archive.SaveAsync(candidate, candidateDirectory.FullName, token);
            var metadataCopy = Path.Combine(temporary.FullName, "metadata-copy");
            await new WorldReleaseFixtureBuilder(blobs, archive, capturedArchive).BuildAsync(release, owner, capture, metadataCopy, token);
            var transition = new WorldReleaseQualificationTransition(blobs, archive);
            await transition.ApplyAsync(metadataCopy, silo, release, candidate, token);
            var transformed = new WorldAuthorityBlobStore(blobs, new DirectoryObjectStorageTarget(Path.Combine(metadataCopy, "store")));
            foreach (var row in silo.Worlds) {
                var identity = new WorldAuthorityIdentity(owner, row.World);
                Assert.Equal(candidateBytes, WorldDefinitionSerialization.Serialize((await transformed.LoadDefinitionAsync(identity, token))!));
                var saved = (await transformed.LoadRecoveryAsync(identity, token))!.Value;
                Assert.True(WorldAuthorityCheckpointCodec.TryDecode(saved.Checkpoint!.Value.Encoded.Span, out var checkpoint, out reason), reason);
                Assert.Equal("Release B", WorldDefinitionSerialization.Deserialize(checkpoint!.Server.DefinitionJson).Metadata!.Title);
                Assert.Equal(capture.Worlds[row.World.Value].Tick, checkpoint.Server.LastCompletedTick);
            }
            await transition.ApplyAsync(metadataCopy, silo, candidate, release, token);
            foreach (var row in silo.Worlds) {
                var saved = (await transformed.LoadRecoveryAsync(new(owner, row.World), token))!.Value;
                Assert.Equal((await capturedArchive.ReadCheckpointAsync(capture, row.World.Value, token)).ToArray(), saved.Checkpoint!.Value.Encoded.ToArray());
                Assert.Equal(bytes, WorldDefinitionSerialization.Serialize((await transformed.LoadDefinitionAsync(new(owner, row.World), token))!));
            }
            var missingPackages = new WorldReleaseQualificationRunner(complete, Path.Combine(temporary.FullName, "missing-packages"), "missing", "missing");
            var missing = await Assert.ThrowsAsync<InvalidDataException>(() => missingPackages.RunAsync(release, candidate, token));
            Assert.Contains("both verified release packages", missing.Message);
            var unsupportedDirectory = Directory.CreateDirectory(Path.Combine(temporary.FullName, "unsupported"));
            var unsupportedBytes = WorldDefinitionSerialization.Serialize(candidateDefinition with {
                HostRaw = WorldHostDefaults.Absent with { Width = 320, Height = 200, TargetHertz = 30, Authority = "localhost:7825" },
            });
            File.WriteAllBytes(Path.Combine(unsupportedDirectory.FullName, "world.json"), unsupportedBytes);
            var unsupported = candidate with {
                Label = "unsupported simulation edit",
                Definitions = release.Definitions.ToDictionary(row => row.Key, _ => "sha256/" + Convert.ToHexStringLower(SHA256.HashData(unsupportedBytes))),
            };
            await archive.SaveAsync(unsupported, unsupportedDirectory.FullName, token);
            var refusedEvidence = Path.Combine(temporary.FullName, "refused-evidence");
            var refusedRunner = new WorldReleaseQualificationRunner(complete, refusedEvidence, "missing", "missing", archive: archive);
            var unsupportedError = await Assert.ThrowsAsync<InvalidDataException>(() => refusedRunner.RunAsync(release, unsupported, token));
            Assert.Contains("outside metadata", unsupportedError.Message);
            Assert.Empty(Directory.EnumerateFiles(refusedEvidence, "receipt.json", SearchOption.AllDirectories));
            if (image is not null) {
                var evidence = Environment.GetEnvironmentVariable("PUCK_TEST_RELEASE_EVIDENCE_DIRECTORY") ?? Path.Combine(temporary.FullName, "evidence");
                var runner = new WorldReleaseQualificationRunner(complete, Path.GetFullPath(evidence), image, image, 4);
                Assert.NotNull(await runner.RunAsync(release, release with { Label = "candidate" }, token));
                var sourceManifestPath = Path.Combine(package.FullName, "release.json");
                var targetManifestPath = Path.Combine(candidateDirectory.FullName, "release.json");
                File.WriteAllBytes(sourceManifestPath, WorldReleaseManifest.Canonicalize(release));
                File.WriteAllBytes(targetManifestPath, WorldReleaseManifest.Canonicalize(candidate));
                // Exercise the public qualify command's package retention as well as its four packaged legs.
                var command = WorldReleaseCommand.Create();
                Assert.Equal(0, await command.Parse(["qualify", sourceManifestPath, targetManifestPath, complete,
                    "--source-image", image, "--target-image", image, "--steps", "4", "--output", Path.GetFullPath(evidence)])
                    .InvokeAsync(cancellationToken: token));
                var proofPath = Directory.EnumerateFiles(evidence, "evidence.json", SearchOption.AllDirectories)
                    .Single(path => JsonNode.Parse(File.ReadAllBytes(path))!["TargetRelease"]!.GetValue<string>() == candidate.Identity);
                var proof = JsonNode.Parse(File.ReadAllBytes(proofPath))!;
                Assert.True(proof["MetadataTransition"]!.GetValue<bool>());
                Assert.NotEqual(proof["SeedHash"]!.GetValue<string>(), proof["ForwardSeedHash"]!.GetValue<string>());
                foreach (var leg in new[] { "target-import", "source-reverse-import" }) {
                    var legStore = new WorldAuthorityBlobStore(blobs, new DirectoryObjectStorageTarget(Path.Combine(Path.GetDirectoryName(proofPath)!, leg, "store")));
                    foreach (var row in silo.Worlds) {
                        var saved = (await legStore.LoadRecoveryAsync(new(owner, row.World), token))!.Value;
                        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(saved.Checkpoint!.Value.Encoded.Span, out var checkpoint, out reason), reason);
                        var expectedTitle = leg == "target-import" ? "Release B" : null;
                        Assert.Equal(expectedTitle, WorldDefinitionSerialization.Deserialize(checkpoint!.Server.DefinitionJson).Metadata?.Title);
                        Assert.Equal(expectedTitle, WorldDefinitionSerialization.Deserialize(checkpoint.Server.BaseDefinitionJson).Metadata?.Title);
                        Assert.Equal(capture.Worlds[row.World.Value].Tick + (leg == "target-import" ? 4UL : 8UL), checkpoint.Server.LastCompletedTick);
                    }
                }
            }
            foreach (var row in silo.Worlds) {
                var saved = (await restored.LoadRecoveryAsync(new(owner, row.World), token))!.Value;
                Assert.Equal((await capturedArchive.ReadCheckpointAsync(capture, row.World.Value, token)).ToArray(), saved.Checkpoint!.Value.Encoded.ToArray());
                Assert.Equal(bytes, WorldDefinitionSerialization.Serialize((await restored.LoadDefinitionAsync(new(owner, row.World), token))!));
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
