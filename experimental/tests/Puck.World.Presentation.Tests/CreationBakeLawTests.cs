using System.Text;
using Puck.Assets;
using Puck.SignedDistance.Baking;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a creation's bake is keyed by its pin, the bake derivation's fingerprint and the tier, and one
/// key is one set of bytes. One cache (<see cref="WorldBakeStore"/>) is filled two ways: a compiled world's <c>BAKE</c> chunk names the
/// keys it needs and the bake pack (<see cref="WorldBakePack"/>) that ships them, so a boot from it holds every bake the
/// pack carries, byte for byte what a fresh bake makes, and bakes nothing, counted through the <c>sdf.bakes</c> source;
/// and on a miss, a key the pack lacks or a pack that is gone, the presentation's <see cref="WorldBakeSchedule"/> bakes
/// in the background only the prototypes the cache lacks, keeps them under the cache's directory, and reports each
/// ready. A boot that finds no compiled world holding <c>BAKE</c> leaves it out rather than baking on its critical path.
/// The chunk's version comes from the fingerprint, and the bytes of this world's bakes are pinned. Bakes never reach
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
        var compiled = CompiledWorldFixtures.Compile(chunks: Chunks(store: store), path: path);

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
    private static (long Held, long Scheduled, long Baked, long Refused) Counts(WorldBakeSchedule schedule) => (
        schedule.Read(kind: WorldBakeSchedule.Held),
        schedule.Read(kind: WorldBakeSchedule.Scheduled),
        schedule.Read(kind: WorldBakeSchedule.Baked),
        schedule.Read(kind: WorldBakeSchedule.Refused)
    );
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
        var boot = CompiledWorldFixtures.Boot(cache: new CompiledWorldCache(chunks: Chunks(store: store), directory: directory.PathOf(name: "state/compiled")), path: path);
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
}
