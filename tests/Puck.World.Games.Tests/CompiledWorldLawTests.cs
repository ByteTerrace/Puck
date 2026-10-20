using System.Text;
using Puck.Abstractions.Counting;
using Puck.Assets;
using Puck.Testing;
using Xunit;
using static Puck.World.Testing.CompiledWorldFixtures;

namespace Puck.World.Games.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a boot resolves its drawn definition through a compiled world. With none that holds it
/// derives every chunk and writes one into its cache; with one whose header is its own it takes every chunk that
/// still holds, beside the document first, and derives only the rest; a compiled world whose header differs is
/// ignored whole; and a boot from a compiled world admits byte for byte the definition a fresh draw admits. The
/// boots count through the <c>world.boot</c> work source's <c>compiled-hits</c> and <c>chunk-derivations</c>. That every
/// shipped world the World's build compiled boots from its compiled world was held by <c>CompiledCatalogLawTests</c>,
/// which is quarantined in <c>experimental/tests</c>.
/// </summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class CompiledWorldLawTests {
    internal static string WriteWorld(TemporaryDirectory directory, string patchSource) {
        WritePatchBeside(directory: directory);

        return directory.WriteBytes(
            bytes: Encoding.UTF8.GetBytes(s: CompiledWorldFixtures.World.Replace(
                newValue: patchSource.Replace(newChar: '/', oldChar: '\\'),
                oldValue: "PATCH"
            )),
            name: "law.world.json"
        );
    }

    private static byte[] Canonical(WorldDefinitionAdmission admission) =>
        WorldDefinitionSerialization.Serialize(definition: admission.Definition);
    private static string[] Codes(IEnumerable<ChunkCode> codes) => [.. codes.Select(selector: static code => code.ToString())];
    private static string[] DeterministicCounts(WorldBootWork work) => [
        .. WorldBootWork.Kinds.ToArray()
            .Where(predicate: static kind => (kind.Class == WorkClass.Deterministic))
            .Select(selector: kind => $"{kind.Name}={work.Read(kind: kind)}")
    ];

    [Fact]
    public void AMissDerivesEveryChunkAndWritesTheCacheAndTheNextBootAdmitsTheSameWorldFromIt() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory, patchSource: PatchBeside);
        var cache = new CompiledWorldCache(directory: directory.PathOf(name: "state/compiled"));
        var fresh = Boot(cache: cache, path: path);

        Assert.Equal(actual: (fresh.Hits, fresh.Derivations), expected: (0L, 3L));
        Assert.Null(@object: fresh.Resolution.LoadedFrom);
        Assert.Equal(expected: ["DEFN", "ASST", "CURV"], actual: Codes(codes: fresh.Resolution.Derived));
        Assert.Equal(expected: cache.FileFor(documentPath: path), actual: fresh.Resolution.WrittenTo);
        Assert.True(condition: File.Exists(path: cache.FileFor(documentPath: path)));

        var cached = Boot(cache: cache, path: path);

        Assert.Equal(actual: (cached.Hits, cached.Derivations), expected: (1L, 0L));
        Assert.Equal(expected: cache.FileFor(documentPath: path), actual: cached.Resolution.LoadedFrom);
        Assert.Equal(expected: ["DEFN", "ASST", "CURV"], actual: Codes(codes: cached.Resolution.Kept));
        Assert.Null(@object: cached.Resolution.WrittenTo);
        Assert.Equal(expected: Canonical(admission: fresh.Admission), actual: Canonical(admission: cached.Admission));
        Assert.Equal(
            expected: HashAfter(definition: fresh.Admission.Definition, ticks: 30),
            actual: HashAfter(definition: cached.Admission.Definition, ticks: 30)
        );
        // Whether a boot finds a compiled world is the per-user cache's state, so it moves only the kinds classed
        // Pacing: `puck counters` boots each backend's leg against one cache, and the leg that finds the other's
        // compiled world must read every deterministic count the first leg read.
        Assert.Equal(expected: DeterministicCounts(work: fresh.Work), actual: DeterministicCounts(work: cached.Work));
    }
    [Fact]
    public void ACompiledWorldBesideTheDocumentIsTakenAndNothingIsWritten() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory, patchSource: PatchBeside);
        var cache = new CompiledWorldCache(directory: directory.PathOf(name: "state/compiled"));

        File.WriteAllBytes(bytes: Compile(path: path), path: CompiledWorld.Beside(documentPath: path));

        var boot = Boot(cache: cache, path: path);

        Assert.Equal(actual: (boot.Hits, boot.Derivations), expected: (1L, 0L));
        Assert.Equal(expected: CompiledWorld.Beside(documentPath: path), actual: boot.Resolution.LoadedFrom);
        Assert.Null(@object: boot.Resolution.WrittenTo);
        Assert.False(condition: Directory.Exists(path: cache.Directory));
    }
    [Fact]
    public void AMismatchedHeaderIsIgnoredWhole() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory, patchSource: PatchBeside);
        var beside = CompiledWorld.Beside(documentPath: path);
        var valid = Compile(path: path);
        var corrupt = valid.ToArray();

        corrupt[^1] ^= 0xff;

        foreach (var mismatched in ((byte[][])[Compile(path: path, instanceIdentity: "elsewhere"), Compile(catalogFingerprint: "another catalog", path: path), corrupt])) {
            var cache = new CompiledWorldCache(directory: directory.PathOf(name: $"state-{Guid.NewGuid():N}"));

            File.WriteAllBytes(bytes: mismatched, path: beside);

            var boot = Boot(cache: cache, path: path);

            Assert.Equal(actual: (boot.Hits, boot.Derivations), expected: (0L, 3L));
            Assert.Null(@object: boot.Resolution.LoadedFrom);
            Assert.Equal(expected: valid, actual: File.ReadAllBytes(path: cache.FileFor(documentPath: path)));
        }

        // The control: the same file with the boot's own header is taken.
        File.WriteAllBytes(bytes: valid, path: beside);
        Assert.Equal(expected: 1L, actual: Boot(cache: new CompiledWorldCache(directory: directory.PathOf(name: "state-control")), path: path).Hits);
    }
    [Fact]
    public void AnEditedAssetFileRederivesItsChunkAndTheRestKept() {
        using var directory = new TemporaryDirectory();
        var patch = directory.WriteBytes(bytes: File.ReadAllBytes(path: ShippedPatch()), name: "patches/stinger.synth.json");
        var path = WriteWorld(directory: directory, patchSource: patch);
        var cache = new CompiledWorldCache(directory: directory.PathOf(name: "state/compiled"));

        File.WriteAllBytes(bytes: Compile(path: path), path: CompiledWorld.Beside(documentPath: path));

        var held = Boot(cache: cache, path: path);

        Assert.Equal(actual: (held.Hits, held.Derivations), expected: (1L, 0L));

        // Whitespace moves the file's bytes and not the patch document, so the world still admits.
        File.AppendAllText(contents: "\n", path: patch);

        var boot = Boot(cache: cache, path: path);

        Assert.Equal(actual: (boot.Hits, boot.Derivations), expected: (1L, 1L));
        Assert.Equal(expected: ["DEFN", "CURV"], actual: Codes(codes: boot.Resolution.Kept));
        Assert.Equal(expected: ["ASST"], actual: Codes(codes: boot.Resolution.Derived));
        Assert.True(condition: CompiledWorld.TryDecode(container: out var written, content: File.ReadAllBytes(path: cache.FileFor(documentPath: path)), header: out _, reason: out var reason), userMessage: reason);
        Assert.True(condition: written.TryFind(chunk: out var assets, code: AssetChunk.Instance.Code));
        Assert.Equal(expected: AssetContentHash.Compute(content: File.ReadAllBytes(path: patch)), actual: Assert.Single(collection: assets.Inputs).Hash);
    }
    [Fact]
    public void ARegisteredChunkRidesTheSameContainerAndIsRederivedWithItsDependency() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory, patchSource: PatchBeside);
        var chunks = CompiledWorldChunks.Standard.With(chunk: DocumentIdChunk.Instance);
        var cache = new CompiledWorldCache(chunks: chunks, directory: directory.PathOf(name: "state/compiled"));
        var valid = Compile(chunks: chunks, path: path);

        Assert.Throws<ArgumentException>(testCode: () => chunks.With(chunk: DocumentIdChunk.Instance));
        Assert.True(condition: CompiledWorld.TryDecode(container: out var container, content: valid, header: out var header, reason: out var reason), userMessage: reason);
        Assert.True(condition: container.TryFind(chunk: out var registered, code: DocumentIdChunk.Instance.Code));
        Assert.Equal(expected: "compiled-world-law", actual: Encoding.UTF8.GetString(bytes: registered.Payload.Span));
        File.WriteAllBytes(
            bytes: CompiledWorld.Encode(
                chunks: [.. container.Chunks.Select(selector: static chunk => ((chunk.Code == AssetChunk.Instance.Code)
                    ? new ContainerChunk(code: chunk.Code, inputs: [.. chunk.Inputs.Select(selector: static input => (input with { Hash = null }))], payload: chunk.Payload, version: chunk.Version)
                    : chunk))],
                header: header
            ),
            path: CompiledWorld.Beside(documentPath: path)
        );

        var boot = Boot(cache: cache, path: path);

        Assert.Equal(expected: ["DEFN", "CURV"], actual: Codes(codes: boot.Resolution.Kept));
        Assert.Equal(expected: ["ASST", "DOCI"], actual: Codes(codes: boot.Resolution.Derived));
        Assert.Equal(expected: valid, actual: File.ReadAllBytes(path: cache.FileFor(documentPath: path)));
    }
}

// A chunk a later package might register: the drawn document's id, read after ASST so it follows ASST's derivation.
internal sealed class DocumentIdChunk : ICompiledWorldChunk {
    private DocumentIdChunk() { }

    public static DocumentIdChunk Instance { get; } = new();

    public ChunkCode Code { get; } = ChunkCode.Parse(text: "DOCI");
    public IReadOnlyList<ChunkCode> DependsOn { get; } = [AssetChunk.Instance.Code];

    public AssetContentHash? ReadInput(CompiledWorldContext context, string name) => null;
    public bool TryDerive(CompiledWorldContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out CompiledWorldProduct? product, out string reason) {
        product = new CompiledWorldProduct(
            Inputs: [],
            Payload: Encoding.UTF8.GetBytes(s: (context.RequireDrawn().DocumentId ?? string.Empty))
        );
        reason = string.Empty;
        return true;
    }
    public bool TryLoad(CompiledWorldContext context, ReadOnlyMemory<byte> payload, out string reason) {
        reason = string.Empty;
        return true;
    }
}
