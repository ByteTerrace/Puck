using System.Text;
using Puck.Abstractions;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// A checkpoint is durable and crosses machines, so it names an owned world's asset directory only relative to a root
/// the restoring host supplies — the owned-world catalog or the hosting world's directory — and never as a
/// machine-local path. A checkpoint written under one directory restores under another, and one whose directory does
/// not resolve where it is restored refuses by name before anything is restored.
/// </summary>
public sealed class CheckpointAssetAnchorLawTests {
    private static WorldDefinition WorldAt(string? directory) => (Fixtures.BuildDocument() with { DocumentDirectory = directory });
    private static (byte[] Bytes, WorldAuthorityCheckpoint Checkpoint) CaptureWithOwnedWorld(WorldFixture fixture) {
        Assert.NotNull(@object: fixture.Server.Profiles.Create(
            colorHex: "#336699",
            name: SafeName.Parse(candidate: "traveler"),
            reason: out var created
        ));
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: $"{created} {reason}"
        );

        return (WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!), checkpoint!);
    }
    private static void AssertNamesNoPath(byte[] bytes, string directory) {
        var text = Encoding.UTF8.GetString(bytes: bytes);
        var normalized = PuckPaths.Normalize(path: directory);

        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: normalized);
        Assert.DoesNotContain(expectedSubstring: normalized.Replace(newChar: '\\', oldChar: '/'), actualString: text, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    private static (WorldServer Server, IWorldMachineHost Machines) Restore(byte[] bytes, WorldDefinition template, string catalog) {
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: bytes, checkpoint: out var decoded, reason: out var reason),
            userMessage: reason
        );

        var machines = Fixtures.MachineHostFactory(template.Screens, [], null);
        var profiles = new WorldOwnedWorlds(
            directory: catalog,
            machineId: Guid.NewGuid(),
            template: template
        );

        try {
            return (WorldServer.FromCheckpoint(
                checkpoint: decoded!,
                documentDirectory: template.DocumentDirectory,
                instanceIdentity: "boot",
                machines: machines,
                profiles: profiles
            ).Server, machines);
        } catch {
            machines.Dispose();

            throw;
        }
    }

    // A seeded owned world resolves its assets beside the hosting world. Captured under one world directory and
    // restored under another, it resolves beside the second, and the bytes between name neither.
    [Fact]
    public void AnOwnedWorldAnchoredToItsHostingWorldRestoresUnderAnotherDirectory() {
        using var first = new TemporaryDirectory(prefix: "puck-anchor-world-a-");
        using var second = new TemporaryDirectory(prefix: "puck-anchor-world-b-");
        using var catalog = new TemporaryDirectory(prefix: "puck-anchor-catalog-b-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: first.RootPath));

        var (bytes, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);
        Assert.All(
            collection: checkpoint.OwnedWorlds.Documents,
            action: static document => Assert.Equal(expected: (WorldOwnedDocumentAnchor.World, string.Empty), actual: (document.Anchor, document.RelativeDirectory))
        );
        AssertNamesNoPath(bytes: bytes, directory: first.RootPath);
        AssertNamesNoPath(bytes: bytes, directory: fixture.Server.Profiles.FilePath);

        var (server, machines) = Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: second.RootPath));

        using (machines) {
            Assert.Equal(
                expected: PuckPaths.Normalize(path: second.RootPath),
                actual: PuckPaths.Normalize(path: server.Profiles.All.Single(predicate: static identity => (identity.Id == "traveler")).Document!.DocumentDirectory!)
            );
        }
    }
    // A loaded owned world resolves its assets beside the catalog that holds it; restored into another catalog, it
    // resolves beside that one.
    [Fact]
    public void AnOwnedWorldAnchoredToItsCatalogRestoresIntoAnotherCatalog() {
        using var world = new TemporaryDirectory(prefix: "puck-anchor-world-");
        using var first = new TemporaryDirectory(prefix: "puck-anchor-catalog-a-");
        using var second = new TemporaryDirectory(prefix: "puck-anchor-catalog-b-");
        var template = WorldAt(directory: world.RootPath);
        var writer = new WorldOwnedWorlds(directory: first.RootPath, machineId: Guid.NewGuid(), template: template);

        Assert.NotNull(@object: writer.Create(colorHex: "#663399", name: SafeName.Parse(candidate: "keeper"), reason: out _));

        // Loading the saved file makes the catalog its asset directory.
        var loaded = new WorldOwnedWorlds(directory: first.RootPath, machineId: Guid.NewGuid(), template: template);

        Assert.True(condition: loaded.TryCapture(checkpoint: out var captured, reason: out var reason), userMessage: reason);

        Assert.All(
            collection: captured!.Documents,
            action: static document => Assert.Equal(expected: (WorldOwnedDocumentAnchor.Catalog, string.Empty), actual: (document.Anchor, document.RelativeDirectory))
        );

        var restored = new WorldOwnedWorlds(directory: second.RootPath, machineId: Guid.NewGuid(), template: template);

        restored.Restore(checkpoint: captured);
        Assert.Equal(
            expected: PuckPaths.Normalize(path: second.RootPath),
            actual: PuckPaths.Normalize(path: restored.All.Single(predicate: static identity => (identity.Id == "keeper")).Document!.DocumentDirectory!)
        );
    }
    // A checkpoint whose owned world resolves beside a hosting world that has no directory, or one that does not
    // exist where it is restored, refuses by name, and the refusal comes before any of the checkpoint is restored.
    [Fact]
    public void AnOwnedWorldWhoseDirectoryDoesNotResolveRefusesByName() {
        using var world = new TemporaryDirectory(prefix: "puck-anchor-world-");
        using var catalog = new TemporaryDirectory(prefix: "puck-anchor-catalog-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: world.RootPath));

        var (bytes, _) = CaptureWithOwnedWorld(fixture: fixture);

        var withoutDirectory = Assert.Throws<InvalidOperationException>(testCode: () => Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: null)));

        Assert.Contains(expectedSubstring: "owned world #0 cannot be restored", actualString: withoutDirectory.Message);
        Assert.Contains(expectedSubstring: "the restoring world has none", actualString: withoutDirectory.Message);

        var missing = Path.Join(path1: world.RootPath, path2: "absent");
        var absent = Assert.Throws<InvalidOperationException>(testCode: () => Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: missing)));

        Assert.Contains(expectedSubstring: "does not exist here", actualString: absent.Message);
    }
    // A journal base loaded from a file names that file relative to the world's document directory — the loaded
    // file's own — so the bytes name neither where it was loaded from nor the catalog. A restore under another
    // directory names the file there, and one under a world with no directory refuses by name.
    [Fact]
    public void AJournalBaseLoadedFromAFileIsNamedRelativeToItsWorld() {
        using var loaded = new TemporaryDirectory(prefix: "puck-anchor-loaded-");
        using var boot = new TemporaryDirectory(prefix: "puck-anchor-boot-");
        using var elsewhere = new TemporaryDirectory(prefix: "puck-anchor-elsewhere-");
        using var catalog = new TemporaryDirectory(prefix: "puck-anchor-catalog-");
        var path = loaded.WriteBytes(name: "world.json", bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()));
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: boot.RootPath));

        LoadFile(fixture: fixture, path: path);

        var (bytes, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);

        Assert.Equal(expected: LoadedFrom(path: "world.json"), actual: checkpoint.Server.BaseOrigin);
        AssertNamesNoPath(bytes: bytes, directory: loaded.RootPath);
        AssertNamesNoPath(bytes: bytes, directory: fixture.Server.Profiles.FilePath);

        var (server, machines) = Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: elsewhere.RootPath));

        using (machines) {
            Assert.Equal(expected: LoadedFrom(path: PuckPaths.Normalize(path: Path.Join(path1: elsewhere.RootPath, path2: "world.json"))), actual: server.Document.BaseOrigin);
        }

        var refused = Assert.Throws<InvalidOperationException>(testCode: () => Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: null)));

        Assert.Contains(expectedSubstring: "journal base names 'world.json'", actualString: refused.Message);
    }
    // A world.load names a file however the builder spelled it. A relative spelling is still a file, anchored like any
    // other, never read as a store name because of its shape.
    [Fact]
    public void AWorldLoadWithARelativePathIsAnchoredNotKeptAsAName() {
        var relativeDirectory = $"puck-anchor-relative-{Guid.NewGuid():N}";
        using var boot = new TemporaryDirectory(prefix: "puck-anchor-boot-");

        _ = Directory.CreateDirectory(path: relativeDirectory);

        try {
            var relative = $"{relativeDirectory}/world.json";

            File.WriteAllBytes(bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()), path: relative);

            using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: boot.RootPath));

            LoadFile(fixture: fixture, path: relative);

            var (bytes, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);

            Assert.Equal(expected: LoadedFrom(path: "world.json"), actual: checkpoint.Server.BaseOrigin);
            AssertNamesNoPath(bytes: bytes, directory: Path.GetFullPath(path: relativeDirectory));
        } finally {
            Directory.Delete(path: relativeDirectory, recursive: true);
        }
    }
    // A journal base read from a hosted world's store names that store, which no machine keeps anywhere: the
    // checkpoint writes it as it is, and a restore under a world with a directory restores it unchanged.
    [Fact]
    public void AJournalBaseFromAHostedStoreKeepsItsStoreVerbatim() {
        using var boot = new TemporaryDirectory(prefix: "puck-anchor-boot-");
        using var catalog = new TemporaryDirectory(prefix: "puck-anchor-catalog-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: boot.RootPath));
        var hosted = (Fixtures.BuildDocument() with { DocumentDirectory = null });
        var store = new WorldRebuildOrigin.Store(Owner: Guid.Parse(input: "00000000-0000-0000-0000-000000000001"), World: SafeName.Parse(candidate: "hosted"));

        fixture.Server.EnqueueRebuild(
            principal: Puck.Commands.Principal.Console,
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: hosted)),
                Definition: hosted,
                Force: true,
                Kind: WorldRebuildKind.Reload,
                Origin: store
            )
        );
        fixture.Step();

        var (bytes, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);
        var expected = new WorldBaseOrigin(Kind: WorldBaseOriginKind.Reload, Source: store);

        Assert.Equal(expected: expected, actual: checkpoint.Server.BaseOrigin);

        var (server, machines) = Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: boot.RootPath));

        using (machines) {
            Assert.Equal(expected: expected, actual: server.Document.BaseOrigin);
        }
    }
    // Every case of a journal base's origin crosses the checkpoint wire as itself: none, a file named under its root,
    // and a store, verbatim.
    [Theory]
    [InlineData("none")]
    [InlineData("file")]
    [InlineData("store")]
    public void EveryBaseOriginCaseRoundTripsTheCheckpointWire(string @case) {
        using var world = new TemporaryDirectory(prefix: "puck-anchor-world-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: world.RootPath));
        var origin = BaseOriginCase(@case: @case);

        var (_, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: (checkpoint with { Server = (checkpoint.Server with { BaseOrigin = origin }) })),
            checkpoint: out var decoded,
            reason: out var reason
        ), userMessage: reason);
        Assert.Equal(expected: origin, actual: decoded!.Server.BaseOrigin);
    }
    // Both rebuild origin cases cross the submission wire as themselves, the store verbatim.
    [Theory]
    [InlineData("file")]
    [InlineData("store")]
    public void EveryRebuildOriginCaseRoundTripsTheSubmissionWire(string @case) {
        var definition = Fixtures.BuildDocument();
        var origin = BaseOriginCase(@case: @case).Source!;

        Assert.True(condition: WorldSubmissionCodec.TryEncodeRebuild(
            bytes: out var bytes,
            failure: out var encodeFailure,
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: definition)),
                Definition: definition,
                Force: false,
                Kind: WorldRebuildKind.Reload,
                Origin: origin
            )
        ), userMessage: encodeFailure.ToString());
        Assert.True(condition: WorldSubmissionCodec.TryDecodeRebuild(
            bytes: bytes,
            failure: out var decodeFailure,
            request: out var decoded
        ), userMessage: decodeFailure.ToString());
        Assert.Equal(expected: origin, actual: decoded!.Origin);
    }
    // The wire refuses a journal base file that is named absolutely or climbs out of its root.
    [Theory]
    [InlineData("../escape.json")]
    [InlineData("C:/rooted.json")]
    [InlineData("/rooted.json")]
    public void TheWireRefusesABaseOriginOutsideItsRoot(string relative) {
        using var world = new TemporaryDirectory(prefix: "puck-anchor-world-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: world.RootPath));

        var (_, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);
        var tampered = (checkpoint with { Server = (checkpoint.Server with { BaseOrigin = LoadedFrom(path: relative) }) });

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: tampered),
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "server base origin");
    }

    private static WorldBaseOrigin LoadedFrom(string path) => new(Kind: WorldBaseOriginKind.Load, Source: new WorldRebuildOrigin.File(Path: path));
    private static WorldBaseOrigin BaseOriginCase(string @case) => (@case switch {
        "file" => LoadedFrom(path: "nested/world.json"),
        "store" => new WorldBaseOrigin(Kind: WorldBaseOriginKind.Reload, Source: new WorldRebuildOrigin.Store(Owner: Guid.Parse(input: "00000000-0000-0000-0000-000000000002"), World: SafeName.Parse(candidate: "hosted"))),
        _ => WorldBaseOrigin.Boot,
    });
    private static void LoadFile(WorldFixture fixture, string path) {
        fixture.Server.EnqueueRebuild(
            principal: Puck.Commands.Principal.Console,
            request: new WorldRebuildRequest(Kind: WorldRebuildKind.Load, Definition: null, Origin: new WorldRebuildOrigin.File(Path: path), Force: true)
        );
        fixture.Step();
    }

    // The wire refuses a directory that climbs out of its root or names one absolutely, whatever wrote it.
    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/rooted")]
    [InlineData("/rooted")]
    [InlineData("nested/../escape")]
    public void TheWireRefusesADirectoryOutsideItsRoot(string relative) {
        using var world = new TemporaryDirectory(prefix: "puck-anchor-world-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: world.RootPath));

        var (_, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);
        var tampered = (checkpoint with {
            OwnedWorlds = (checkpoint.OwnedWorlds with {
                Documents = [.. checkpoint.OwnedWorlds.Documents.Select(selector: document => (document with { RelativeDirectory = relative }))],
            }),
        });

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: tampered),
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "owned world asset directory");
    }
}
