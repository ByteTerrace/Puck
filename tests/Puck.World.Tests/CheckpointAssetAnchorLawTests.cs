using System.Text;
using Puck.Abstractions;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

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

        var machines = Fixtures.MachineHostFactory(template.Screens, [], null, null);
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
            action: static document => Assert.Equal(expected: (WorldCheckpointAnchor.World, string.Empty), actual: (document.Anchor, document.RelativeDirectory))
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
            action: static document => Assert.Equal(expected: (WorldCheckpointAnchor.Catalog, string.Empty), actual: (document.Anchor, document.RelativeDirectory))
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
    // file's own — so the bytes name neither where it was loaded from nor the catalog, and a restore under a world
    // with no directory refuses by name.
    [Fact]
    public void AJournalBaseLoadedFromAFileIsNamedRelativeToItsWorld() {
        using var loaded = new TemporaryDirectory(prefix: "puck-anchor-loaded-");
        using var boot = new TemporaryDirectory(prefix: "puck-anchor-boot-");
        using var catalog = new TemporaryDirectory(prefix: "puck-anchor-catalog-");
        var path = loaded.WriteBytes(name: "world.json", bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()));
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: boot.RootPath));

        fixture.Server.EnqueueRebuild(
            principal: Puck.Commands.Principal.Console,
            request: new WorldRebuildRequest(Kind: WorldRebuildKind.Load, Definition: null, PathHint: path, Force: true)
        );
        fixture.Step();

        var (bytes, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);

        Assert.Equal(expected: (new WorldBaseOrigin(Kind: WorldBaseOriginKind.Load, Path: "world.json"), WorldCheckpointAnchor.World), actual: (checkpoint.Server.BaseOrigin, checkpoint.Server.BaseOriginAnchor));
        AssertNamesNoPath(bytes: bytes, directory: loaded.RootPath);
        AssertNamesNoPath(bytes: bytes, directory: fixture.Server.Profiles.FilePath);

        var refused = Assert.Throws<InvalidOperationException>(testCode: () => Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: null)));

        Assert.Contains(expectedSubstring: "journal base names 'world.json'", actualString: refused.Message);
    }
    // A journal base whose origin is a hosted world's store identity names no file on any machine, so the checkpoint
    // keeps that name as written and a restore under a world with a directory restores it unchanged, resolved against
    // nothing.
    [Fact]
    public void AJournalBaseFromAHostedOriginKeepsItsStoreName() {
        using var boot = new TemporaryDirectory(prefix: "puck-anchor-boot-");
        using var catalog = new TemporaryDirectory(prefix: "puck-anchor-catalog-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: boot.RootPath));
        var hosted = (Fixtures.BuildDocument() with { DocumentDirectory = null });
        const string Name = "owner/00000000-0000-0000-0000-000000000001/hosted";

        fixture.Server.EnqueueRebuild(
            principal: Puck.Commands.Principal.Console,
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: hosted)),
                Definition: hosted,
                Force: true,
                Kind: WorldRebuildKind.Reload,
                PathHint: Name
            )
        );
        fixture.Step();

        var (bytes, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);

        Assert.Equal(expected: (new WorldBaseOrigin(Kind: WorldBaseOriginKind.Reload, Path: Name), WorldCheckpointAnchor.None), actual: (checkpoint.Server.BaseOrigin, checkpoint.Server.BaseOriginAnchor));

        var (server, machines) = Restore(bytes: bytes, catalog: catalog.RootPath, template: WorldAt(directory: boot.RootPath));

        using (machines) {
            Assert.Equal(expected: new WorldBaseOrigin(Kind: WorldBaseOriginKind.Reload, Path: Name), actual: server.Document.BaseOrigin);
        }
    }
    // The wire refuses a journal base origin that names a file absolutely or climbs out of its root.
    [Theory]
    [InlineData("../escape.json")]
    [InlineData("C:/rooted.json")]
    [InlineData("/rooted.json")]
    public void TheWireRefusesABaseOriginOutsideItsRoot(string relative) {
        using var world = new TemporaryDirectory(prefix: "puck-anchor-world-");
        using var fixture = Fixtures.FreshServer(definition: WorldAt(directory: world.RootPath));

        var (_, checkpoint) = CaptureWithOwnedWorld(fixture: fixture);
        var tampered = (checkpoint with {
            Server = (checkpoint.Server with {
                BaseOrigin = new WorldBaseOrigin(Kind: WorldBaseOriginKind.Load, Path: relative),
                BaseOriginAnchor = WorldCheckpointAnchor.World,
            }),
        });

        Assert.False(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: tampered),
            checkpoint: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "server base origin");
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
