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
    private static async Task PumpAsync(WorldSiloHost host, Task operation, CancellationToken token) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: token);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 20));
        while (!operation.IsCompleted) { host.DrainActivationMailbox(); await Task.Delay(
            1,
            deadline.Token
        ); }
        await operation;
        host.DrainActivationMailbox();
    }

    [Fact]
    public async Task BootstrapAndCapturedExportsPreserveInventoryAndRefuseIncompleteState() {
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-fixture-builder-");

        try {
            var services = new ServiceCollection();

            Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
            using var provider = services.BuildServiceProvider();
            var blobs = provider.GetRequiredService<IObjectBlobStore>();
            var owner = Guid.NewGuid();
            var remote = new DirectoryObjectStorageTarget(Path.Combine(
                path1: temporary.FullName,
                path2: "remote"
            ));
            var archive = new WorldReleaseArchive(
                blobs,
                remote,
                owner
            );
            var snapshots = new WorldReleaseFixtureArchive(
                owner: owner,
                store: blobs,
                target: remote
            );
            var bytes = WorldDefinitionSerialization.Serialize(definition: new WorldDefinition(HostRaw: WorldHostDefaults.Absent with {
                Width = 320,
                Height = 200,
                TargetHertz = 60,
                Authority = "localhost:7825",
            }));
            var package = Directory.CreateDirectory(path: Path.Combine(
                path1: temporary.FullName,
                path2: "package"
            ));

            File.WriteAllBytes(
                Path.Combine(
                    path1: package.FullName,
                    path2: "world.json"
                ),
                bytes
            );
            var token = TestContext.Current.CancellationToken;
            var image = Environment.GetEnvironmentVariable(variable: "PUCK_TEST_WORLD_IMAGE");
            var digest = ((image is null)
                ? ("sha256:" + new string(
                    c: 'a',
                    count: 64
                ))
                : JsonNode.Parse(await CliProcess.RunCheckedAsync(
                    Environment.CurrentDirectory,
                    "docker",
                    ["image", "inspect", image],
                    capture: true,
                    cancellationToken: token
                ))![0]!["Id"]!.GetValue<string>()
            );
            var release = new WorldReleaseManifest {
                Label = "fixture",
                SourceRevision = "test",
                EngineImageDigest = digest,
                PersistenceContract = "test",
                PeerProtocolContract = "test",
                Definitions = new[] { "alpha", "beta" }.ToDictionary(
                world => $"{owner:D}/{world}",
                _ => ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)))
            ),
                DefinitionFiles = new[] { "alpha", "beta" }.ToDictionary(
                elementSelector: _ => "world.json",
                keySelector: world => $"{owner:D}/{world}"
            ),
            };

            await archive.SaveAsync(
                release,
                package.FullName,
                token
            );
            var output = Path.Combine(
                path1: temporary.FullName,
                path2: "fixture"
            );
            var builder = new WorldReleaseFixtureBuilder(
                blobs,
                archive,
                snapshots
            );

            await builder.BuildAsync(
                release,
                owner,
                null,
                output,
                token
            );
            Assert.Equal(
                "puck.world.qualification.v1",
                File.ReadAllText(path: Path.Combine(
                    path1: output,
                    path2: "qualification.fixture"
                ))
            );
            Assert.True(
                condition: WorldSiloDefinitionSerialization.TryLoadFile(
                    Path.Combine(
                        path1: output,
                        path2: "silo.json"
                    ),
                    out var silo,
                    out var reason
                ),
                userMessage: reason
            );
            Assert.Null(@object: silo!.Release);
            Assert.Equal(
                2,
                silo.Worlds.Count
            );
            Assert.Equal(
                2,
                silo.Worlds.Select(selector: row => row.Federation.KeyFile).Distinct().Count()
            );
            var local = new DirectoryObjectStorageTarget(Path.Combine(
                path1: output,
                path2: "store"
            ));
            var authority = new WorldAuthorityBlobStore(
                store: blobs,
                target: local
            );

            foreach (var row in silo.Worlds) {
                Assert.True(condition: row.Pinned);
                Assert.Null(@object: row.Federation.Authentication);
                Assert.StartsWith(
                    output.Replace(
                        newChar: '/',
                        oldChar: '\\'
                    ),
                    row.Federation.KeyFile
                );
                Assert.Null(value: await authority.LoadRootAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                ));
                Assert.Equal(
                    bytes,
                    (await blobs.ReadAsync(
                        local,
                        WorldOwnedWorldSync.HostedAddressFor(
                            owner,
                            row.World,
                            "definition.json"
                        ),
                        token
                    ))!.Value.Content.ToArray()
                );
            }
            await Assert.ThrowsAsync<IOException>(() => builder.BuildAsync(
                release,
                owner,
                null,
                output,
                token
            ));
            var groups = new WorldReleaseGroupStore(
                owner: owner,
                store: blobs,
                target: local
            );

            Assert.True(condition: (await groups.CreateAsync(
                "official",
                release.Identity,
                token
            )).Ok);
            using var console = new BufferedConsoleOutput();
            var commands = new TextCommandSource(new CommandRegistry(modules: []));
            var routing = new SiloConsoleRouting(
                source: () => commands,
                tagging: new SiloConsoleTagging(output: console)
            );
            var host = new WorldSiloHost(
                silo with { Release = new(
                    "official",
                    owner,
                    release.Identity
                ) },
                blobs,
                routing,
                local
            );
            using var instances = host.Instances;

            foreach (var row in silo.Worlds) {
                var activating = host.ActivateAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                );

                await PumpAsync(
                    host: host,
                    operation: activating,
                    token: token
                );
                Assert.True(condition: await activating);
            }
            var publishing = host.PublishManagedReleaseAdmissionAsync(token);

            await PumpAsync(
                host: host,
                operation: publishing,
                token: token
            );
            Assert.Equal(
                WorldReleaseAdmissionPublication.Opened,
                await publishing
            );
            var expectedReceipts = new Dictionary<string, WorldAuthorityOperationReceipt>();
            var laterReceipts = new Dictionary<string, WorldAuthorityOperationReceipt>();

            foreach (var row in silo.Worlds) {
                var root = (await authority.LoadRootAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                ))!.Value;
                var receipt = new WorldAuthorityOperationReceipt(
                    Guid.NewGuid(),
                    "fixture",
                    "original",
                    "refused",
                    false,
                    1,
                    null
                );

                expectedReceipts.Add(
                    key: row.World.Value,
                    value: receipt
                );
                Assert.True(condition: (await authority.RecordReceiptAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    receipt,
                    token,
                    new(
                        root.Root.Epoch,
                        root.Root.FenceToken,
                        root.VersionToken
                    )
                )).Ok);
            }
            var capturing = host.ExportReleaseFixtureAsync(
                Guid.NewGuid(),
                token
            );

            await PumpAsync(
                host: host,
                operation: capturing,
                token: token
            );
            var capture = await capturing;

            foreach (var row in silo.Worlds) {
                var root = (await authority.LoadRootAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                ))!.Value;
                var receipt = new WorldAuthorityOperationReceipt(
                    Guid.NewGuid(),
                    "fixture",
                    "later",
                    "refused",
                    false,
                    2,
                    null
                );

                laterReceipts.Add(
                    key: row.World.Value,
                    value: receipt
                );
                Assert.True(condition: (await authority.RecordReceiptAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    receipt,
                    token,
                    new(
                        root.Root.Epoch,
                        root.Root.FenceToken,
                        root.VersionToken
                    )
                )).Ok);
            }
            var capturedArchive = new WorldReleaseFixtureArchive(
                owner: owner,
                store: blobs,
                target: local
            );
            var complete = Path.Combine(
                path1: temporary.FullName,
                path2: "captured"
            );

            await new WorldReleaseFixtureBuilder(
                blobs,
                archive,
                capturedArchive
            ).BuildAsync(
                release,
                owner,
                capture,
                complete,
                token
            );
            var restored = new WorldAuthorityBlobStore(
                store: blobs,
                target: new DirectoryObjectStorageTarget(Path.Combine(
                    path1: complete,
                    path2: "store"
                ))
            );

            foreach (var row in silo.Worlds) {
                var saved = await restored.LoadRecoveryAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                );

                Assert.NotNull(value: saved);
                Assert.Equal(
                    (await capturedArchive.ReadCheckpointAsync(
                        capture,
                        row.World.Value,
                        token
                    )).ToArray(),
                    saved.Value.Checkpoint!.Value.Encoded.ToArray()
                );
                Assert.Equal(
                    expectedReceipts[row.World.Value],
                    await restored.FindOperationReceiptAsync(
                        new(
                            Owner: owner,
                            World: row.World
                        ),
                        expectedReceipts[row.World.Value].OperationId,
                        token
                    )
                );
                Assert.Null(value: await restored.FindOperationReceiptAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    laterReceipts[row.World.Value].OperationId,
                    token
                ));
                var history = await capturedArchive.ReadReceiptsAsync(
                    capture,
                    row.World.Value,
                    token
                );

                Assert.Equal(
                    history.Source.Root.ReceiptHash,
                    saved.Value.Root.Root.ReceiptHash
                );
                Assert.Equal(
                    history.Source.Root.ReceiptIndexHash,
                    saved.Value.Root.Root.ReceiptIndexHash
                );
            }
            Assert.Equal(
                capture.MachineId.ToString(format: "D"),
                File.ReadAllText(path: Path.Combine(
                    path1: complete,
                    path2: "state",
                    path3: "silo-machine.id"
                ))
            );
            var candidateDirectory = Directory.CreateDirectory(path: Path.Combine(
                path1: temporary.FullName,
                path2: "candidate"
            ));
            var candidateDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: bytes) with { Metadata = new(Title: "Release B") };
            var candidateBytes = WorldDefinitionSerialization.Serialize(definition: candidateDefinition);

            File.WriteAllBytes(
                Path.Combine(
                    path1: candidateDirectory.FullName,
                    path2: "world.json"
                ),
                candidateBytes
            );
            var candidate = release with {
                Label = "metadata candidate",
                CoordinatorContract = WorldReleaseManifest.MetadataCoordinatorContract,
                Definitions = release.Definitions.ToDictionary(
                row => row.Key,
                _ => ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: candidateBytes)))
            ),
            };

            await archive.SaveAsync(
                candidate,
                candidateDirectory.FullName,
                token
            );
            var metadataCopy = Path.Combine(
                path1: temporary.FullName,
                path2: "metadata-copy"
            );

            await new WorldReleaseFixtureBuilder(
                blobs,
                archive,
                capturedArchive
            ).BuildAsync(
                release,
                owner,
                capture,
                metadataCopy,
                token
            );
            var transition = new WorldReleaseQualificationTransition(
                blobs,
                archive
            );

            await transition.ApplyAsync(
                metadataCopy,
                silo,
                release,
                candidate,
                token
            );
            var transformed = new WorldAuthorityBlobStore(
                store: blobs,
                target: new DirectoryObjectStorageTarget(Path.Combine(
                    path1: metadataCopy,
                    path2: "store"
                ))
            );

            foreach (var row in silo.Worlds) {
                var identity = new WorldAuthorityIdentity(
                    Owner: owner,
                    World: row.World
                );

                Assert.Equal(
                    candidateBytes,
                    WorldDefinitionSerialization.Serialize(definition: (await transformed.LoadDefinitionAsync(
                        cancellationToken: token,
                        identity: identity
                    ))!)
                );
                var saved = (await transformed.LoadRecoveryAsync(
                    cancellationToken: token,
                    identity: identity
                ))!.Value;

                Assert.True(
                    condition: WorldAuthorityCheckpointCodec.TryDecode(
                        bytes: saved.Checkpoint!.Value.Encoded.Span,
                        checkpoint: out var checkpoint,
                        reason: out reason
                    ),
                    userMessage: reason
                );
                Assert.Equal(
                    "Release B",
                    WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson).Metadata!.Title
                );
                Assert.Equal(
                    capture.Worlds[row.World.Value].Tick,
                    checkpoint.Server.LastCompletedTick
                );
            }
            await transition.ApplyAsync(
                metadataCopy,
                silo,
                candidate,
                release,
                token
            );
            foreach (var row in silo.Worlds) {
                var saved = (await transformed.LoadRecoveryAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                ))!.Value;

                Assert.Equal(
                    (await capturedArchive.ReadCheckpointAsync(
                        capture,
                        row.World.Value,
                        token
                    )).ToArray(),
                    saved.Checkpoint!.Value.Encoded.ToArray()
                );
                Assert.Equal(
                    bytes,
                    WorldDefinitionSerialization.Serialize(definition: (await transformed.LoadDefinitionAsync(
                        new(
                            Owner: owner,
                            World: row.World
                        ),
                        token
                    ))!)
                );
            }
            var missingPackages = new WorldReleaseQualificationRunner(
                complete,
                Path.Combine(
                    path1: temporary.FullName,
                    path2: "missing-packages"
                ),
                "missing",
                "missing"
            );
            var missing = await Assert.ThrowsAsync<InvalidDataException>(() => missingPackages.RunAsync(
                release,
                candidate,
                token
            ));

            Assert.Contains(
                "both verified release packages",
                missing.Message
            );
            var unsupportedDirectory = Directory.CreateDirectory(path: Path.Combine(
                path1: temporary.FullName,
                path2: "unsupported"
            ));
            var unsupportedBytes = WorldDefinitionSerialization.Serialize(definition: candidateDefinition with {
                HostRaw = WorldHostDefaults.Absent with { Width = 320, Height = 200, TargetHertz = 30, Authority = "localhost:7825" },
            });

            File.WriteAllBytes(
                Path.Combine(
                    path1: unsupportedDirectory.FullName,
                    path2: "world.json"
                ),
                unsupportedBytes
            );
            var unsupported = candidate with {
                Label = "unsupported simulation edit",
                Definitions = release.Definitions.ToDictionary(
                row => row.Key,
                _ => ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: unsupportedBytes)))
            ),
            };

            await archive.SaveAsync(
                unsupported,
                unsupportedDirectory.FullName,
                token
            );
            var refusedEvidence = Path.Combine(
                path1: temporary.FullName,
                path2: "refused-evidence"
            );
            var refusedRunner = new WorldReleaseQualificationRunner(
                complete,
                refusedEvidence,
                "missing",
                "missing",
                archive: archive
            );
            var unsupportedError = await Assert.ThrowsAsync<InvalidDataException>(() => refusedRunner.RunAsync(
                release,
                unsupported,
                token
            ));

            Assert.Contains(
                "outside metadata",
                unsupportedError.Message
            );
            Assert.Empty(collection: Directory.EnumerateFiles(
                path: refusedEvidence,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "receipt.json"
            ));
            if (image is not null) {
                var evidence = (Environment.GetEnvironmentVariable(variable: "PUCK_TEST_RELEASE_EVIDENCE_DIRECTORY") ?? Path.Combine(
                    path1: temporary.FullName,
                    path2: "evidence"
                ));
                var runner = new WorldReleaseQualificationRunner(
                    complete,
                    Path.GetFullPath(path: evidence),
                    image,
                    image,
                    4
                );

                Assert.NotNull(await runner.RunAsync(
                    release,
                    release with { Label = "candidate" },
                    token
                ));
                var sourceManifestPath = Path.Combine(
                    path1: package.FullName,
                    path2: "release.json"
                );
                var targetManifestPath = Path.Combine(
                    path1: candidateDirectory.FullName,
                    path2: "release.json"
                );

                File.WriteAllBytes(
                    sourceManifestPath,
                    WorldReleaseManifest.Canonicalize(manifest: release)
                );
                File.WriteAllBytes(
                    targetManifestPath,
                    WorldReleaseManifest.Canonicalize(manifest: candidate)
                );
                // Exercise the public qualify command's package retention as well as its four packaged legs.
                var command = WorldReleaseCommand.Create();

                Assert.Equal(
                    0,
                    await command.Parse(["qualify", sourceManifestPath, targetManifestPath, complete,
                    "--source-image", image, "--target-image", image, "--steps", "4", "--output", Path.GetFullPath(path: evidence)])
                    .InvokeAsync(cancellationToken: token)
                );
                var proofPath = Directory.EnumerateFiles(
                    path: evidence,
                    searchOption: SearchOption.AllDirectories,
                    searchPattern: "evidence.json"
                )
                    .Single(predicate: path => (JsonNode.Parse(File.ReadAllBytes(path: path))!["TargetRelease"]!.GetValue<string>() == candidate.Identity));
                var proof = JsonNode.Parse(File.ReadAllBytes(path: proofPath))!;

                Assert.True(condition: proof["MetadataTransition"]!.GetValue<bool>());
                foreach (var proofLeg in new[] { "SourceImport", "TargetImport", "ReverseImport", "ReverseReference" }) {
                    var result = proof[proofLeg]!;

                    Assert.Equal(
                        "puck.world.qualification-exercise.v2",
                        result["Schema"]!.GetValue<string>()
                    );
                    Assert.Equal(
                        result["ReceiptSeedHash"]!.GetValue<string>(),
                        result["ImportedReceiptHash"]!.GetValue<string>()
                    );
                    Assert.Equal(
                        result["ReceiptSeedHash"]!.GetValue<string>(),
                        result["ContinuedReceiptHash"]!.GetValue<string>()
                    );
                }
                Assert.NotEqual(
                    proof["SeedHash"]!.GetValue<string>(),
                    proof["ForwardSeedHash"]!.GetValue<string>()
                );
                foreach (var leg in new[] { "target-import", "source-reverse-import" }) {
                    var legStore = new WorldAuthorityBlobStore(
                        store: blobs,
                        target: new DirectoryObjectStorageTarget(Path.Combine(
                            path1: Path.GetDirectoryName(path: proofPath)!,
                            path2: leg,
                            path3: "store"
                        ))
                    );

                    foreach (var row in silo.Worlds) {
                        var saved = (await legStore.LoadRecoveryAsync(
                            new(
                                Owner: owner,
                                World: row.World
                            ),
                            token
                        ))!.Value;

                        Assert.True(
                            condition: WorldAuthorityCheckpointCodec.TryDecode(
                                bytes: saved.Checkpoint!.Value.Encoded.Span,
                                checkpoint: out var checkpoint,
                                reason: out reason
                            ),
                            userMessage: reason
                        );
                        var expectedTitle = ((leg == "target-import")
                            ? "Release B"
                            : null
                        );

                        Assert.Equal(
                            expectedTitle,
                            WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson).Metadata?.Title
                        );
                        Assert.Equal(
                            expectedTitle,
                            WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.BaseDefinitionJson).Metadata?.Title
                        );
                        Assert.Equal(
                            (capture.Worlds[row.World.Value].Tick + ((leg == "target-import")
                            ? 4UL
                            : 8UL)),
                            checkpoint.Server.LastCompletedTick
                        );
                        Assert.Equal(
                            expectedReceipts[row.World.Value],
                            await legStore.FindOperationReceiptAsync(
                                new(
                                    Owner: owner,
                                    World: row.World
                                ),
                                expectedReceipts[row.World.Value].OperationId,
                                token
                            )
                        );
                        Assert.Null(value: await legStore.FindOperationReceiptAsync(
                            new(
                                Owner: owner,
                                World: row.World
                            ),
                            laterReceipts[row.World.Value].OperationId,
                            token
                        ));
                    }
                }
                var previousImage = Environment.GetEnvironmentVariable(variable: "PUCK_TEST_PREVIOUS_WORLD_IMAGE");

                if (previousImage is not null) {
                    var previousDigest = JsonNode.Parse(await CliProcess.RunCheckedAsync(
                        Environment.CurrentDirectory,
                        "docker",
                        ["image", "inspect", previousImage],
                        capture: true,
                        cancellationToken: token
                    ))![0]!["Id"]!.GetValue<string>();

                    Assert.NotEqual(
                        actual: previousDigest,
                        expected: digest
                    );
                    var unsupportedEvidence = Path.Combine(
                        path1: temporary.FullName,
                        path2: "old-exercise-evidence"
                    );
                    var oldRunner = new WorldReleaseQualificationRunner(
                        complete,
                        unsupportedEvidence,
                        previousImage,
                        image,
                        4
                    );
                    var oldError = await Assert.ThrowsAsync<InvalidDataException>(() => oldRunner.RunAsync(
                        release with { EngineImageDigest = previousDigest },
                        release,
                        token
                    ));

                    Assert.Contains(
                        "receipt-aware exercise",
                        oldError.Message
                    );
                    Assert.Empty(collection: Directory.EnumerateFiles(
                        path: unsupportedEvidence,
                        searchOption: SearchOption.AllDirectories,
                        searchPattern: "receipt.json"
                    ));
                }
            }
            foreach (var row in silo.Worlds) {
                var saved = (await restored.LoadRecoveryAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                ))!.Value;

                Assert.Equal(
                    (await capturedArchive.ReadCheckpointAsync(
                        capture,
                        row.World.Value,
                        token
                    )).ToArray(),
                    saved.Checkpoint!.Value.Encoded.ToArray()
                );
                Assert.Equal(
                    bytes,
                    WorldDefinitionSerialization.Serialize(definition: (await restored.LoadDefinitionAsync(
                        new(
                            Owner: owner,
                            World: row.World
                        ),
                        token
                    ))!)
                );
            }
            await PumpAsync(
                host: host,
                operation: host.DrainAsync(ct: token),
                token: token
            );
            var legacyRows = new Dictionary<string, WorldReleaseFixtureCheckpoint>();

            foreach (var row in silo.Worlds) {
                legacyRows.Add(
                    key: row.World.Value,
                    value: new(
                        (await capturedArchive.ReadCheckpointAsync(
                            capture,
                            row.World.Value,
                            token
                        )).ToArray(),
                        capture.Worlds[row.World.Value].Tick
                    )
                );
            }
            var legacy = await snapshots.SaveAsync(
                Guid.NewGuid(),
                "official",
                release.Identity,
                capture.MachineId,
                legacyRows,
                token
            );
            var legacyOutput = Path.Combine(
                path1: temporary.FullName,
                path2: "legacy"
            );
            var legacyError = await Assert.ThrowsAsync<InvalidDataException>(() => builder.BuildAsync(
                release,
                owner,
                legacy,
                legacyOutput,
                token
            ));

            Assert.Contains(
                "receipt history proof",
                legacyError.Message
            );
            Assert.False(condition: File.Exists(path: Path.Combine(
                path1: legacyOutput,
                path2: "qualification.fixture"
            )));
            var invalid = await snapshots.SaveAsync(
                Guid.NewGuid(),
                "official",
                release.Identity,
                Guid.NewGuid(),
                new[] { "alpha", "beta" }.ToDictionary(
                    world => world,
                    _ => new WorldReleaseFixtureCheckpoint(
                        "invalid checkpoint"u8.ToArray(),
                        10
                    )
                ),
                token
            );
            var partial = Path.Combine(
                path1: temporary.FullName,
                path2: "partial"
            );

            await Assert.ThrowsAsync<InvalidDataException>(() => builder.BuildAsync(
                release,
                owner,
                invalid,
                partial,
                token
            ));
            Assert.False(condition: File.Exists(path: Path.Combine(
                path1: partial,
                path2: "qualification.fixture"
            )));
        } finally { temporary.Delete(recursive: true); }
    }
}
