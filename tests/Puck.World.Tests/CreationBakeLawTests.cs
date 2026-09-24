using System.Text;
using Puck.Assets;
using Puck.SignedDistance.Baking;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a creation's bake is keyed by its pin, the baker's version and the tier, and one key is one set
/// of bytes. One cache (<see cref="WorldBakeStore"/>) is filled two ways: a compiled world's <c>BAKE</c> chunk names the
/// keys it needs and the bake pack (<see cref="WorldBakePack"/>) that ships them, so a boot from it holds every bake the
/// pack carries, byte for byte what a fresh bake makes, and bakes nothing, counted through the <c>sdf.bakes</c> source;
/// and on a miss, a key the pack lacks or a pack that is gone, the presentation's <see cref="WorldBakeSchedule"/> bakes
/// in the background only the prototypes the cache lacks, keeps them under the cache's directory, and reports each
/// ready. A boot that finds no compiled world holding <c>BAKE</c> leaves it out rather than baking on its critical path.
/// The chunk's version is the baker's, and the pack of this world's bakes is pinned to it. Bakes never reach simulation
/// state.
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
    // The bake pack of this file's world at the baker's current version. A change to what the baker produces moves
    // SdfBaker.Version and re-records this pin.
    private const uint PinnedVersion = 3;
    private const string PinnedProduct = "sha256-64/f29a3b0f14890bcb";

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

    [Fact]
    public void BakingOneCreationTwiceGivesOneKeyAndTheSameBytes() {
        var request = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[0];
        var again = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[0];
        var first = WorldBakeStore.Bake(request: request, work: out var work);
        var second = WorldBakeStore.Bake(request: again, work: out _);

        Assert.Equal(expected: request.Key, actual: again.Key);
        Assert.Equal(expected: request.Key.Pin, actual: again.Key.Pin);
        Assert.Equal(actual: second, expected: first);
        Assert.True(condition: (work.FieldEvaluations > 0L));
        Assert.True(condition: CreationBakeCodec.TryDecode(bake: out var decoded, content: first, refusal: out _));
        Assert.Equal(expected: first, actual: CreationBakeCodec.Encode(bake: decoded));
    }
    [Fact]
    public void TheKeyMovesWithTheCreationTheBakerAndTheTier() {
        var key = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Standard)[0].Key;
        ContentPin[] moved = [
            (key with { CreationPin = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Standard)[1].Key.CreationPin }).Pin,
            (key with { BakerVersion = (key.BakerVersion + 1) }).Pin,
            (key with { Quality = SdfBakeQuality.Preview }).Pin,
        ];

        Assert.Equal(expected: SdfBaker.Version, actual: key.BakerVersion);
        Assert.All(collection: moved, action: pin => Assert.NotEqual(expected: key.Pin, actual: pin));
        Assert.Equal(expected: moved.Length, actual: moved.Distinct().Count());
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
            Assert.Equal(expected: WorldBakeStore.Bake(request: request, work: out _), actual: loaded.ToArray());
        }

        using var schedule = new WorldBakeSchedule(store: store);

        schedule.Pump(definition: boot.Admission.Definition);
        Assert.False(condition: schedule.IsBusy);
        Assert.Equal(expected: (3L, 0L, 0L, 0L), actual: Counts(schedule: schedule));
        Assert.Equal(expected: WorldBakeState.Ready, actual: schedule.StateOf(prototypeId: "pip"));
        Assert.Equal(expected: WorldBakeState.Refused, actual: schedule.StateOf(prototypeId: "glint"));
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
    public void TheChunkNamesOnlyKeysItsVersionIsTheBakersAndItsPackIsPinnedToIt() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory);
        var chunk = new WorldBakeChunk(store: null);
        var pack = CompileWithPack(path: path);
        var product = AssetContentHash.Compute(content: pack);

        Assert.Equal(expected: SdfBaker.Version, actual: chunk.Version);
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
            condition: ((chunk.Version != PinnedVersion) || (product.ToString() == PinnedProduct)),
            userMessage: $"the pack of this world's bakes at version {PinnedVersion} is {product}, pinned as {PinnedProduct}: a change to what the baker produces moves SdfBaker.Version and re-records this pin."
        );
        Assert.True(condition: (chunk.Version == PinnedVersion), userMessage: $"BAKE is at version {chunk.Version}; re-record its pin at that version.");
    }
    [Fact]
    public void APackIsOneCanonicalFileAndRefusesWhatItCannotAccountFor() {
        var request = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[0];
        var other = WorldBakeStore.RequestsOf(definition: Definition(), quality: SdfBakeQuality.Preview)[2];
        var outcomes = new[] {
            KeyValuePair.Create(key: request.Key.Pin, value: ((ReadOnlyMemory<byte>)WorldBakeStore.Bake(request: request, work: out _))),
            KeyValuePair.Create(key: other.Key.Pin, value: ((ReadOnlyMemory<byte>)WorldBakeStore.Bake(request: other, work: out _))),
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
