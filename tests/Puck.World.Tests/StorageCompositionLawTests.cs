using System.Text;
using System.Text.Json.Nodes;

using Xunit;

using Puck.Storage;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves the one composition path (<see cref="WorldDefinitionFileSource.TryComposeChain"/>) behaves
/// identically whether the chain is walked over a directory or over <see cref="WorldStorageDocumentSource"/>'s flat
/// cloud namespace, and that the owned-world sync engine (<see cref="WorldOwnedWorldSync"/>) composes on pull and
/// pushes a whole chain — never just its flattened tip.</summary>
public sealed class StorageCompositionLawTests {
    private static readonly Guid ContainerId = Guid.Parse(input: "11111111-2222-3333-4444-555555555555");
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");

    private static string BasisKey(string name) => WorldOwnedWorldSync.BasisAddressFor(
        containerId: ContainerId,
        name: name
    ).Key;
    /// <summary>Builds an owned-world catalog seeded with one flat "amber" identity, then reshapes its on-disk file
    /// into a delta over a hand-placed <c>basis/shared.world.json</c> — the shape the owner ruling settled: a basis
    /// file lives outside the catalog's own directory glob, so it never enumerates as a second owned world.</summary>
    private static (WorldOwnedWorlds Worlds, WorldIdentity Amber) BuildGraftedCatalog(TempWorldDirectory dir) {
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var amber = (worlds.FindById(id: "amber") ?? throw new InvalidOperationException(message: "seeding must produce 'amber'"));
        var tipPath = Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "amber"))
        );
        var flatBytes = File.ReadAllBytes(path: tipPath);
        var basisDirectory = Path.Combine(
            path1: worlds.FilePath,
            path2: "basis"
        );

        Directory.CreateDirectory(path: basisDirectory);
        File.WriteAllBytes(
            path: Path.Combine(
                path1: basisDirectory,
                path2: "shared.world.json"
            ),
            bytes: flatBytes
        );
        File.WriteAllText(
            contents: /*lang=json*/ """{ "basis": "basis/shared.world.json" }""",
            path: tipPath
        );

        return (worlds, amber);
    }
    private static WorldStorageDocumentSource Source(FakeObjectBlobStore store, CancellationToken cancellationToken) => new(
        cancellationToken: cancellationToken,
        containerId: ContainerId,
        store: store,
        target: Target
    );
    // The one writer-side key encoding (WorldOwnedWorldSync.AddressFor's own XML) — every test seeds and reads
    // through these rather than a hand-spelled literal, so a seed can never drift from the encoding it is proving.
    private static string TipKey(string id) => WorldOwnedWorldSync.AddressFor(
        containerId: ContainerId,
        id: WorldSafeName.Parse(candidate: id)
    ).Key;

    [Fact]
    public void Push_PublishesTheCounterpartClaim_NamingThisWorldUnderItsOwnerArmNeighbourKey() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var store = new FakeObjectBlobStore();
        var publisher = new FakeCounterpartPublisher(accepted: true);
        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            publisher: publisher,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );

        var outcomes = sync.Push(id: "amber");
        var tip = Assert.Single(collection: outcomes, predicate: outcome => (outcome.Id == "amber"));

        Assert.True(condition: tip.Ok, userMessage: tip.Detail);
        Assert.Contains(expectedSubstring: "counterpart claim posted", actualString: tip.Detail, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "counterpart claim posted", actualString: sync.LastClaimDetail, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: "amber", actual: publisher.LastWorldId);
        // The exact spelling ValidateAttestedCounterpart requires: a peer's owner-arm WorldReference.NeighbourKey.
        Assert.Contains(expectedSubstring: $"owner/{ContainerId:D}/amber", actualString: Encoding.UTF8.GetString(bytes: publisher.LastPayload!), comparisonType: StringComparison.Ordinal);
    }
    // The document write is the primary effect: a refused claim post is reported, never fatal to a landed push.
    [Fact]
    public void Push_StillSucceedsWhenTheCounterpartPublisherRefuses() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var store = new FakeObjectBlobStore();
        var publisher = new FakeCounterpartPublisher(accepted: false);
        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            publisher: publisher,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );

        var outcomes = sync.Push(id: "amber");
        var tip = Assert.Single(collection: outcomes, predicate: outcome => (outcome.Id == "amber"));

        Assert.True(condition: tip.Ok, userMessage: tip.Detail);
        Assert.Contains(expectedSubstring: "counterpart claim post refused — a distinctive fake refusal", actualString: tip.Detail, comparisonType: StringComparison.Ordinal);
    }

    private sealed class FakeCounterpartPublisher(bool accepted) : ICounterpartPublisher {
        public byte[]? LastPayload { get; private set; }
        public string? LastWorldId { get; private set; }

        public bool TryPublish(string worldId, ReadOnlyMemory<byte> payload, out string detail) {
            LastPayload = payload.ToArray();
            LastWorldId = worldId;
            detail = (accepted
                ? "accepted"
                : "a distinctive fake refusal");

            return accepted;
        }
    }

    [Fact]
    public void BasisBlobAlreadyInCloud_WithDifferentBytes_RefusesByName() {
        using var dir = new TempWorldDirectory();

        BuildGraftedCatalog(dir: dir);

        var reloaded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "schema": "not-the-same-document" }"""),
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );

        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: reloaded
        );

        var outcomes = sync.Push(id: "amber");
        var basisOutcome = outcomes.Single(predicate: outcome => (outcome.Id == "shared.world.json (basis)"));

        Assert.False(condition: basisOutcome.Ok);
        Assert.Contains(
            expectedSubstring: "different content",
            actualString: basisOutcome.Detail,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void BasisBlobAlreadyInCloud_WithIdenticalBytes_PushSucceeds() {
        using var dir = new TempWorldDirectory();

        BuildGraftedCatalog(dir: dir);

        var reloaded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var basisBytes = File.ReadAllBytes(path: Path.Combine(
            path1: reloaded.FilePath,
            path2: "basis",
            path3: "shared.world.json"
        ));
        var store = new FakeObjectBlobStore();

        // Seeded directly (never through this engine's own WriteAsync), simulating a basis blob a SIBLING catalog
        // already pushed this session before the sidecar here ever tracked a token for it.
        store.Seed(
            bytes: basisBytes,
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );

        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: reloaded
        );

        var outcomes = sync.Push(id: "amber");

        Assert.All(
            collection: outcomes,
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );
    }
    [Fact]
    public void ChainLinkOutsideTheReferrersDirectory_RefusesByName() {
        using var dir = new TempWorldDirectory();

        // The ancestor sits as a SIBLING of the delta, not inside its basis/ subdirectory.
        dir.WriteBytes(
            bytes: Fixtures.DefaultWorldBytes(),
            name: "sibling.world.json"
        );

        var deltaPath = dir.WriteText(
            name: "delta.world.json",
            text: /*lang=json*/ """{ "basis": "sibling.world.json", "motion": { "moveSpeed": 6.5 } }"""
        );

        Assert.False(condition: WorldDefinitionFileSource.TryResolveChainFiles(
            chain: out _,
            path: deltaPath,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not live directly under"
        );
    }
    [Fact]
    public void GraftedCatalog_ReloadsAndComposesTheHandPlacedBasis() {
        using var dir = new TempWorldDirectory();

        BuildGraftedCatalog(dir: dir);

        var reloaded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );

        Assert.Single(collection: reloaded.All);
        Assert.Equal(
            expected: "amber",
            actual: reloaded.All[0].Id
        );
    }
    [Fact]
    public void PullOfACloudDelta_WritesTheDeltaPlusItsLinks() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var basisBytes = File.ReadAllBytes(path: Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "amber"))
        ));
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: basisBytes,
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );
        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "shared.world.json", "motion": { "moveSpeed": 9.75 } }"""),
            key: TipKey(id: "amber"),
            objectId: ContainerId
        );

        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );

        Assert.All(
            collection: sync.Pull(id: "amber"),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        // A FRESH machine's first pull of a delta must write the link, not just a flattened probe — the link is
        // what SavePreservingBasis needs on every LATER save to keep writing a delta rather than a flat document.
        var linkPath = Path.Combine(
            path1: worlds.FilePath,
            path2: "basis",
            path3: "shared.world.json"
        );

        Assert.True(condition: File.Exists(path: linkPath));
        Assert.Equal(
            expected: basisBytes,
            actual: File.ReadAllBytes(path: linkPath)
        );

        var tipPath = Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "amber"))
        );
        var tipDocument = ((JsonObject)JsonNode.Parse(json: File.ReadAllText(path: tipPath))!);

        Assert.Equal(
            expected: "basis/shared.world.json",
            actual: tipDocument[propertyName: "basis"]!.GetValue<string>()
        );
    }
    [Fact]
    public void PullOfADelta_ComposesFromTheCloud_WithNoLocalSibling() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var basisBytes = File.ReadAllBytes(path: Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "amber"))
        ));
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: basisBytes,
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );
        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "shared.world.json", "motion": { "moveSpeed": 9.75 } }"""),
            key: TipKey(id: "amber"),
            objectId: ContainerId
        );

        // No local `basis/shared.world.json` sibling exists — proving the compose ran against the CLOUD, not a
        // coincidental local file of the same name.
        Assert.False(condition: File.Exists(path: Path.Combine(
            path1: worlds.FilePath,
            path2: "basis",
            path3: "shared.world.json"
        )));

        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );

        var outcomes = sync.Pull(id: "amber");

        Assert.All(
            collection: outcomes,
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        var pulled = (worlds.FindById(id: "amber") ?? throw new InvalidOperationException(message: "pull must adopt 'amber'"));

        Assert.Equal(
            expected: 9.75f,
            actual: pulled.Document!.Motion.MoveSpeed
        );
        // The turn rate is authored only by the basis — inheriting it (not defaulting or refusing) proves the
        // compose ran, not merely the flat probe write.
        Assert.Equal(
            expected: WorldDefinitionSerialization.Deserialize(utf8Json: basisBytes).Motion.TurnSpeed,
            actual: pulled.Document.Motion.TurnSpeed
        );
    }
    [Fact]
    public void PullOfADelta_PrefersTheCloudBasisOverADifferentLocalSiblingOfTheSameName() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var cloudBasisBytes = File.ReadAllBytes(path: Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "amber"))
        ));
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: cloudBasisBytes,
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );
        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "shared.world.json" }"""),
            key: TipKey(id: "amber"),
            objectId: ContainerId
        );

        // A DIFFERENT local file of the SAME name, sitting where the OLD coincidence-based resolution would have
        // looked: the local owned-worlds directory itself (not the basis/ subdirectory).
        File.WriteAllText(
            path: Path.Combine(
                path1: worlds.FilePath,
                path2: "shared.world.json"
            ),
            contents: /*lang=json*/ """{ "schema": "a local file the cloud compose must never touch" }"""
        );

        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );

        var outcomes = sync.Pull(id: "amber");

        Assert.All(
            collection: outcomes,
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        var pulled = (worlds.FindById(id: "amber") ?? throw new InvalidOperationException(message: "pull must adopt 'amber'"));

        Assert.Equal(
            expected: WorldDefinitionSerialization.Deserialize(utf8Json: cloudBasisBytes).Motion.MoveSpeed,
            actual: pulled.Document!.Motion.MoveSpeed
        );
    }
    [Fact]
    public void PushOfADelta_PushesItsBasisUnderTheBasisKey() {
        using var dir = new TempWorldDirectory();

        BuildGraftedCatalog(dir: dir);

        var reloaded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var store = new FakeObjectBlobStore();
        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: reloaded
        );

        var outcomes = sync.Push(id: "amber");

        Assert.All(
            collection: outcomes,
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );
        Assert.Contains(
            collection: outcomes,
            filter: outcome => (outcome.Id == "amber")
        );
        Assert.Contains(
            collection: outcomes,
            filter: outcome => (outcome.Id == "shared.world.json (basis)")
        );

        var tipBytes = store.TryGetBytes(
            key: TipKey(id: "amber"),
            objectId: ContainerId
        );
        var basisBytes = store.TryGetBytes(
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );

        Assert.NotNull(@object: tipBytes);
        Assert.NotNull(@object: basisBytes);
        Assert.Contains(
            expectedSubstring: "\"basis\"",
            actualString: Encoding.UTF8.GetString(bytes: tipBytes!),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void PushOfTwoIdentitiesSharingOneBasis_PushesTheBasisExactlyOnce() {
        using var dir = new TempWorldDirectory();

        BuildGraftedCatalog(dir: dir);

        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );

        Assert.NotNull(@object: worlds.Create(
            colorHex: "#3388CC",
            name: WorldSafeName.Parse(candidate: "topaz"),
            reason: out var createReason
        ));
        Assert.Equal(
            actual: createReason,
            expected: string.Empty
        );

        var topazPath = Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "topaz"))
        );

        File.WriteAllText(
            contents: /*lang=json*/ """
            {
              "basis": "basis/shared.world.json",
              "identity": { "id": "topaz", "name": "topaz", "color": "#3388CC", "moveSpeedState": "identity-move-speed", "turnSpeedState": "identity-turn-speed" }
            }
            """,
            path: topazPath
        );

        var reloaded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );

        Assert.Equal(
            expected: 2,
            actual: reloaded.All.Count
        );

        var store = new FakeObjectBlobStore();
        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: reloaded
        );

        var outcomes = sync.Push(id: null);

        Assert.All(
            collection: outcomes,
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );
        Assert.Single(
            collection: outcomes,
            predicate: outcome => (outcome.Id == "shared.world.json (basis)")
        );
        Assert.Equal(
            expected: 1,
            actual: store.WriteCountFor(
                key: BasisKey(name: "shared.world.json"),
                objectId: ContainerId
            )
        );
    }
    [Fact]
    public void PushPullPush_DoesNotFlattenTheCloudCopy() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var basisBytes = File.ReadAllBytes(path: Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "amber"))
        ));
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: basisBytes,
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );
        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "shared.world.json", "motion": { "moveSpeed": 9.75 } }"""),
            key: TipKey(id: "amber"),
            objectId: ContainerId
        );

        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );

        Assert.All(
            collection: sync.Pull(id: "amber"),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );
        Assert.All(
            collection: sync.Push(id: "amber"),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        // A fresh machine's pull-then-push must not replace the cloud's authored delta with a flattened document,
        // nor orphan the basis blob it names.
        var cloudTip = ((JsonObject)JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: store.TryGetBytes(
            key: TipKey(id: "amber"),
            objectId: ContainerId
        )!))!);

        Assert.Equal(
            expected: "shared.world.json",
            actual: cloudTip[propertyName: "basis"]!.GetValue<string>()
        );
        Assert.NotNull(@object: store.TryGetBytes(
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        ));
    }
    [Fact]
    public void PushedBasisBlob_IsNotDiscoveredAsAnOwnedWorld() {
        using var dir = new TempWorldDirectory();

        BuildGraftedCatalog(dir: dir);

        var reloaded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var store = new FakeObjectBlobStore();
        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: reloaded
        );

        Assert.All(
            collection: sync.Push(id: null),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        var pullOutcomes = sync.Pull(id: null);

        Assert.All(
            collection: pullOutcomes,
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: $"{outcome.Id}: {outcome.Detail}"
            )
        );
        Assert.DoesNotContain(
            collection: pullOutcomes,
            filter: outcome => (outcome.Id == "shared.world.json")
        );
        Assert.False(condition: sync.Dirty);
    }
    [Fact]
    public void SingleIdPush_DoesNotDirtyTheCatalog() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var store = new FakeObjectBlobStore();
        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );

        Assert.All(
            collection: sync.Push(id: null),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );
        Assert.False(condition: sync.Dirty);

        // A single-id push publishes live state before resolving its chain (WorldOwnedWorldSync.PushOne), but the
        // identity has not actually changed since the whole-catalog push above — the catalog must not read as dirty
        // from a save that wrote nothing new.
        Assert.All(
            collection: sync.Push(id: "amber"),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );
        Assert.False(condition: sync.Dirty);
    }
    [Fact]
    public void StaleBasisToken_WithIdenticalBytes_PushReconciles() {
        using var dir = new TempWorldDirectory();

        BuildGraftedCatalog(dir: dir);

        var reloaded = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var basisBytes = File.ReadAllBytes(path: Path.Combine(
            path1: reloaded.FilePath,
            path2: "basis",
            path3: "shared.world.json"
        ));
        var store = new FakeObjectBlobStore();
        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: reloaded
        );

        Assert.All(
            collection: sync.Push(id: "amber"),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        // Another writer (a sibling catalog, or the platform) rewrote the SAME bytes under a NEW token — this
        // catalog's tracked token for the link is now stale even though the content it names has not diverged.
        store.Seed(
            bytes: basisBytes,
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId,
            token: "external-token"
        );

        Assert.All(
            collection: sync.Push(id: "amber"),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        // The reconciliation must have ADOPTED the current token — a third push against it must also succeed,
        // rather than refusing "the cloud copy moved since last sync" forever.
        Assert.All(
            collection: sync.Push(id: "amber"),
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );
    }
    [Fact]
    public void StorageBasisCycle_RefusesNamingBothBlobs() {
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "second.world.json" }"""),
            key: BasisKey(name: "first.world.json"),
            objectId: ContainerId
        );
        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "first.world.json" }"""),
            key: BasisKey(name: "second.world.json"),
            objectId: ContainerId
        );

        var rootBytes = Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "first.world.json" }""");

        Assert.False(condition: WorldDefinitionFileSource.TryComposeChain(
            chainBytes: out _,
            composed: out _,
            reason: out var reason,
            rootBytes: rootBytes,
            rootResolvedName: "root.world.json",
            source: Source(
                cancellationToken: TestContext.Current.CancellationToken,
                store: store
            )
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "cycle"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "first.world.json"
        );
    }
    [InlineData(" leading.world.json")]
    [InlineData("trailing.world.json ")]
    [InlineData("../escape.world.json")]
    [InlineData("sub/dir.world.json")]
    [InlineData("no-suffix.json")]
    [Theory]
    public void StorageBasisName_OutsideTheCanonicalShape_RefusesBeforeAnyRead(string name) {
        var store = new FakeObjectBlobStore();
        var source = Source(
            cancellationToken: TestContext.Current.CancellationToken,
            store: store
        );

        Assert.False(condition: source.TryRead(
            content: out _,
            name: name,
            reason: out var reason,
            referrerName: "root.world.json",
            resolvedName: out _
        ));
        Assert.NotEmpty(collection: reason);
        Assert.Equal(
            expected: 0,
            actual: store.ReadCount
        );
    }
    [Fact]
    public void StorageBasisName_WithDoubleDot_ButNoSlash_IsAccepted() {
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: Fixtures.DefaultWorldBytes(),
            key: BasisKey(name: "a..b.world.json"),
            objectId: ContainerId
        );

        var source = Source(
            cancellationToken: TestContext.Current.CancellationToken,
            store: store
        );

        Assert.True(
            condition: source.TryRead(
                content: out var content,
                name: "a..b.world.json",
                reason: out var reason,
                referrerName: "root.world.json",
                resolvedName: out var resolvedName
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: resolvedName,
            expected: BasisKey(name: "a..b.world.json")
        );
        Assert.NotNull(@object: content);
        Assert.Equal(
            expected: 1,
            actual: store.ReadCount
        );
    }
    [Fact]
    public void StorageBasisNamedLikeTheRootTip_IsNotACycle() {
        using var dir = new TempWorldDirectory();
        var worlds = new WorldOwnedWorlds(
            directory: dir.RootPath,
            machineId: Guid.NewGuid(),
            template: Fixtures.BuildDocument()
        );
        var basisBytes = File.ReadAllBytes(path: Path.Combine(
            path1: worlds.FilePath,
            path2: WorldOwnedWorldFileName.For(id: WorldSafeName.Parse(candidate: "amber"))
        ));
        var store = new FakeObjectBlobStore();

        // A basis link sharing the TIP's own bare file name — a DISTINCT blob under the basis/ namespace, never the
        // tip itself.
        store.Seed(
            bytes: basisBytes,
            key: BasisKey(name: "amber.world.json"),
            objectId: ContainerId
        );
        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "amber.world.json", "motion": { "moveSpeed": 9.75 } }"""),
            key: TipKey(id: "amber"),
            objectId: ContainerId
        );

        var sync = new WorldOwnedWorldSync(
            containerId: ContainerId,
            stateFilePath: Path.Combine(
                path1: dir.RootPath,
                path2: "sync-state.json"
            ),
            store: store,
            target: Target,
            worlds: worlds
        );
        var outcomes = sync.Pull(id: "amber");

        Assert.All(
            collection: outcomes,
            action: outcome => Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            )
        );

        var pulled = (worlds.FindById(id: "amber") ?? throw new InvalidOperationException(message: "pull must adopt 'amber'"));

        Assert.Equal(
            expected: 9.75f,
            actual: pulled.Document!.Motion.MoveSpeed
        );
    }
    [Fact]
    public void StorageChain_EqualsFileChain_ByteForByte_AndHashForHash() {
        using var files = new TempWorldDirectory();

        var basisBytes = Fixtures.DefaultWorldBytes();

        files.WriteBytes(
            bytes: basisBytes,
            name: "shared.world.json"
        );

        var deltaText = /*lang=json*/ """{ "basis": "shared.world.json", "motion": { "moveSpeed": 6.5 } }""";
        var deltaPath = files.WriteText(
            name: "delta.world.json",
            text: deltaText
        );
        var deltaBytes = Encoding.UTF8.GetBytes(s: deltaText);

        Assert.True(
            condition: WorldDefinitionFileSource.TryLoad(
                path: deltaPath,
                definition: out var fileComposed,
                contentHash: out var fileHash,
                reason: out var fileReason
            ),
            userMessage: fileReason
        );

        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: basisBytes,
            key: BasisKey(name: "shared.world.json"),
            objectId: ContainerId
        );

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeChain(
                chainBytes: out var chainBytes,
                composed: out var composed,
                reason: out var composeReason,
                rootBytes: deltaBytes,
                rootResolvedName: "delta.world.json",
                source: Source(
                    cancellationToken: TestContext.Current.CancellationToken,
                    store: store
                )
            ),
            userMessage: composeReason
        );

        Assert.NotNull(@object: composed);
        Assert.Equal(
            expected: 2,
            actual: chainBytes.Count
        );
        Assert.Equal(
            expected: deltaBytes,
            actual: chainBytes[0]
        );
        Assert.Equal(
            expected: basisBytes,
            actual: chainBytes[1]
        );

        var storageHash = WorldDefinitionFileSource.ComputeChainContentHash(chain: chainBytes);

        Assert.Equal(
            actual: storageHash,
            expected: fileHash
        );

        Assert.True(
            condition: WorldJsonPayload.TryParse(
                json: composed!.ToJsonString(),
                info: WorldJsonContext.Default.WorldDefinition,
                value: out var storageDefinition,
                error: out var parseError
            ),
            userMessage: parseError
        );
        Assert.Equal(
            expected: WorldDefinitionSerialization.Serialize(definition: fileComposed!),
            actual: WorldDefinitionSerialization.Serialize(definition: WorldDefinitionMigrations.Apply(definition: storageDefinition))
        );

        // Mutation falsifier: folding the SAME two files in the opposite order must not produce the same pin —
        // proves the fold is order-sensitive, not merely present.
        var reversedHash = WorldDefinitionFileSource.ComputeChainContentHash(chain: [.. chainBytes.Reverse()]);

        Assert.NotEqual(
            actual: reversedHash,
            expected: storageHash
        );
    }
    [Fact]
    public void StorageChain_ObservesOneDeadlineForTheWholeChain() {
        var store = new FakeObjectBlobStore();

        store.Seed(
            bytes: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "deep.world.json" }"""),
            key: BasisKey(name: "mid.world.json"),
            objectId: ContainerId
        );
        store.Seed(
            bytes: Fixtures.DefaultWorldBytes(),
            key: BasisKey(name: "deep.world.json"),
            objectId: ContainerId
        );
        // No read delay is seeded on hop 1, so this proves the SAME caller-supplied deadline (already ticking since
        // before hop 1) reaches hop 2's read — token propagation across the whole chain, not a per-hop-fresh budget
        // of equal size. The 50ms deadline set below is what actually expires during hop 2's 2s delay.
        store.SeedReadDelay(
            delay: TimeSpan.FromSeconds(seconds: 2),
            key: BasisKey(name: "deep.world.json"),
            objectId: ContainerId
        );

        using var deadline = new CancellationTokenSource(delay: TimeSpan.FromMilliseconds(value: 50));
        var rootBytes = Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "mid.world.json" }""");

        Assert.False(condition: WorldDefinitionFileSource.TryComposeChain(
            chainBytes: out _,
            composed: out _,
            reason: out var reason,
            rootBytes: rootBytes,
            rootResolvedName: "root.world.json",
            source: Source(
                cancellationToken: deadline.Token,
                store: store
            )
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "timed out"
        );
    }
    [Fact]
    public void StorageMissingBasisBlob_RefusesNamingTheAddress() {
        var store = new FakeObjectBlobStore();
        var rootBytes = Encoding.UTF8.GetBytes(s: /*lang=json*/ """{ "basis": "ghost.world.json" }""");

        Assert.False(condition: WorldDefinitionFileSource.TryComposeChain(
            chainBytes: out _,
            composed: out _,
            reason: out var reason,
            rootBytes: rootBytes,
            rootResolvedName: "root.world.json",
            source: Source(
                cancellationToken: TestContext.Current.CancellationToken,
                store: store
            )
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: BasisKey(name: "ghost.world.json")
        );
    }
}
