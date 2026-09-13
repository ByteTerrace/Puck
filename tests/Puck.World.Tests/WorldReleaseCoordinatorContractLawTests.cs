using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldReleaseCoordinatorContractLawTests {
    [Fact]
    public void RequirementChangesIdentityAndProtectsBothDirectionsWithoutRelabelingLegacyManifests() {
        var legacy = Manifest(Guid.NewGuid());
        var current = legacy with { CoordinatorContract = WorldReleaseManifest.MetadataCoordinatorContract };
        Assert.NotEqual(legacy.Identity, current.Identity);
        Assert.DoesNotContain("coordinatorContract", System.Text.Encoding.UTF8.GetString(WorldReleaseManifest.Canonicalize(legacy)));
        Assert.DoesNotContain("coordinatorContract", JsonSerializer.Serialize(legacy));
        Assert.Equal(legacy.Identity, JsonSerializer.Deserialize<WorldReleaseManifest>(WorldReleaseManifest.Canonicalize(legacy))!.Identity);
        Assert.True(WorldReleaseCompatibility.TryRequireMetadataCoordinator(legacy, current, out _));
        Assert.True(WorldReleaseCompatibility.TryRequireMetadataCoordinator(current, legacy, out _));
        Assert.False(WorldReleaseCompatibility.TryRequireMetadataCoordinator(legacy, legacy with { Label = "other" }, out var reason));
        Assert.Contains("current tooling", reason);
        Assert.False(WorldReleaseManifest.TryValidate(current with { CoordinatorContract = "unknown-future-contract" }, out reason));
        Assert.Contains("coordinator contract", reason);
        Assert.False(WorldReleaseCompatibility.TryRequireMetadataCoordinator(current, current with { CoordinatorContract = "unknown" }, out _));
    }

    [Fact]
    public async Task PreviousArchiveReaderAcceptsLegacyButRefusesCoordinatorRequirementWithoutWriting() {
        var path = Environment.GetEnvironmentVariable("PUCK_TEST_PREVIOUS_WORLD_SERVER");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(path), "Set PUCK_TEST_PREVIOUS_WORLD_SERVER to an older Puck.World.Server.dll to exercise the previous reader.");
        Assert.True(File.Exists(path), "The configured previous assembly is missing.");
        using var directory = new TempWorldDirectory();
        var blobs = new FakeObjectBlobStore();
        var storage = new DirectoryObjectStorageTarget(directory.RootPath);
        var owner = Guid.NewGuid();
        var legacy = Manifest(owner);
        var current = legacy with { CoordinatorContract = WorldReleaseManifest.MetadataCoordinatorContract };
        directory.WriteText("package/world.json", "world");
        var archive = new WorldReleaseArchive(blobs, storage, owner);
        var token = TestContext.Current.CancellationToken;
        await archive.SaveAsync(legacy, Path.Combine(directory.RootPath, "package"), token);
        await archive.SaveAsync(current, Path.Combine(directory.RootPath, "package"), token);
        Assert.Equal(current.Identity, (await archive.LoadAsync(current.Identity, token))!.Identity);
        var writes = blobs.WriteCount;
        var context = new PreviousReaderContext();
        try {
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path!));
            var manifestType = assembly.GetType(typeof(WorldReleaseManifest).FullName!)!;
            Assert.Null(manifestType.GetProperty(nameof(WorldReleaseManifest.CoordinatorContract)));
            var archiveType = assembly.GetType(typeof(WorldReleaseArchive).FullName!)!;
            var oldArchive = Activator.CreateInstance(archiveType, blobs, storage, owner, 64 * 1024 * 1024)!;
            async Task<object?> LoadAsync(string identity) {
                var task = (Task)archiveType.GetMethod(nameof(WorldReleaseArchive.LoadAsync))!.Invoke(oldArchive, [identity, token])!;
                await task;
                return task.GetType().GetProperty("Result")!.GetValue(task);
            }
            var oldLegacy = await LoadAsync(legacy.Identity);
            Assert.NotNull(oldLegacy);
            Assert.Equal(legacy.Identity, manifestType.GetProperty(nameof(WorldReleaseManifest.Identity))!.GetValue(oldLegacy));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(current.Identity));
            Assert.Contains("canonical", error.Message);
            Assert.Equal(writes, blobs.WriteCount);
            TestContext.Current.TestOutputHelper?.WriteLine("Previous reader SHA256: " + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path!))));
        } finally { context.Unload(); }
    }

    private static WorldReleaseManifest Manifest(Guid owner) => new() {
        Label = "contract-test", SourceRevision = new string('a', 40), EngineImageDigest = "sha256:" + new string('b', 64),
        PersistenceContract = "test", PeerProtocolContract = "test",
        Definitions = new Dictionary<string, string> { [$"{owner:D}/world"] = "sha256/" + Convert.ToHexStringLower(SHA256.HashData("world"u8)) },
        DefinitionFiles = new Dictionary<string, string> { [$"{owner:D}/world"] = "world.json" },
    };

    private sealed class PreviousReaderContext() : AssemblyLoadContext(isCollectible: true) {
        protected override Assembly? Load(AssemblyName name) => name.Name == "Puck.World.Server" ? null :
            Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name));
    }
}
