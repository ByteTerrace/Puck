using System.Text;

using Xunit;

using Puck.Maths;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the border-margin strip proven against REAL authored content — the 2x2 quilt
/// (<c>src/Puck.World/Assets/worlds/quilt-{nw,ne,sw,se}.world.json</c>, authored on lane L3,
/// <c>worktree-agent-a0f3b85ccae545692</c> @ <c>736d5f33</c>) — through the REAL <see cref="WorldStorageNeighbourResolver"/>,
/// never <c>MarginStripValidationLawTests</c>' own <c>IWorldNeighbourResolver</c> stub. The ONE thing standing in for
/// a live account is <see cref="InMemoryObjectBlobStore"/>: this environment carries no Azure credentials, so the
/// <see cref="IObjectBlobStore"/> BACKEND is a fake, but everything downstream of that one interface boundary is the
/// production code path — <see cref="WorldStorageNeighbourResolver.Resolve"/>'s own address construction,
/// <see cref="Puck.World.WorldJsonPayload"/> parsing, <see cref="Puck.World.WorldDefinitionMigrations.Apply"/>, and
/// <see cref="Puck.World.WorldDefinitionValidator"/>'s cross-document floor/identity proof, run unmodified against
/// the checked-in quilt bytes read straight off disk.
/// </summary>
/// <remarks>
/// NOT an L3 content defect — a TEST-HARNESS artifact, tracked down and confirmed after an initial report wrongly
/// blamed the checked-in documents. Every quilt document's own <c>bindingOverlays[0]</c> wheel ("play-primary")
/// holds on page "play-wheel" for group "play", authored WITHOUT declaring that page itself — by design, exactly
/// like every shipped world's own overlays, because <c>Puck.World.WorldDefaultBindings</c> (the REAL
/// engine-default compose layer, <c>WorldDataHookInstaller.cs</c>) already declares
/// <c>PlayGroup</c>/<c>WheelHoldPageId</c> ("play-wheel") as the standard action-wheel hold page every world
/// composes against. <c>ValidateBindingOverlays</c> unconditionally composes <see cref="BindingVocabularyHook.DefaultDocument"/>
/// as the FIRST layer ahead of a document's own overlays — and THIS test project installs its own, deliberately
/// minimal stand-in for that hook (<c>TestHookInstaller.BuildMinimalBindingDocument</c>, group "main" only, no
/// "play" group at all — see that file's own remarks: "irrelevant... never a real control scheme"). Composing a
/// quilt overlay against that minimal stand-in instead of the real engine default is what produced the refusal; a
/// real <c>--world</c> boot (which installs <c>WorldDataHookInstaller</c>'s real default) never sees it. Every
/// LOCAL-side load below neutralizes its in-memory copy's <c>bindingOverlays</c> (never the file on disk) purely to
/// isolate the margin-strip proof from this test-harness gap — not because the checked-in content is wrong.
/// </remarks>
public sealed class QuiltBorderMarginIntegrationTests {
    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly ObjectStorageTarget Target = AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true");

    private const string Nw = "quilt-nw.world.json";
    private const string Ne = "quilt-ne.world.json";
    private const string Sw = "quilt-sw.world.json";
    private const string Se = "quilt-se.world.json";

    // Every quilt document's own kit/rate authoring, read directly off the real files (not re-derived by hand) so
    // the expected-floor arithmetic below can never silently drift from what is actually checked in.
    private const float ColliderRadius = 0.35f;
    private const int RateHz = 60;

