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
    private static Task PumpAsync(WorldSiloHost host, Task operation, CancellationToken token) => WorldSiloHost.PumpActivationMailboxesAsync(
        cancellationToken: token,
        hosts: [host],
        operation: operation
    );
    // The qualify verb's two failure codes over a real captured fixture, with scripted containers in place of Docker:
    // a pair that ran and failed a leg's claim exits 1, anything that stops the pair from running exits 2, and
    // neither leaves a receipt. The runner reports the same failures as a result rather than an exception.
    private static async Task QualifyVerdictsAsync(string fixture, WorldReleaseManifest release, WorldReleaseManifest unsupported, string temporary, CancellationToken token) {
        var manifests = Directory.CreateDirectory(path: Path.Combine(
            path1: temporary,
            path2: "qualify-manifests"
        )).FullName;
        var sourcePath = Path.Combine(
            path1: manifests,
            path2: "source.json"
        );
        var targetPath = Path.Combine(
            path1: manifests,
            path2: "target.json"
        );
        var unsupportedPath = Path.Combine(
            path1: manifests,
            path2: "unsupported.json"
        );
        var target = release with { Label = "qualify candidate" };

        File.WriteAllBytes(
            sourcePath,
            WorldReleaseManifest.Canonicalize(manifest: release)
        );
        File.WriteAllBytes(
            targetPath,
            WorldReleaseManifest.Canonicalize(manifest: target)
        );
        File.WriteAllBytes(
            unsupportedPath,
            WorldReleaseManifest.Canonicalize(manifest: unsupported)
        );

        async Task<(int ExitCode, string Error, string Evidence)> QualifyAsync(string name, string targetManifest, IWorldReleaseQualificationContainers containers) {
            var evidence = Path.Combine(
                path1: temporary,
                path2: ("qualify-" + name)
            );
            var command = WorldReleaseCommand.Create(
                clock: TimeProvider.System,
                containers: containers
            );

            CliExit.Guard(command: command);

            var (exitCode, _, error) = await ConsoleCapture.RunSplitAsync(run: () => command.Parse(["qualify", sourcePath, targetManifest, fixture, "--source-image", "source",
                "--target-image", "target", "--steps", "4", "--output", evidence]).InvokeAsync(cancellationToken: token));

            return (exitCode, error, evidence);
        }
        void AssertNoReceipt(string evidence) {
            if (Directory.Exists(path: evidence)) {
                Assert.Empty(collection: Directory.EnumerateFiles(
                    path: evidence,
                    searchOption: SearchOption.AllDirectories,
                    searchPattern: "receipt.json"
                ));
            }
        }

        var failedLeg = await QualifyAsync(
            containers: new ScriptedContainers(
                exercise: static _ => 1,
                imageId: release.EngineImageDigest
            ),
            name: "failed-leg",
            targetManifest: targetPath
        );

        Assert.Equal(
            actual: failedLeg.ExitCode,
            expected: CliExit.Failed
        );
        Assert.Contains(
            actualString: failedLeg.Error,
            expectedSubstring: "qualification leg 'source-import': the packaged engine exited with code 1"
        );
        AssertNoReceipt(evidence: failedLeg.Evidence);

        foreach (var (name, targetManifest, containers, expected) in new (string, string, IWorldReleaseQualificationContainers, string)[] {
            ("unsupported", unsupportedPath, new ScriptedContainers(exercise: static _ => 0, imageId: release.EngineImageDigest), "release definition"),
            ("foreign-image", targetPath, new ScriptedContainers(exercise: static _ => 0, imageId: ("sha256:" + new string(c: 'f', count: 64))), "immutable digest"),
            ("no-engine", targetPath, new ScriptedContainers(exercise: static _ => 0, imageId: null), "cannot inspect"),
        }) {
            var refused = await QualifyAsync(
                containers: containers,
                name: name,
                targetManifest: targetManifest
            );

            Assert.Equal(
                actual: refused.ExitCode,
                expected: CliExit.Refused
            );
            Assert.Contains(
                actualString: refused.Error,
                expectedSubstring: expected
            );
            AssertNoReceipt(evidence: refused.Evidence);
        }

        foreach (var (report, expected) in new (string?, string)[] {
            (null, "produced no readable state report"),
            ("not json", "produced no readable state report"),
            ((("{\"Schema\":\"" + WorldReleaseExerciseResult.CurrentSchema) + "\"}"), "does not prove receipt preservation"),
        }) {
            var result = await new WorldReleaseQualificationRunner(
                containers: new ScriptedContainers(
                    exercise: directory => {
                        if (report is not null) {
                            File.WriteAllText(
                                contents: report,
                                path: Path.Combine(
                                    path1: directory,
                                    path2: "exercise-result.json"
                                )
                            );
                        }

                        return 0;
                    },
                    imageId: release.EngineImageDigest
                ),
                fixture: fixture,
                outputDirectory: Path.Combine(
                    path1: temporary,
                    path2: "qualify-reports"
                ),
                sourceImage: "source",
                steps: 4,
                targetImage: "target"
            ).RunAsync(
                cancellationToken: token,
                source: release,
                target: target
            );

            Assert.Null(@object: result.Receipt);
            Assert.Contains(
                actualString: result.Failure,
                expectedSubstring: expected
            );
        }
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
            var imageId = await CandidateWorldImage.TryResolveIdAsync(cancellationToken: token);
            var image = ((imageId is null)
                ? null
                : CandidateWorldImage.Tag
            );
            var digest = (imageId ?? ("sha256:" + new string(
                c: 'a',
                count: 64
            )));
            var release = new WorldReleaseManifest {
                CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
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
                blobs: blobs,
                fixtures: snapshots,
                releases: archive
            );

            await builder.BuildAsync(
                directory: output,
                owner: owner,
                release: release,
                snapshot: null,
                token: token
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
                var seeded = (await authority.LoadRootAsync(
                    new(
                        Owner: owner,
                        World: row.World
                    ),
                    token
                ))!.Value.Root;

                Assert.Equal(
                    0,
                    seeded.Epoch
                );
                Assert.Equal(
                    Guid.Empty,
                    seeded.FenceToken
                );
                Assert.Null(@object: seeded.CheckpointHash);
                Assert.Null(@object: seeded.ReceiptHash);
                Assert.Equal(
                    bytes,
                    (await authority.LoadPublishedDefinitionBytesAsync(
                        new(
                            Owner: owner,
                            World: row.World
                        ),
                        token
                    ))!.Value.ToArray()
                );
            }
            await Assert.ThrowsAsync<IOException>(testCode: () => builder.BuildAsync(
                directory: output,
                owner: owner,
                release: release,
                snapshot: null,
                token: token
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
                silo with {
                    Release = new(
                    "official",
                    owner,
                    release.Identity
                ),
                },
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
                blobs: blobs,
                fixtures: capturedArchive,
                releases: archive
            ).BuildAsync(
                directory: complete,
                owner: owner,
                release: release,
                snapshot: capture,
                token: token
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
                blobs: blobs,
                fixtures: capturedArchive,
                releases: archive
            ).BuildAsync(
                directory: metadataCopy,
                owner: owner,
                release: release,
                snapshot: capture,
                token: token
            );
            var transition = new WorldReleaseQualificationTransition(
                archive: archive,
                blobs: blobs
            );

            await transition.ApplyAsync(
                definition: silo,
                directory: metadataCopy,
                source: release,
                target: candidate,
                token: token
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
                definition: silo,
                directory: metadataCopy,
                source: candidate,
                target: release,
                token: token
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
            var missing = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => missingPackages.RunAsync(
                cancellationToken: token,
                source: release,
                target: candidate
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
            var unsupportedError = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => refusedRunner.RunAsync(
                cancellationToken: token,
                source: release,
                target: unsupported
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
            await QualifyVerdictsAsync(
                fixture: complete,
                release: release,
                temporary: temporary.FullName,
                token: token,
                unsupported: unsupported
            );
            if (image is not null) {
                // Retained beside the test assembly for inspection, replaced by the next run's Docker legs.
                var evidence = Path.Combine(
                    path1: AppContext.BaseDirectory,
                    path2: "world-release-evidence"
                );

                if (Directory.Exists(path: evidence)) {
                    Directory.Delete(
                        path: evidence,
                        recursive: true
                    );
                }

                var runner = new WorldReleaseQualificationRunner(
                    complete,
                    Path.GetFullPath(path: evidence),
                    image,
                    image,
                    4
                );

                Assert.NotNull(@object: (await runner.RunAsync(
                    release,
                    release with { Label = "candidate" },
                    token
                )).Receipt);
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
                var command = WorldReleaseCommand.Create(clock: TimeProvider.System);

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
                        WorldReleaseExerciseResult.CurrentSchema,
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
            var receipts = new Dictionary<string, WorldAuthorityReceiptSnapshot>(comparer: StringComparer.Ordinal);

            foreach (var row in silo.Worlds) {
                receipts.Add(
                    key: row.World.Value,
                    value: await capturedArchive.ReadReceiptsAsync(
                        capture,
                        row.World.Value,
                        token
                    )
                );
            }
            var invalid = await snapshots.SaveAsync(
                Guid.NewGuid(),
                "official",
                release.Identity,
                Guid.NewGuid(),
                new[] { "alpha", "beta" }.ToDictionary(
                    world => world,
                    world => new WorldReleaseFixtureCheckpoint(
                        "invalid checkpoint"u8.ToArray(),
                        10,
                        receipts[world]
                    )
                ),
                token
            );
            var partial = Path.Combine(
                path1: temporary.FullName,
                path2: "partial"
            );

            await Assert.ThrowsAsync<InvalidDataException>(testCode: () => builder.BuildAsync(
                directory: partial,
                owner: owner,
                release: release,
                snapshot: invalid,
                token: token
            ));
            Assert.False(condition: File.Exists(path: Path.Combine(
                path1: partial,
                path2: "qualification.fixture"
            )));
        } finally { temporary.Delete(recursive: true); }
    }

    // A container engine that never starts Docker: every image resolves to imageId, or the engine cannot inspect it at
    // all when imageId is null, and each exercise answers with the exit code exercise returns for its fixture.
    private sealed class ScriptedContainers(Func<string, int> exercise, string? imageId) : IWorldReleaseQualificationContainers {
        public Task<int> ExerciseAsync(string container, string image, string fixture, int steps, CancellationToken cancellationToken) =>
            Task.FromResult(result: exercise(arg: fixture));
        public Task<WorldReleaseQualificationImage> InspectAsync(string reference, CancellationToken cancellationToken) =>
            ((imageId is null)
                ? throw new InvalidOperationException(message: "the container engine cannot inspect images")
                : Task.FromResult(result: new WorldReleaseQualificationImage(
                    Id: imageId,
                    RepoDigests: []
                )));
        public Task RemoveAsync(string container) => Task.CompletedTask;
    }
}
