using System.Text;
using Puck.Assets;
using Puck.SignedDistance.Baking;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a creation's bake is keyed by its pin, the bake derivation's fingerprint and the tier, and one
/// key is one set of bytes. One cache (<see cref="WorldBakeStore"/>) is filled two ways: a compiled world's <c>BAKE</c> chunk names the
/// keys it needs and the bake pack (<see cref="WorldBakePack"/>) that ships them, so a boot from it holds every bake the
/// pack carries, byte for byte what a fresh bake makes, and bakes nothing, counted through the <c>sdf.bakes</c> source;
/// and on a miss, a key the pack lacks or a pack that is gone, the presentation's <see cref="WorldBakeSchedule"/> bakes
/// in the background only the prototypes the cache lacks, keeps them under the cache's directory, and reports each
/// ready. A boot that finds no compiled world holding <c>BAKE</c> leaves it out rather than baking on its critical path.
/// The chunk's version comes from the fingerprint, and the pack of this world's bakes is pinned. Bakes never reach
/// simulation state.
/// </summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class CreationBakeLawTests {
    // Three prototypes: two that bake, and one whose only shape is shading detail, which has no bake.
    private const string Prototypes = """
        "prototypes": [
            { "id": "pip", "document": { "schema": "puck.creation.v1", "name": "pip", "palette": [{ "color": "#CC3322", "emissive": 0, "specular": 0, "roughness": 0 }], "shapes": [{ "id": 0, "name": "pip", "type": "Sphere", "position": [0, 0.5, 0], "rotation": [0, 0, 0, 1], "scale": [0.5, 0.5, 0.5], "material": 0, "blend": "Union" }] } },
            { "id": "block", "document": { "schema": "puck.creation.v1", "name": "block", "palette": [{ "color": "#3355CC", "emissive": 0, "specular": 0, "roughness": 0 }], "shapes": [{ "id": 0, "name": "block", "type": "Box", "position": [0, 0.4, 0], "rotation": [0, 0, 0, 1], "scale": [0.8, 0.8, 0.8], "material": 0, "blend": "Union" }] } },
            { "id": "glint", "document": { "schema": "puck.creation.v1", "name": "glint", "palette": [{ "color": "#FFFFFF", "emissive": 0, "specular": 0, "roughness": 0 }], "shapes": [{ "id": 0, "name": "glint", "type": "Sphere", "position": [0, 0.2, 0], "rotation": [0, 0, 0, 1], "scale": [0.2, 0.2, 0.2], "material": 0, "blend": "Union", "detail": true }] } }
        ],
        """;
    // The bake pack of this file's world. Regenerating DerivationFingerprint.Bake re-records this pin.
    private const string PinnedProduct = "sha256-64/d69f3b35c52c8696";

    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(minutes: 2);

    private static string WriteWorld(TemporaryDirectory directory) {
        CompiledWorldLawTests.WritePatchBeside(directory: directory);

        return directory.WriteBytes(
            bytes: Encoding.UTF8.GetBytes(s: CompiledWorldLawTests.World
                .Replace(newValue: CompiledWorldLawTests.PatchBeside, oldValue: "PATCH")
                .Replace(newValue: (Prototypes + "\n  \"patches\": ["), oldValue: "\"patches\": [")),
            name: "bakes.world.json"
        );
    }
    private static CompiledWorldContext Context(string path) => new(
        authored: new WorldDefinition(),
        instanceIdentity: WorldDefinitionLoader.BootInstanceName,
        sourceName: path
    );
    private static CompiledWorldChunks Chunks(WorldBakeStore? store) => WorldBakeChunk.Register(
        chunks: CompiledWorldChunks.Standard,
        store: store
    );
    // The keys a compiled world's BAKE chunk names, and the pack reference it records.
    private static (string Reference, IReadOnlyList<ContentPin> Keys) Named(byte[] compiled) {
        Assert.True(condition: CompiledWorld.TryDecode(container: out var container, content: compiled, header: out _, reason: out var reason), userMessage: reason);
        Assert.True(condition: container.TryFind(chunk: out var stored, code: WorldBakeChunk.BakeCode));
        Assert.True(condition: WorldBakeChunk.TryRead(keys: out var keys, packReference: out var reference, payload: stored.Payload.Span, reason: out reason), userMessage: reason);

        return (reference, keys);
    }
    // Compiles the world beside its document, with its bakes derived into a store, and writes beside it the pack of
    // every key the compiled world names except those in `omit`. Returns the pack's bytes.
    private static byte[] CompileWithPack(string path, params string[] omit) {
        var store = new WorldBakeStore();
        var compiled = CompiledWorldLawTests.Compile(chunks: Chunks(store: store), path: path);

        var (reference, keys) = Named(compiled: compiled);
        var omitted = WorldBakeStore.RequestsOf(definition: Definition(), quality: WorldBakeChunk.Quality)
            .Where(predicate: request => omit.Contains(value: request.PrototypeId))
            .Select(selector: static request => request.Key.Pin)
            .ToHashSet();
        var pack = WorldBakePack.Encode(outcomes: keys
            .Where(predicate: key => !omitted.Contains(item: key))
            .Select(selector: key => {
                Assert.True(condition: store.TryGetHeld(key: key, outcome: out var outcome));

                return KeyValuePair.Create(key: key, value: outcome);
            }));

        Assert.Equal(actual: reference, expected: WorldBakePack.FileName);
        File.WriteAllBytes(bytes: compiled, path: CompiledWorld.Beside(documentPath: path));
        File.WriteAllBytes(bytes: pack, path: WorldBakePack.Resolve(documentPath: path, reference: reference));

        return pack;
    }
    private static WorldDefinition Definition() => WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: $$"""
        {
          "schema": "puck.world.definition.v1",
          "documentId": "creation-bake-law",
          {{Prototypes.TrimEnd().TrimEnd(trimChar: ',')}}
        }
        """));
    private static WorldDefinition WithBlockScale(WorldDefinition definition, float scale) {
        var creations = definition.Creations.Select(selector: prototype => ((prototype.Id.Value == "block")
            ? new WorldPrototype(
                Document: prototype.Document with {
                    Shapes = [prototype.Document.Shapes![0] with { Scale = new System.Numerics.Vector3(value: scale) }],
                },
                Id: prototype.Id
            )
            : prototype)).ToArray();

        return definition with { CreationsRaw = creations };
    }
    private static void Drain(WorldBakeSchedule schedule, WorldDefinition definition) {
        var deadline = (DateTime.UtcNow + Patience);

        schedule.Pump(definition: definition);

        while (schedule.IsBusy) {
            Assert.True(condition: (DateTime.UtcNow < deadline), userMessage: "the background bakes did not finish");
            Thread.Sleep(millisecondsTimeout: 1);
            schedule.Pump(definition: definition);
        }
    }
    private static (long Held, long Scheduled, long Baked, long Refused) Counts(WorldBakeSchedule schedule) => (
        schedule.Read(kind: WorldBakeSchedule.Held),
        schedule.Read(kind: WorldBakeSchedule.Scheduled),
        schedule.Read(kind: WorldBakeSchedule.Baked),
        schedule.Read(kind: WorldBakeSchedule.Refused)
    );
    private static string AnotherFingerprint(string fingerprint) =>
        (((fingerprint[0] == '0') ? '1' : '0') + fingerprint[1..]);

    [Fact]
    public void BakingOneCreationTwiceGivesOneKeyAndTheSameBytes() {
        var request = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[0];
        var again = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[0];
        var first = WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: request, work: out var work);
        var second = WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: again, work: out _);

        Assert.Equal(expected: request.Key, actual: again.Key);
        Assert.Equal(expected: request.Key.Pin, actual: again.Key.Pin);
        Assert.Equal(actual: second, expected: first);
        Assert.True(condition: (work.FieldEvaluations > 0L));
        Assert.True(condition: CreationBakeCodec.TryDecode(bake: out var decoded, content: first, refusal: out _));
        Assert.Equal(expected: first, actual: CreationBakeCodec.Encode(bake: decoded));
    }
    [Fact]
    public void TheCodecRefusesAnImpostorTextureInAnotherUsagesSlot() {
        var request = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[0];

        Assert.True(condition: CreationBakeCodec.TryDecode(bake: out var bake, content: WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: request, work: out _), refusal: out _));

        var swapped = CreationBakeCodec.Encode(bake: (bake with { Impostor = (bake.Impostor with { Depth = bake.Impostor.Emission, Emission = bake.Impostor.Depth }) }));

        _ = Assert.Throws<InvalidDataException>(testCode: () => CreationBakeCodec.Decode(bake: out _, content: swapped, refusal: out _));
    }
    [Fact]
    public void TheKeyMovesWithTheCreationTheFingerprintAndTheTier() {
        var key = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Standard)[0].Key;
        ContentPin[] moved = [
            (key with { CreationPin = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Standard)[1].Key.CreationPin }).Pin,
            (key with { Fingerprint = AnotherFingerprint(fingerprint: key.Fingerprint) }).Pin,
            (key with { Quality = SdfBakeQuality.Preview }).Pin,
        ];

        Assert.Equal(expected: DerivationFingerprint.Bake, actual: key.Fingerprint);
        Assert.All(collection: moved, action: pin => Assert.NotEqual(expected: key.Pin, actual: pin));
        Assert.Equal(expected: moved.Length, actual: moved.Distinct().Count());
    }
    [Fact]
    public void AChangedFingerprintMissesTheOldStoreAndRebakesWhileTheSameFingerprintIsHeld() {
        using var directory = new TemporaryDirectory();
        var definition = Definition();

        definition = definition with { CreationsRaw = [definition.Creations[0]] };

        var request = Assert.Single(collection: WorldBakeStore.RequestsOf(definition: definition, quality: SdfBakeQuality.Preview));
        var previous = request with { Key = request.Key with { Fingerprint = AnotherFingerprint(fingerprint: request.Key.Fingerprint) } };
        var path = directory.PathOf(name: "bakes");
        var oldStore = new WorldBakeStore(directory: path);

        Assert.True(condition: oldStore.Keep(
            key: previous.Key.Pin,
            outcome: WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: previous, work: out _)
        ));

        var currentStore = new WorldBakeStore(directory: path);

        Assert.True(condition: currentStore.TryGet(key: previous.Key.Pin, outcome: out _));
        Assert.False(condition: currentStore.TryGet(key: request.Key.Pin, outcome: out _));

        using (var changed = new WorldBakeSchedule(quality: SdfBakeQuality.Preview, store: currentStore)) {
            Drain(definition: definition, schedule: changed);
            Assert.Equal(expected: (0L, 1L), actual: (changed.Read(kind: WorldBakeSchedule.Held), changed.Read(kind: WorldBakeSchedule.Baked)));
            Assert.Equal(expected: WorldBakeState.Ready, actual: changed.StateOf(prototypeId: request.PrototypeId));
        }

        using var unchanged = new WorldBakeSchedule(quality: SdfBakeQuality.Preview, store: new WorldBakeStore(directory: path));

        Drain(definition: definition, schedule: unchanged);
        Assert.Equal(expected: (1L, 0L), actual: (unchanged.Read(kind: WorldBakeSchedule.Held), unchanged.Read(kind: WorldBakeSchedule.Baked)));
    }
    [Fact]
    public void AMissingBakeIsScheduledInTheBackgroundKeptAndReportedReady() {
        using var directory = new TemporaryDirectory();
        var definition = Definition();

        using (var schedule = new WorldBakeSchedule(quality: SdfBakeQuality.Preview, store: new WorldBakeStore(directory: directory.PathOf(name: "bakes")))) {
            schedule.Pump(definition: definition);

            Assert.Equal(expected: WorldBakeState.Pending, actual: schedule.StateOf(prototypeId: "pip"));
            Assert.Equal(expected: WorldBakeState.Unknown, actual: schedule.StateOf(prototypeId: "absent"));
            Drain(definition: definition, schedule: schedule);
            Assert.Equal(expected: (0L, 3L, 2L, 1L), actual: Counts(schedule: schedule));
            Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "pip"));
            Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "block"));
            Assert.Equal(expected: WorldBakeState.Refused, actual: schedule.StateOf(prototypeId: "glint"));
        }

        // A later run over the same directory reads every outcome back and bakes nothing.
        using var later = new WorldBakeSchedule(quality: SdfBakeQuality.Preview, store: new WorldBakeStore(directory: directory.PathOf(name: "bakes")));

        Drain(definition: definition, schedule: later);
        Assert.Equal(expected: (0L, 0L), actual: (later.Read(kind: WorldBakeSchedule.Baked), later.Read(kind: WorldBakeSchedule.Refused)));
        Assert.Equal(expected: 3L, actual: later.Read(kind: WorldBakeSchedule.Held));
        Assert.Equal(expected: WorldBakeState.Ready, actual: later.StateOf(prototypeId: "block"));

        // Bakes this machine made and kept are never the world's own: reconciled again with every bake now held from the
        // cache, the world still ships none, so its fields draw by default on this machine as on a clean one.
        later.Pump(definition: Definition());
        Assert.True(condition: later.HasReconciled);
        Assert.False(condition: later.Ships);
    }
    /// <summary>A disposed schedule writes nothing more under its store's directory, so the directory's owner may delete
    /// it: the bake it was running stops at its next field evaluation, and disposal returns once it has. Red leg: a
    /// schedule that only signals its bake leaves it to finish and keep its outcome after disposal.</summary>
    [Fact]
    public void ADisposedScheduleWritesNothingMoreUnderItsStore() {
        using var directory = new TemporaryDirectory();
        var definition = Definition();
        var store = directory.PathOf(name: "bakes");

        using (var schedule = new WorldBakeSchedule(quality: SdfBakeQuality.Preview, store: new WorldBakeStore(directory: store))) {
            schedule.Pump(definition: definition);
            Assert.True(condition: schedule.IsBusy);
            // A schedule disposed before its bake began has nothing in flight to stop, so wait until the bake runs.
            Assert.True(condition: SpinWait.SpinUntil(condition: () => schedule.IsBaking, timeout: TestLiveness.Bound));
        }

        var disposed = Entries(directory: store);

        // Baking every prototype here takes at least as long as the one bake the schedule started would.
        foreach (var request in WorldBakeStore.RequestsOf(definition: definition, quality: SdfBakeQuality.Preview)) {
            _ = WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: request, work: out _);
        }

        Assert.Equal(expected: disposed, actual: Entries(directory: store));
    }

    private static string[] Entries(string directory) => (Directory.Exists(path: directory)
        ? [.. Directory.EnumerateFileSystemEntries(path: directory, searchOption: SearchOption.AllDirectories, searchPattern: "*").Order(comparer: StringComparer.Ordinal)]
        : []);

    /// <summary>A ready bake draws in place of its field, and the switch is counted once. Before its bake lands, a
    /// placement draws through its field: no mesh draw, and a camera-visible instance. Once the bake is ready it draws
    /// the baked mesh and its impostor's card, with its instance camera-hidden so its field still casts shadows and
    /// occludes. A creation whose
    /// bake is refused keeps its field, and a later rebuild counts no second switch.</summary>
    [Fact]
    public void AReadyBakeDrawsInPlaceOfItsFieldAndTheSwitchIsCounted() {
        static WorldPlacement Placed(string prototypeId, float x) => new(
            Id: $"{prototypeId}-placed",
            Position: new Puck.Assets.Documents.DocumentVector3(value: new System.Numerics.Vector3(x: x, y: 0f, z: 0f)),
            PrototypeId: prototypeId,
            Scale: 1f,
            YawDegrees: 0f
        );

        var definition = (Definition() with { PlacementRowsRaw = [Placed(prototypeId: "block", x: 0f), Placed(prototypeId: "glint", x: 4f)] });
        using var schedule = new WorldBakeSchedule(quality: SdfBakeQuality.Preview, store: new WorldBakeStore());

        (List<Puck.SdfVm.SdfMeshDraw> Draws, Puck.SignedDistance.SdfProgram Program) Emit() {
            var draws = new List<Puck.SdfVm.SdfMeshDraw>();
            var builder = new Puck.SignedDistance.SdfProgramBuilder();

            WorldPlacementStamper.EmitStatic(
                bakedFor: prototypeId => (schedule.TryGetDraw(draw: out var baked, prototypeId: prototypeId) ? baked : null),
                builder: builder,
                creations: definition.Creations,
                definition: definition,
                meshDraws: draws,
                placements: definition.Placements
            );

            return (draws, builder.Build());
        }

        schedule.Pump(definition: definition);

        var pending = Emit();

        Assert.Empty(collection: pending.Draws);
        Assert.DoesNotContain(collection: pending.Program.Instances, filter: static instance => instance.CameraHidden);

        Drain(definition: definition, schedule: schedule);

        var ready = Emit();
        // A ready bake draws two representations of the one placement: its mesh, recorded while it is large on screen, and
        // its impostor's card, recorded once it is small (the view chooses: SdfMeshLodSelector). Both are bounded by the
        // impostor's sphere, and the switch is the impostor's view edge.
        Assert.Equal(expected: 2, actual: ready.Draws.Count);

        var draw = ready.Draws[0];
        var card = ready.Draws[1];

        Assert.False(condition: draw.Lod!.Value.Far);
        Assert.Null(@object: draw.Impostor);
        Assert.True(condition: card.Lod!.Value.Far);
        Assert.NotNull(@object: card.Impostor);
        Assert.Same(expected: Puck.SdfVm.SdfMeshCard.Mesh, actual: card.Mesh);
        Assert.Equal(expected: (card.Impostor!.Center, card.Impostor.Radius, ((float)card.Impostor.ViewTexels)), actual: (draw.Lod!.Value.Center, draw.Lod!.Value.Radius, draw.Lod!.Value.SwitchPixels));
        Assert.Equal(expected: draw.Material, actual: card.Material);
        Assert.Equal(expected: draw.ObjectToWorld, actual: card.ObjectToWorld);

        Assert.True(condition: (draw.Mesh.TriangleCount > 0));
        // The baked mesh draws its vertex normals and each triangle's palette entry: the block's palette has one.
        Assert.Equal(expected: draw.Mesh.Positions.Length, actual: draw.Mesh.Normals.Length);
        Assert.Equal(expected: draw.Mesh.TriangleCount, actual: draw.Mesh.TriangleMaterials.Length);
        Assert.All(collection: draw.Mesh.TriangleMaterials.ToArray(), action: static material => Assert.Equal(actual: material, expected: 0u));
        Assert.Contains(collection: ready.Program.Instances, filter: static instance => instance.CameraHidden);
        Assert.Contains(collection: ready.Program.Instances, filter: static instance => !instance.CameraHidden);
        Assert.Equal(expected: 1L, actual: schedule.Read(kind: WorldBakeSchedule.Drawn));
        _ = Emit();
        Assert.Equal(expected: 1L, actual: schedule.Read(kind: WorldBakeSchedule.Drawn));
    }
    [Fact]
    public void EditingOnePrototypeRebakesOnlyThatPrototype() {
        var definition = Definition();
        using var schedule = new WorldBakeSchedule(quality: SdfBakeQuality.Preview, store: new WorldBakeStore());

        Drain(definition: definition, schedule: schedule);

        var before = Counts(schedule: schedule);
        var edited = WithBlockScale(definition: definition, scale: 0.6f);

        schedule.Pump(definition: edited);
        Assert.Equal(expected: WorldBakeState.Pending, actual: schedule.StateOf(prototypeId: "block"));
        Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "pip"));
        Drain(definition: edited, schedule: schedule);

        var after = Counts(schedule: schedule);

        Assert.Equal(actual: ((after.Held - before.Held), (after.Scheduled - before.Scheduled), (after.Baked - before.Baked), (after.Refused - before.Refused)), expected: (0L, 1L, 1L, 0L));
        Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "block"));
    }
    [Fact]
    public void ACompiledWorldWithItsPackBakesNothingOnLoadAndHoldsWhatAFreshBakeMakes() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory);

        _ = CompileWithPack(path: path);

        var store = new WorldBakeStore();
        var boot = CompiledWorldLawTests.Boot(cache: new CompiledWorldCache(chunks: Chunks(store: store), directory: directory.PathOf(name: "state/compiled")), path: path);

        Assert.Equal(expected: ["DEFN", "ASST", "BAKE"], actual: boot.Resolution.Kept.Select(selector: static code => code.ToString()));
        Assert.Equal(expected: 3, actual: store.HeldCount);

        foreach (var request in WorldBakeStore.RequestsOf(definition: boot.Admission.Definition, quality: WorldBakeChunk.Quality)) {
            Assert.True(condition: store.TryGetHeld(key: request.Key.Pin, outcome: out var loaded));
            Assert.Equal(expected: WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: request, work: out _), actual: loaded.ToArray());
        }

        using var schedule = new WorldBakeSchedule(store: store);

        schedule.Pump(definition: boot.Admission.Definition);
        Assert.False(condition: schedule.IsBusy);
        Assert.True(condition: schedule.Ships);
        Assert.Equal(expected: (3L, 0L, 0L, 0L), actual: Counts(schedule: schedule));
        Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "pip"));
        Assert.Equal(expected: WorldBakeState.Refused, actual: schedule.StateOf(prototypeId: "glint"));
    }
    /// <summary>The parity world ships its bakes: compiled beside its companion files, its <c>BAKE</c> chunk names a key for
    /// every creation it draws and the pack carries each, so a boot from it holds them all, reconciles with nothing
    /// scheduled and nothing baked, and ships (the leg that <c>puck parity</c> holds the device to).</summary>
    [Fact]
    public void TheParityWorldShipsItsBakesAndABootFromItsPackBakesNothing() {
        using var directory = new TemporaryDirectory();

        foreach (var file in Directory.GetFiles(path: Path.Combine(path1: AuthoredGameFixtures.Root, path2: "tests", path3: "Puck.Parity"))) {
            File.Copy(
                destFileName: directory.PathOf(name: Path.GetFileName(path: file)),
                sourceFileName: file
            );
        }

        var path = directory.PathOf(name: "parity.puck");

        _ = CompileWithPack(path: path);

        var store = new WorldBakeStore();
        var boot = CompiledWorldLawTests.Boot(cache: new CompiledWorldCache(chunks: Chunks(store: store), directory: directory.PathOf(name: "state/compiled")), path: path);
        var creations = boot.Admission.Definition.Creations.Count;

        Assert.True(condition: (creations > 0));
        Assert.Equal(expected: creations, actual: store.HeldCount);

        using var schedule = new WorldBakeSchedule(store: store);

        schedule.Pump(definition: boot.Admission.Definition);
        Assert.False(condition: schedule.IsBusy);
        Assert.True(condition: schedule.Ships);
        Assert.Equal(expected: (((long)creations), 0L, 0L, 0L), actual: Counts(schedule: schedule));
        Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "vocabRig"));
    }
    /// <summary>A bake held under a key that this baker cannot read, such as one a build before the impostor carried its
    /// material plane kept (a plane in the wrong place, or bytes cut short), never silently suppresses cards: the
    /// prototype draws through its field, the refusal is counted under <c>sdf.bakes.undecodable</c> once and named on the
    /// error stream, and no exception reaches the frame thread. The bake's key carries the baker's version, which is how
    /// such bytes stop matching once the baker moves.</summary>
    [Fact]
    public void AHeldBakeThisBakerCannotDecodeDrawsTheFieldAndIsCountedByName() {
        var definition = Definition();
        var request = WorldBakeStore.RequestsOf(definition: definition, quality: WorldBakeChunk.Quality).Single(predicate: static request => (request.PrototypeId == "block"));
        var fresh = WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: request, work: out _);

        Assert.True(condition: CreationBakeCodec.TryDecode(bake: out var bake, content: fresh, refusal: out _));

        // A bake as an older build wrote it: the impostor's slot after the depth holds the emission texture, not the material
        // plane this baker expects there.
        byte[][] held = [
            CreationBakeCodec.Encode(bake: (bake! with { Impostor = (bake.Impostor with { Material = bake.Impostor.Emission }) })),
            fresh[..^9],
        ];

        foreach (var bytes in held) {
            var store = new WorldBakeStore();

            Assert.True(condition: store.Keep(key: request.Key.Pin, outcome: bytes));

            using var schedule = new WorldBakeSchedule(store: store);

            schedule.Pump(definition: definition);

            var asked = schedule.TryGetDraw(draw: out var draw, prototypeId: "block");

            Assert.False(condition: asked);
            Assert.Null(@object: draw);
            Assert.Equal(expected: 1L, actual: schedule.Read(kind: WorldBakeSchedule.Undecodable));
            // Asked again, it is the same refusal and is counted once.
            Assert.False(condition: schedule.TryGetDraw(draw: out _, prototypeId: "block"));
            Assert.Equal(expected: 1L, actual: schedule.Read(kind: WorldBakeSchedule.Undecodable));
            Assert.Equal(expected: 0L, actual: schedule.Read(kind: WorldBakeSchedule.Drawn));
        }

        // The red leg: a held bake this baker wrote draws, with its impostor.
        var healthy = new WorldBakeStore();

        Assert.True(condition: healthy.Keep(key: request.Key.Pin, outcome: fresh));

        using var good = new WorldBakeSchedule(store: healthy);

        good.Pump(definition: definition);
        Assert.True(condition: good.TryGetDraw(draw: out var shown, prototypeId: "block"));
        Assert.NotNull(@object: shown!.Impostor);
        Assert.Equal(expected: 0L, actual: good.Read(kind: WorldBakeSchedule.Undecodable));
    }
    [Fact]
    public void ReadinessWaitsForAReconcileAndAnEmptyWorldSettles() {
        using var schedule = new WorldBakeSchedule(store: new WorldBakeStore());
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults());

        foreach (var lever in new bool?[] { null, true, false }) {
            settings.Bakes = lever;
            Assert.Equal(expected: lever, actual: settings.Bakes);
            Assert.False(condition: schedule.IsReadyForDrawing(bakes: settings.Bakes));
        }

        schedule.Pump(definition: new WorldDefinition());
        Assert.True(condition: schedule.HasReconciled);
        Assert.True(condition: schedule.IsSettled);
        Assert.False(condition: schedule.Ships);

        foreach (var lever in new bool?[] { null, true, false }) {
            settings.Bakes = lever;
            Assert.True(condition: schedule.IsReadyForDrawing(bakes: settings.Bakes));
            Assert.Equal(expected: (lever ?? false), actual: settings.DrawsBakes(schedule: schedule));
        }
    }
    [Fact]
    public void APackLoadedForOneWorldDoesNotShipASourceWorldWithTheSameKeys() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory);

        _ = CompileWithPack(path: path);

        var store = new WorldBakeStore();
        var boot = CompiledWorldLawTests.Boot(cache: new CompiledWorldCache(chunks: Chunks(store: store), directory: directory.PathOf(name: "state/compiled")), path: path);
        using var schedule = new WorldBakeSchedule(store: store);
        var settings = new WorldRenderSettings(defaults: new WorldRenderDefaults());

        schedule.Pump(definition: boot.Admission.Definition);
        Assert.True(condition: settings.DrawsBakes(schedule: schedule));
        schedule.Pump(definition: (boot.Admission.Definition with { DocumentDirectory = directory.PathOf(name: "copy") }));
        Assert.True(condition: schedule.Ships);
        schedule.Pump(definition: Definition());
        Assert.True(condition: schedule.IsSettled);
        Assert.False(condition: schedule.Ships);
        Assert.False(condition: settings.DrawsBakes(schedule: schedule));
        Assert.Equal(expected: 0L, actual: schedule.Read(kind: WorldBakeSchedule.Scheduled));
        settings.Bakes = true;
        Assert.True(condition: settings.DrawsBakes(schedule: schedule));
    }
    [Fact]
    public void AKeyThePackLacksIsBakedInTheBackground() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory);

        _ = CompileWithPack(omit: "block", path: path);

        var store = new WorldBakeStore();
        var boot = CompiledWorldLawTests.Boot(cache: new CompiledWorldCache(chunks: Chunks(store: store), directory: directory.PathOf(name: "state/compiled")), path: path);

        Assert.Contains(collection: boot.Resolution.Kept.Select(selector: static code => code.ToString()), expected: "BAKE");
        Assert.Equal(expected: 2, actual: store.HeldCount);

        using var schedule = new WorldBakeSchedule(store: store);

        schedule.Pump(definition: boot.Admission.Definition);
        Assert.False(condition: schedule.Ships);
        Assert.Equal(expected: WorldBakeState.Pending, actual: schedule.StateOf(prototypeId: "block"));
        Drain(definition: boot.Admission.Definition, schedule: schedule);
        Assert.Equal(expected: (2L, 1L, 1L, 0L), actual: Counts(schedule: schedule));
        Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "block"));
    }
    [Fact]
    public void AMissingPackLeavesEveryBakeToTheBackground() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory);

        _ = CompileWithPack(path: path);
        File.Delete(path: directory.PathOf(name: WorldBakePack.FileName));

        var store = new WorldBakeStore(directory: directory.PathOf(name: "bakes"));
        var boot = CompiledWorldLawTests.Boot(cache: new CompiledWorldCache(chunks: Chunks(store: store), directory: directory.PathOf(name: "state/compiled")), path: path);

        Assert.Contains(collection: boot.Resolution.Kept.Select(selector: static code => code.ToString()), expected: "BAKE");
        Assert.Equal(expected: 0, actual: store.HeldCount);

        using var schedule = new WorldBakeSchedule(quality: WorldBakeChunk.Quality, store: store);

        Drain(definition: boot.Admission.Definition, schedule: schedule);
        Assert.Equal(expected: (0L, 3L, 2L, 1L), actual: Counts(schedule: schedule));
    }
    [Fact]
    public void ABootWithoutACompiledWorldLeavesTheBakesToTheBackground() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory);
        var store = new WorldBakeStore();
        var cache = new CompiledWorldCache(chunks: Chunks(store: store), directory: directory.PathOf(name: "state/compiled"));
        var boot = CompiledWorldLawTests.Boot(cache: cache, path: path);

        Assert.Equal(expected: ["DEFN", "ASST"], actual: boot.Resolution.Derived.Select(selector: static code => code.ToString()));
        Assert.Equal(expected: ["BAKE"], actual: boot.Resolution.Deferred.Select(selector: static code => code.ToString()));
        Assert.Equal(expected: 0, actual: store.HeldCount);
        Assert.True(condition: CompiledWorld.TryDecode(container: out var written, content: File.ReadAllBytes(path: cache.FileFor(documentPath: path)), header: out _, reason: out var reason), userMessage: reason);
        Assert.False(condition: written.TryFind(chunk: out _, code: ChunkCode.Parse(text: "BAKE")));

        // Bakes are presentation only: the world a boot admits, and the state it steps to, do not depend on them.
        var compiled = CompiledWorldLawTests.Compile(chunks: Chunks(store: null), path: path);

        File.WriteAllBytes(bytes: compiled, path: CompiledWorld.Beside(documentPath: path));

        var baked = CompiledWorldLawTests.Boot(cache: new CompiledWorldCache(chunks: Chunks(store: new WorldBakeStore()), directory: directory.PathOf(name: "state/other")), path: path);

        Assert.Equal(
            expected: CompiledWorldLawTests.HashAfter(definition: boot.Admission.Definition, ticks: 30),
            actual: CompiledWorldLawTests.HashAfter(definition: baked.Admission.Definition, ticks: 30)
        );
    }
    [Fact]
    public void TheChunkNamesOnlyKeysItsVersionComesFromTheFingerprintAndItsPackIsPinned() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory);
        var chunk = new WorldBakeChunk(store: null);
        var pack = CompileWithPack(path: path);
        var product = AssetContentHash.Compute(content: pack);

        Assert.Equal(expected: DerivationFingerprint.BakeChunkVersion, actual: chunk.Version);
        Assert.False(condition: chunk.DerivesOnBoot);
        Assert.True(condition: CompiledWorld.TryDecode(container: out var container, content: File.ReadAllBytes(path: CompiledWorld.Beside(documentPath: path)), header: out _, reason: out var reason), userMessage: reason);
        Assert.True(condition: container.TryFind(chunk: out var stored, code: chunk.Code));
        Assert.True(condition: CompiledWorldChunks.Holds(chunk: chunk, context: Context(path: path), stored: stored));
        Assert.False(condition: CompiledWorldChunks.Holds(chunk: chunk, context: Context(path: path), stored: new ContainerChunk(code: stored.Code, inputs: stored.Inputs, payload: stored.Payload, version: (stored.Version + 1))));

        // The chunk carries the reference and one key per distinct prototype, and no outcome: a store-less derivation
        // names the same keys without baking.
        var (reference, keys) = Named(compiled: File.ReadAllBytes(path: CompiledWorld.Beside(documentPath: path)));

        Assert.Equal(actual: reference, expected: WorldBakePack.FileName);
        Assert.Equal(
            expected: WorldBakeStore.RequestsOf(definition: Definition(), quality: WorldBakeChunk.Quality).Select(selector: static request => request.Key.Pin.Hex).Order(comparer: StringComparer.Ordinal),
            actual: keys.Select(selector: static key => key.Hex)
        );
        Assert.True(condition: CompiledWorld.TryDecode(container: out var storeless, content: CompiledWorldLawTests.Compile(chunks: Chunks(store: null), path: path), header: out _, reason: out reason), userMessage: reason);
        Assert.True(condition: storeless.TryFind(chunk: out var named, code: chunk.Code));
        Assert.Equal(expected: stored.Payload.ToArray(), actual: named.Payload.ToArray());
        Assert.True(condition: WorldBakePack.TryDecode(content: pack, pack: out var decoded, reason: out reason), userMessage: reason);
        Assert.Equal(expected: keys.Count, actual: decoded.Count);
        Assert.True(
            condition: (product.ToString() == PinnedProduct),
            userMessage: $"the pack of this world's bakes at fingerprint {DerivationFingerprint.Bake} is {product}, pinned as {PinnedProduct}; re-record the product pin after regenerating the fingerprint."
        );
    }
    [Fact]
    public void APackIsOneCanonicalFileAndRefusesWhatItCannotAccountFor() {
        var request = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[0];
        var other = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[2];
        var outcomes = new[] {
            KeyValuePair.Create(key: request.Key.Pin, value: ((ReadOnlyMemory<byte>)WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: request, work: out _))),
            KeyValuePair.Create(key: other.Key.Pin, value: ((ReadOnlyMemory<byte>)WorldBakeStore.Bake(cancellationToken: TestContext.Current.CancellationToken, request: other, work: out _))),
        };
        var pack = WorldBakePack.Encode(outcomes: outcomes);

        // The order the outcomes arrive in does not reach the bytes, and a key given twice is refused.
        Assert.Equal(expected: pack, actual: WorldBakePack.Encode(outcomes: Enumerable.Reverse(source: outcomes)));
        _ = Assert.Throws<ArgumentException>(testCode: () => WorldBakePack.Encode(outcomes: [outcomes[0], outcomes[0]]));
        Assert.True(condition: WorldBakePack.TryDecode(content: pack, pack: out var decoded, reason: out var reason), userMessage: reason);
        Assert.True(condition: decoded.TryGet(key: other.Key.Pin, outcome: out var held));
        Assert.Equal(expected: outcomes[1].Value.ToArray(), actual: held.ToArray());

        var damaged = pack.ToArray();

        damaged[^1] ^= 0x01;
        Assert.False(condition: WorldBakePack.TryDecode(content: damaged, pack: out _, reason: out _));
        Assert.False(condition: WorldBakePack.TryDecode(content: pack.AsMemory(start: 0, length: (pack.Length - 1)), pack: out _, reason: out _));
        Assert.Equal(expected: ("../" + WorldBakePack.FileName), actual: WorldBakePack.Reference(documentPath: Path.Combine(path1: Path.GetTempPath(), path2: "out/shards/a.world.json"), packPath: Path.Combine(path1: Path.GetTempPath(), path2: ("out/" + WorldBakePack.FileName))));
    }
}
