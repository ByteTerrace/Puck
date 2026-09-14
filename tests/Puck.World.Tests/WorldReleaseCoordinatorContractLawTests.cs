using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseCoordinatorContractLawTests {
    private static WorldReleaseManifest Manifest(Guid owner) => new() {
        Label = "contract-test",
        SourceRevision = new string(
        c: 'a',
        count: 40
    ),
        EngineImageDigest = ("sha256:" + new string(
        c: 'b',
        count: 64
    )),
        PersistenceContract = "test",
        PeerProtocolContract = "test",
        Definitions = new Dictionary<string, string> { [$"{owner:D}/world"] = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: "world"u8))) },
        DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/world"] = "world.json" },
    };

    [InlineData("PUCK_TEST_PREVIOUS_WORLD_SERVER", null, false, "canonical")]
    [InlineData("PUCK_TEST_PREVIOUS_METADATA_SERVER", WorldReleaseManifest.MetadataCoordinatorContract, true, "coordinator contract")]
    [Theory]
    public async Task PreviousArchiveReaderAcceptsLegacyButRefusesCoordinatorRequirementWithoutWriting(string variable, string? priorContract, bool hasContractProperty, string refusal) {
        var path = Environment.GetEnvironmentVariable(variable: variable);

        Assert.SkipWhen(
            condition: string.IsNullOrWhiteSpace(value: path),
            reason: $"Set {variable} to the previous Puck.World.Server.dll to exercise its reader."
        );
        Assert.True(
            condition: File.Exists(path: path),
            userMessage: "The configured previous assembly is missing."
        );
        using var directory = new TempWorldDirectory();
        var blobs = new FakeObjectBlobStore();
        var storage = new DirectoryObjectStorageTarget(directory.RootPath);
        var owner = Guid.NewGuid();
        var legacy = Manifest(owner: owner) with { CoordinatorContract = priorContract };
        var current = legacy with { CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract };

        directory.WriteText(
            name: "package/world.json",
            text: "world"
        );
        var archive = new WorldReleaseArchive(
            blobs,
            storage,
            owner
        );
        var token = TestContext.Current.CancellationToken;

        await archive.SaveAsync(
            legacy,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            token
        );
        await archive.SaveAsync(
            current,
            Path.Combine(
                path1: directory.RootPath,
                path2: "package"
            ),
            token
        );
        Assert.Equal(
            current.Identity,
            (await archive.LoadAsync(
                current.Identity,
                token
            ))!.Identity
        );
        var writes = blobs.WriteCount;
        var context = new PreviousReaderContext();

        try {
            var assembly = context.LoadFromAssemblyPath(assemblyPath: Path.GetFullPath(path: path!));
            var manifestType = assembly.GetType(name: typeof(WorldReleaseManifest).FullName!)!;

            Assert.Equal(
                hasContractProperty,
                (manifestType.GetProperty(name: nameof(WorldReleaseManifest.CoordinatorContract)) is not null)
            );
            var archiveType = assembly.GetType(name: typeof(WorldReleaseArchive).FullName!)!;
            var oldArchive = Activator.CreateInstance(
                archiveType,
                blobs,
                storage,
                owner,
                ((64 * 1024) * 1024)
            )!;

            async Task<object?> LoadAsync(string identity) {
                var task = ((Task)archiveType.GetMethod(name: nameof(WorldReleaseArchive.LoadAsync))!.Invoke(
                    obj: oldArchive,
                    parameters: [identity, token]
                )!);

                await task;
                return task.GetType().GetProperty(name: "Result")!.GetValue(obj: task);
            }
            var oldLegacy = await LoadAsync(identity: legacy.Identity);

            Assert.NotNull(@object: oldLegacy);
            Assert.Equal(
                legacy.Identity,
                manifestType.GetProperty(name: nameof(WorldReleaseManifest.Identity))!.GetValue(obj: oldLegacy)
            );
            var error = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => LoadAsync(identity: current.Identity));

            Assert.Contains(
                refusal,
                error.Message
            );
            Assert.Equal(
                writes,
                blobs.WriteCount
            );
            TestContext.Current.TestOutputHelper?.WriteLine(message: ("Previous reader SHA256: " + Convert.ToHexStringLower(inArray: SHA256.HashData(source: File.ReadAllBytes(path: path!)))));
        } finally { context.Unload(); }
    }
    [Fact]
    public void RequirementChangesIdentityAndProtectsBothDirectionsWithoutRelabelingLegacyManifests() {
        var legacy = Manifest(owner: Guid.NewGuid());
        var current = legacy with { CoordinatorContract = WorldReleaseManifest.MetadataCoordinatorContract };

        Assert.NotEqual(
            legacy.Identity,
            current.Identity
        );
        Assert.DoesNotContain(
            "coordinatorContract",
            System.Text.Encoding.UTF8.GetString(bytes: WorldReleaseManifest.Canonicalize(manifest: legacy))
        );
        Assert.DoesNotContain(
            "coordinatorContract",
            JsonSerializer.Serialize(legacy)
        );
        Assert.Equal(
            legacy.Identity,
            JsonSerializer.Deserialize<WorldReleaseManifest>(WorldReleaseManifest.Canonicalize(manifest: legacy))!.Identity
        );
        Assert.True(condition: WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            reason: out _,
            source: legacy,
            target: current
        ));
        Assert.True(condition: WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            reason: out _,
            source: current,
            target: legacy
        ));
        var receipts = legacy with { CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract };

        Assert.NotEqual(
            current.Identity,
            receipts.Identity
        );
        Assert.True(condition: WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            reason: out _,
            source: receipts,
            target: legacy
        ));
        Assert.True(condition: WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            reason: out _,
            source: legacy,
            target: receipts
        ));
        Assert.False(condition: WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            legacy,
            legacy with { Label = "other" },
            out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "current tooling"
        );
        Assert.False(condition: WorldReleaseManifest.TryValidate(
            manifest: current with { CoordinatorContract = "unknown-future-contract" },
            reason: out reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "coordinator contract"
        );
        Assert.False(condition: WorldReleaseCompatibility.TryRequireMetadataCoordinator(
            current,
            current with { CoordinatorContract = "unknown" },
            out _
        ));
    }

    private sealed class PreviousReaderContext() : AssemblyLoadContext(isCollectible: true) {
        protected override Assembly? Load(AssemblyName name) => ((name.Name == "Puck.World.Server")
            ? null
            : Default.Assemblies.FirstOrDefault(predicate: assembly => AssemblyName.ReferenceMatchesDefinition(
                assembly.GetName(),
                name
            ))
        );
    }
}