    // The validator's OWN fixed-point floor for a quilt border (see DerivedFloor_MatchesTheQuiltsOwnDeclaredData
    // and BelowDerivedFloor_RealNeighbour_RefusesByName_QuotingTheFloor, which both prove this number rather than
    // assert it blindly): reach raw 22938 (Capsule radius 0.35, nearest-quantized — Sphere/Capsule stay
    // nearest-rounded; only the operations flagged by the adversarial review round up) + closingSpeed raw 3145728
    // (48.0 exact, 24+24, SpeedCeiling summed both sides) x tapeLatency raw 1093 (FixedDirectedRounding.
    // TryCeilingQuotient's ceiling of 1/60 — 1092.2667 rounded UP to 1093, not truncated to 1092) + reach, the
    // whole product-plus-sum rounded up ONCE via TryCeilingProductSum = raw 75402 exactly (no remainder) =
    // 1.150543212890625, printed to 5 places. The naive real-arithmetic formula (0.35 + 48/60 = 1.15 exactly) sits
    // BELOW this, as it must for a floor that rounds up rather than nearest.
    private const string ExpectedFloorText = "1.15054";

    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(message: "Puck.slnx not found above the test assembly's base directory.");
    }

    private static string QuiltPath(string fileName) => Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", fileName);

    private static byte[] ReadQuiltBytes(string fileName) => File.ReadAllBytes(path: QuiltPath(fileName: fileName));

    /// <summary>A read-only <see cref="IObjectBlobStore"/> standing in for a live cloud account — seeded per test
    /// with real (or, for the mismatch/floor laws, deliberately mutated) quilt bytes.</summary>
    private sealed class InMemoryObjectBlobStore : IObjectBlobStore {
        private readonly Dictionary<(Guid ObjectId, string Key), byte[]> m_blobs = new();

        /// <summary>Seeds a blob through the WRITER's own address function
        /// (<see cref="WorldOwnedWorldSync.AddressFor"/>) — never a key this test hand-spells to match wherever
        /// <see cref="WorldStorageNeighbourResolver"/> happens to look today. Seeding at the read address would
        /// stay green even if the reader's own encoding drifted from the writer's, since the seed would silently
        /// drift the same way; going through the writer's function is what lets that drift show up as a missing
        /// blob instead.</summary>
        /// <param name="fileName">The quilt document's file name (e.g. <c>"quilt-ne.world.json"</c>) — the SAME
        /// string a <c>WorldReference.Document</c> authors, with the shared <see cref="WorldOwnedWorldFileName.Suffix"/>
        /// stripped before parsing, since <see cref="WorldOwnedWorldSync.AddressFor"/> re-adds it.</param>
        /// <param name="bytes">The document bytes to serve.</param>
        public void Seed(string fileName, byte[] bytes) {
            var id = WorldSafeName.Parse(candidate: fileName[..^WorldOwnedWorldFileName.Suffix.Length]);
            var address = WorldOwnedWorldSync.AddressFor(containerId: ContainerId, id: id);

            m_blobs[(address.ObjectId, address.Key)] = bytes;
        }

        public void SeedAtReferenceSpelling(string document, byte[] bytes) {
            var canonical = WorldOwnedWorldSync.AddressFor(containerId: ContainerId, id: WorldSafeName.Parse(candidate: "address-probe"));
            var slash = canonical.Key.LastIndexOf(value: '/');
            var key = $"{canonical.Key[..(slash + 1)]}{document}";

            m_blobs[(canonical.ObjectId, key)] = bytes;
        }

        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) {
            return new ValueTask<ObjectBlobContent?>(result: m_blobs.TryGetValue(key: (address.ObjectId, address.Key), value: out var bytes)
                ? new ObjectBlobContent(Content: bytes, VersionToken: "test")
                : null);
        }

        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(message: "read-only test double — the neighbour resolver never writes.");

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(message: "read-only test double — the neighbour resolver never lists.");
    }

    private static WorldStorageNeighbourResolver BuildResolver(InMemoryObjectBlobStore store) => new(store: store, target: Target, containerId: ContainerId);

    // Loads a (possibly mutated, in-memory) document through the SAME file-based gate every real load uses
    // (WorldDefinitionFileSource.TryLoad — parse, migrate, validate) rather than a bare WorldJsonPayload.TryParse
    // plus a direct Validate call, so a mutated LOCAL document is proven through the identical pipeline the
    // checked-in files themselves are.
    private static bool TryValidateBytes(byte[] bytes, IWorldNeighbourResolver? neighbours, out string reason) {
        var tempPath = Path.Combine(Path.GetTempPath(), $"quilt-margin-test-{Guid.NewGuid():N}.world.json");

        File.WriteAllBytes(path: tempPath, bytes: bytes);

        try {
            return WorldDefinitionFileSource.TryLoad(path: tempPath, definition: out _, contentHash: out _, reason: out reason, neighbours: neighbours);
        } finally {
            File.Delete(path: tempPath);
        }
    }

    // Parses a quilt document and clears its OWN bindingOverlays[0] — working around THIS PROJECT's minimal
    // BindingVocabularyHook.DefaultDocument stand-in (this suite's class remarks explain why composing a real
    // quilt overlay against it, instead of the real engine default a boot installs, refuses). Applied ONLY to the
    // LOCAL document under test (see this suite's remarks): the resolver never runs Validate on a neighbour
    // (WorldStorageNeighbourResolver parses only), so a neighbour's own bindingOverlays never matters here.
    private static byte[] WithBindingOverlaysNeutralized(byte[] originalBytes) {
        Assert.True(condition: WorldJsonPayload.TryParse(json: Encoding.UTF8.GetString(bytes: originalBytes), info: WorldJsonContext.Default.WorldDefinition, value: out var definition, error: out var parseError), userMessage: parseError);

        return WorldDefinitionSerialization.Serialize(definition: definition with { BindingOverlays = [] });
    }

    // Re-keys a mutated document's marginDepth on one named placement/face and re-serializes it — parse-only
    // (WorldJsonPayload, the SAME primitive WorldStorageNeighbourResolver itself uses), never
    // WorldDefinitionSerialization.Deserialize: a quilt document authors its OWN marginDepth facets (and, in this
    // test project, hits the bindingOverlays/minimal-default-document gap above), so fully validating it here would
    // need its own neighbour resolver, or would refuse outright, just to read a field for this test's setup.
    private static byte[] WithMutatedMarginDepth(byte[] originalBytes, string placementId, string face, float marginDepth) {
        var json = Encoding.UTF8.GetString(bytes: originalBytes);

        Assert.True(condition: WorldJsonPayload.TryParse(json: json, info: WorldJsonContext.Default.WorldDefinition, value: out var definition, error: out var parseError), userMessage: parseError);

        var placements = definition.Placements.Select(selector: placement => (placement.Id == placementId)
            ? placement with {
                FaceSources = placement.FaceSources!.Select(selector: source => (source.Face == face)
                    ? source with { Portal = source.Portal! with { MarginDepth = marginDepth } }
                    : source).ToList(),
            }
            : placement).ToList();

        return WorldDefinitionSerialization.Serialize(definition: definition with { Placements = placements });
    }

    [Fact]
    public void AllFourQuiltDocuments_ValidateAgainstTheirRealNeighbours() {
        var store = new InMemoryObjectBlobStore();
        store.Seed(fileName: Nw, bytes: ReadQuiltBytes(fileName: Nw));
        store.Seed(fileName: Ne, bytes: ReadQuiltBytes(fileName: Ne));
        store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));
        store.Seed(fileName: Se, bytes: ReadQuiltBytes(fileName: Se));

        var resolver = BuildResolver(store: store);

        foreach (var document in new[] { Nw, Ne, Sw, Se }) {
            Assert.True(
                condition: TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: document)), neighbours: resolver, reason: out var reason),
                userMessage: $"{document}: {reason}"
            );
        }
    }

    // The derived-floor arithmetic, read from the SpeedCeiling call this validator itself makes (never re-derived by
    // hand alone): reach (the authored Capsule radius) + closingSpeed (SpeedCeiling summed, both sides) x
    // tapeLatency (one tick period of the shared 60 Hz rate).
    [Fact]
    public void DerivedFloor_MatchesTheQuiltsOwnDeclaredData() {
        var nwBytes = ReadQuiltBytes(fileName: Nw);

        Assert.True(condition: WorldJsonPayload.TryParse(json: Encoding.UTF8.GetString(bytes: nwBytes), info: WorldJsonContext.Default.WorldDefinition, value: out var nw, error: out var parseError), userMessage: parseError);

        var ceiling = WorldFacePortalPolicy.SpeedCeiling(definition: nw);

        Assert.Equal(expected: 24.0, actual: (double)ceiling, precision: 6);
        Assert.Equal(expected: RateHz, actual: nw.SimulationRateHz);
        Assert.Equal(expected: ColliderRadius, actual: ((WorldCollider.Capsule)nw.Kits[0].Collider!).Radius, precision: 5);

        // The naive real-arithmetic check: reach 0.35 + closingSpeed 48 (24 + 24) x tapeLatency (1/60) = 0.35 + 0.8
        // = 1.15 world units — sane for a vaulter whose own capsule radius is a third of that. The validator's own
        // Q48.16 computation, which rounds every step UP rather than nearest, lands a hair ABOVE this (1.15054 —
        // see ExpectedFloorText's own remarks) because a true safety floor must never understate the exact value;
        // BelowDerivedFloor_RealNeighbour_RefusesByName_QuotingTheFloor proves the EXACT value against the live
        // refusal text rather than this naive approximation.
        var expectedFloor = (ColliderRadius + ((double)ceiling * 2 * (1.0 / RateHz)));

        Assert.Equal(expected: 1.15, actual: expectedFloor, precision: 6);
    }

    [Fact]
    public void BelowDerivedFloor_RealNeighbour_RefusesByName_QuotingTheFloor() {
        var store = new InMemoryObjectBlobStore();
        var nwBytes = WithBindingOverlaysNeutralized(originalBytes: WithMutatedMarginDepth(originalBytes: ReadQuiltBytes(fileName: Nw), placementId: "door-to-ne", face: "screen", marginDepth: 1.1f));
        var neBytes = WithMutatedMarginDepth(originalBytes: ReadQuiltBytes(fileName: Ne), placementId: "door-to-nw", face: "screen", marginDepth: 1.1f);

        store.Seed(fileName: Ne, bytes: neBytes);
        store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

        Assert.False(condition: TryValidateBytes(bytes: nwBytes, neighbours: BuildResolver(store: store), reason: out var reason));
        Assert.Contains(expectedSubstring: $"below the derived floor {ExpectedFloorText}", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void BelowDerivedFloor_RealNeighbour_RefusesByName_ControlAtTheAuthoredDepthValidates() {
        Laws.RefusalWithControl(
            lawId: "quilt.below-floor",
            deniedOutcome: () => {
                var store = new InMemoryObjectBlobStore();
                var nwBytes = WithBindingOverlaysNeutralized(originalBytes: WithMutatedMarginDepth(originalBytes: ReadQuiltBytes(fileName: Nw), placementId: "door-to-ne", face: "screen", marginDepth: 1.1f));
                var neBytes = WithMutatedMarginDepth(originalBytes: ReadQuiltBytes(fileName: Ne), placementId: "door-to-nw", face: "screen", marginDepth: 1.1f);

                store.Seed(fileName: Ne, bytes: neBytes);
                store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

                return TryValidateBytes(bytes: nwBytes, neighbours: BuildResolver(store: store), reason: out _);
            },
            controlOutcome: () => {
                var store = new InMemoryObjectBlobStore();
                store.Seed(fileName: Ne, bytes: ReadQuiltBytes(fileName: Ne));
                store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

                return TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: Nw)), neighbours: BuildResolver(store: store), reason: out _);
            });
    }

    [Fact]
    public void MismatchedNeighbourDepth_RealNeighbour_RefusesByName() {
        var store = new InMemoryObjectBlobStore();

        store.Seed(fileName: Ne, bytes: WithMutatedMarginDepth(originalBytes: ReadQuiltBytes(fileName: Ne), placementId: "door-to-nw", face: "screen", marginDepth: 1.5f));
        store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

        Assert.False(condition: TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: Nw)), neighbours: BuildResolver(store: store), reason: out var reason));
        Assert.Contains(expectedSubstring: "authors 1.2", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "authors 1.5", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "must be bit-identical", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MismatchedNeighbourDepth_RealNeighbour_RefusesByName_ControlWithMatchingDepthValidates() {
        Laws.RefusalWithControl(
            lawId: "quilt.mismatched-depth",
            deniedOutcome: () => {
                var store = new InMemoryObjectBlobStore();

                store.Seed(fileName: Ne, bytes: WithMutatedMarginDepth(originalBytes: ReadQuiltBytes(fileName: Ne), placementId: "door-to-nw", face: "screen", marginDepth: 1.5f));
                store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

                return TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: Nw)), neighbours: BuildResolver(store: store), reason: out _);
            },
            controlOutcome: () => {
                var store = new InMemoryObjectBlobStore();

                store.Seed(fileName: Ne, bytes: ReadQuiltBytes(fileName: Ne));
                store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

                return TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: Nw)), neighbours: BuildResolver(store: store), reason: out _);
            });
    }

    [Fact]
    public void UnreachableNeighbour_RealResolver_RefusesByName() {
        var store = new InMemoryObjectBlobStore();

        // quilt-ne.world.json is never seeded — a real "the blob is not there" outcome, not a stub answering
        // Unavailable by fiat.
        store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

        Assert.False(condition: TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: Nw)), neighbours: BuildResolver(store: store), reason: out var reason));
        Assert.Contains(expectedSubstring: "could not be reached", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "no cloud copy", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableNeighbour_RealResolver_RefusesByName_ControlSeededValidates() {
        Laws.RefusalWithControl(
            lawId: "quilt.unreachable-neighbour",
            deniedOutcome: () => {
                var store = new InMemoryObjectBlobStore();

                store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

                return TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: Nw)), neighbours: BuildResolver(store: store), reason: out _);
            },
            controlOutcome: () => {
                var store = new InMemoryObjectBlobStore();

                store.Seed(fileName: Ne, bytes: ReadQuiltBytes(fileName: Ne));
                store.Seed(fileName: Sw, bytes: ReadQuiltBytes(fileName: Sw));

                return TryValidateBytes(bytes: WithBindingOverlaysNeutralized(originalBytes: ReadQuiltBytes(fileName: Nw)), neighbours: BuildResolver(store: store), reason: out _);
            });
    }

    [Fact]
    public void NonCanonicalReferenceDocument_CannotReadABlobTheOwnedWorldWriterCannotAddress() {
        const string nonCanonical = "nested/quilt-ne.world.json";
        var store = new InMemoryObjectBlobStore();
        var bytes = ReadQuiltBytes(fileName: Ne);
        store.SeedAtReferenceSpelling(document: nonCanonical, bytes: bytes);
        store.Seed(fileName: Ne, bytes: bytes);
        var resolver = BuildResolver(store: store);

        var denied = resolver.Resolve(document: nonCanonical);

        Assert.Equal(expected: WorldNeighbourResolutionKind.Unavailable, actual: denied.Kind);
        Assert.Contains(expectedSubstring: "not a canonical owned-world file name", actualString: denied.Reason, comparisonType: StringComparison.Ordinal);
        Laws.RefusalWithControl(
            lawId: "quilt.neighbour-address-writer-encoding",
            deniedOutcome: () => resolver.Resolve(document: nonCanonical).Kind == WorldNeighbourResolutionKind.Resolved,
            controlOutcome: () => resolver.Resolve(document: Ne).Kind == WorldNeighbourResolutionKind.Resolved);
    }

    [Fact]
    public void QuiltCounterpartFrames_MapEveryOffCenterSeamToTheSameWorldPoint() {
        var definitions = new Dictionary<string, WorldDefinition>(comparer: StringComparer.Ordinal);

        foreach (var fileName in new[] { Nw, Ne, Sw, Se }) {
            Assert.True(condition: WorldJsonPayload.TryParse(json: Encoding.UTF8.GetString(bytes: ReadQuiltBytes(fileName: fileName)), info: WorldJsonContext.Default.WorldDefinition, value: out var definition, error: out var parseError), userMessage: parseError);
            definitions[fileName] = definition;
        }

        var seamU = FixedQ4816.FromDouble(value: 0.75);
        var seamV = FixedQ4816.FromDouble(value: 0.2);

        foreach (var (sourceName, source) in definitions) {
            var sourceCatalog = WorldFaceCatalog.For(definition: source);

            foreach (var placement in source.Placements) {
                foreach (var face in (placement.FaceSources ?? [])) {
                    if (face.Portal is not { Arrival: WorldPortalArrival.Mapped } portal) {
                        continue;
                    }

                    var destination = WorldDefinitionRows.FindDestination(destinations: source.Destinations, name: portal.Destination)!;
                    var reference = WorldDefinitionRows.FindReference(references: source.References, name: destination.Reference)!;
                    var neighbour = definitions[reference.Document];
                    Assert.True(condition: WorldPortalCounterpart.TryResolve(definition: neighbour, counterpart: portal.Counterpart, placement: out var counterpartPlacement, face: out var counterpartFace, reason: out var counterpartReason), userMessage: counterpartReason);
                    Assert.True(condition: sourceCatalog.TryFind(placementId: placement.Id, faceName: face.Face, row: out var sourceRow));
                    Assert.True(condition: WorldFaceCatalog.For(definition: neighbour).TryFind(placementId: counterpartPlacement!.Id, faceName: counterpartFace!.Face, row: out var destinationRow));

                    var sourceSeam = sourceRow.Frame.PointAt(u: seamU, v: seamV);
                    var destinationSeam = WorldPortalArrivalMath.CounterpartSeam(destinationFrame: destinationRow.Frame, seamU: seamU, seamV: seamV);
                    var travelerOffset = new FixedVector3(X: FixedQ4816.FromDouble(value: 0.3), Y: FixedQ4816.FromDouble(value: 0.1), Z: FixedQ4816.FromDouble(value: -0.2));
                    var travelerPosition = (sourceSeam + travelerOffset);
                    var travelerVelocity = new FixedVector3(X: FixedQ4816.FromInteger(value: 2), Y: FixedQ4816.Zero, Z: -FixedQ4816.One);
                    var mapped = WorldPortalArrivalMath.ComputeArrival(
                        travelerPosition: travelerPosition,
                        travelerYawRadians: FixedQ4816.Zero,
                        travelerPlanarVelocity: travelerVelocity,
                        travelerVerticalVelocity: FixedQ4816.Zero,
                        sourcePosition: sourceSeam,
                        sourceYawRadians: sourceRow.Frame.PlanarYawRadians,
                        destinationPosition: destinationSeam,
                        destinationYawRadians: destinationRow.Frame.PlanarYawRadians);

                    Assert.Equal(expected: sourceSeam, actual: destinationSeam);
                    Assert.Equal(expected: travelerPosition, actual: mapped.Position);
                    Assert.Equal(expected: travelerVelocity, actual: mapped.PlanarVelocity);
                }
            }
        }
    }
}
