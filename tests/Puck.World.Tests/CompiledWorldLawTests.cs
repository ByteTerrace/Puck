using System.Text;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Testing;
using Puck.World.Machines;
using Puck.World.Server;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a boot resolves its drawn definition through a compiled world. With none that holds it
/// derives every chunk and writes one into its cache; with one whose header is its own it takes every chunk that
/// still holds, beside the document first, and derives only the rest; a compiled world whose header differs is
/// ignored whole; and a boot from a compiled world admits byte for byte the definition a fresh draw admits. The
/// boots count through the <c>world.boot</c> work source's <c>compiled-hits</c> and <c>chunk-derivations</c>. Every
/// shipped world the build compiled boots from its compiled world, and each derivation's product is pinned to its
/// version, so a deliberate change to a derivation moves the version.
/// </summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class CompiledWorldLawTests {
    // A world with a first-fill draw site, so DEFN carries drawn cells, and a patch row, so ASST reads a file.
    internal const string World = """
        {
          "schema": "puck.world.definition.v1",
          "documentId": "compiled-world-law",
          "bodies": { "localSeats": 1 },
          "simulation": { "rateHz": 240 },
          "spawnPoints": [ { "id": "origin", "position": [ 0, 0, 0 ] } ],
          "channels": [
            { "name": "forward", "shape": "Bipolar", "role": "MoveAdvance" },
            { "name": "strafe", "shape": "Bipolar", "role": "MoveStrafe" },
            { "name": "turn", "shape": "Bipolar", "role": "Turn" }
          ],
          "collision": { "requirements": [], "contactSkin": 0.02, "maxIterations": 4, "maxSlopeDegrees": 60, "gradientProbe": 0 },
          "bodyMotionPrograms": [
            {
              "name": "grounded",
              "version": "puck.body.program.v1",
              "kind": "Motion",
              "operations": [ "ResolveYawAttitudeAndPlanarFrame", "ResolveHold", "ComputePlanarTargetVelocity", "ShapeVelocity", "SnapYawToPlanarIntent", "ApplyHold", "IntegratePlanarAndVerticalVelocity", "CommitPose" ]
            }
          ],
          "kits": {
            "rows": [
              {
                "name": "idle",
                "bodyMotionProgram": "grounded",
                "motion": {
                  "speed": { "value": 4 },
                  "turn": { "rate": 2 },
                  "shaping": [ { "along": {} } ],
                  "holds": [
                    { "name": "ground", "bond": "Surface", "cone": [ 0, 60 ], "hold": "Gravity", "reach": 1.2, "gravity": { "rise": 20, "fall": 30 }, "envelope": { "sinkSpeed": 30 } },
                    { "name": "air", "bond": "Free", "hold": "Gravity", "gravity": { "rise": 20, "fall": 30 }, "envelope": { "sinkSpeed": 30 } }
                  ]
                }
              }
            ]
          },
          "defaultSeatKit": "idle",
          "views": {
            "layouts": [],
            "seatControl": { "yawReference": "World", "minPitch": -0.35, "maxPitch": 1.2 },
            "seatRig": {
              "name": "seatChase",
              "version": "puck.camera.program.v1",
              "operations": [
                { "$type": "orbit", "distance": 5.4626001, "yaw": 0, "pitch": 0.4145069, "pivotOffset": [ 0, 0, 0 ] },
                { "$type": "lookAt", "subject": { "$type": "reference" }, "targetOffset": [ 0, 1, 0 ], "worldAxes": false },
                { "$type": "fieldOfView", "fieldOfViewRadians": 0.9599311 }
              ]
            }
          },
          "state": {
            "world": [
              { "name": "tiles", "kind": "Fixed", "field": { "initial": 0, "min": 0, "max": 4, "heightScale": 0, "paint": [ { "$type": "draw", "generator": { "source": "WeightedNumeric", "mode": "RestartOnExhaustion", "weighted": [ { "value": 16384, "weight": 1, "multiplicity": 2 }, { "value": 32768, "weight": 1 }, { "value": 65536, "weight": 1 } ] } } ] }, "domain": { "$type": "cellsOf", "topology": "grid" } }
            ],
            "lattices": [
              { "$type": "field", "name": "grid", "origin": [ 0, 0, 0 ], "cellSize": 1, "width": 4, "depth": 1, "layers": 1, "stepEveryTicks": 1, "reactions": [] }
            ]
          },
          "patches": [
            { "name": "stinger", "source": "PATCH", "hash": "5126a83fc5816863b6ecf981ef238ef346653c028a589520d766ec4ab82260ca" }
          ]
        }
        """;

    // The products the pinned fixture's derivations produce at their current versions. A derivation whose product
    // moves is a deliberate change to it: bump the chunk's Version and re-record its pin here.
    private static readonly (string Code, uint Version, string Product)[] Pins = [
        ("DEFN", 1, "sha256-64/eba157373a1630f3"),
        ("ASST", 1, "sha256-64/1bc8ff1f5742445f"),
    ];
    private static readonly WorldMachineCatalog Catalog = TestHookInstaller.CreateMachineCatalog();

    // The patch file every world here names beside itself: a row's source resolves beside its document.
    internal const string PatchBeside = "patches/stinger.synth.json";

    private static string ShippedPatch() => PuckPaths.Shipped(relativePath: "Assets/worlds/patches/stinger.synth.json");

    internal static void WritePatchBeside(TemporaryDirectory directory) {
        if (!File.Exists(path: directory.PathOf(name: PatchBeside))) {
            _ = directory.WriteBytes(bytes: File.ReadAllBytes(path: ShippedPatch()), name: PatchBeside);
        }
    }
    internal static string WriteWorld(TemporaryDirectory directory, string patchSource) {
        WritePatchBeside(directory: directory);

        return directory.WriteBytes(
            bytes: Encoding.UTF8.GetBytes(s: World.Replace(
                newValue: patchSource.Replace(newChar: '/', oldChar: '\\'),
                oldValue: "PATCH"
            )),
            name: "law.world.json"
        );
    }
    internal static (WorldDefinitionAdmission Admission, CompiledWorldResolution Resolution, long Hits, long Derivations) Boot(string path, CompiledWorldCache cache) {
        var work = new WorldBootWork();
        var request = cache.For(
            catalogFingerprint: Catalog.CompositionFingerprint,
            documentPath: path
        );

        using (WorldBootWork.Attribute(work: work)) {
            Assert.True(
                condition: WorldDefinitionLoader.TryLoadFileForAdmission(
                    admission: out var admission,
                    contentHash: out _,
                    catalog: Catalog,
                    catalogFingerprint: Catalog.CompositionFingerprint,
                    compiled: request,
                    neighbours: new WorldFileNeighbourResolver(
                        baseDirectory: () => Path.GetDirectoryName(path: path)!,
                        catalog: Catalog,
                        catalogFingerprint: Catalog.CompositionFingerprint
                    ),
                    path: path,
                    reason: out var reason
                ),
                userMessage: reason
            );

            return (admission!, request.Resolution!, work.Read(kind: WorldBootWork.CompiledHits), work.Read(kind: WorldBootWork.ChunkDerivations));
        }
    }
    internal static byte[] Compile(string path, string? instanceIdentity = null, string? catalogFingerprint = null, CompiledWorldChunks? chunks = null) {
        Assert.True(
            condition: WorldSourceLoader.TryParseComposed(
                authored: out var authored,
                catalog: Catalog,
                catalogFingerprint: Catalog.CompositionFingerprint,
                document: File.ReadAllBytes(path: path),
                path: path,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: CompiledWorld.TryCompile(
                authored: authored,
                bytes: out var bytes,
                catalogFingerprint: (catalogFingerprint ?? Catalog.CompositionFingerprint),
                instanceIdentity: (instanceIdentity ?? WorldDefinitionLoader.BootInstanceName),
                reason: out reason,
                sourceName: path,
                chunks: chunks
            ),
            userMessage: reason
        );

        return bytes;
    }

    private static byte[] Canonical(WorldDefinitionAdmission admission) =>
        WorldDefinitionSerialization.Serialize(definition: admission.Definition);

    internal static ulong HashAfter(WorldDefinition definition, int ticks) {
        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var tick = 0; (tick < ticks); tick++) {
            fixture.Step();
        }

        return WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: ((ulong)ticks)
        );
    }

    private static string[] Codes(IEnumerable<ChunkCode> codes) => [.. codes.Select(selector: static code => code.ToString())];

    [Fact]
    public void AMissDerivesEveryChunkAndWritesTheCacheAndTheNextBootAdmitsTheSameWorldFromIt() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory, patchSource: PatchBeside);
        var cache = new CompiledWorldCache(directory: directory.PathOf(name: "state/compiled"));
        var fresh = Boot(cache: cache, path: path);

        Assert.Equal(actual: (fresh.Hits, fresh.Derivations), expected: (0L, 2L));
        Assert.Null(@object: fresh.Resolution.LoadedFrom);
        Assert.Equal(expected: ["DEFN", "ASST"], actual: Codes(codes: fresh.Resolution.Derived));
        Assert.Equal(expected: cache.FileFor(documentPath: path), actual: fresh.Resolution.WrittenTo);
        Assert.True(condition: File.Exists(path: cache.FileFor(documentPath: path)));

        var cached = Boot(cache: cache, path: path);

        Assert.Equal(actual: (cached.Hits, cached.Derivations), expected: (1L, 0L));
        Assert.Equal(expected: cache.FileFor(documentPath: path), actual: cached.Resolution.LoadedFrom);
        Assert.Equal(expected: ["DEFN", "ASST"], actual: Codes(codes: cached.Resolution.Kept));
        Assert.Null(@object: cached.Resolution.WrittenTo);
        Assert.Equal(expected: Canonical(admission: fresh.Admission), actual: Canonical(admission: cached.Admission));
        Assert.Equal(
            expected: HashAfter(definition: fresh.Admission.Definition, ticks: 30),
            actual: HashAfter(definition: cached.Admission.Definition, ticks: 30)
        );
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

            Assert.Equal(actual: (boot.Hits, boot.Derivations), expected: (0L, 2L));
            Assert.Null(@object: boot.Resolution.LoadedFrom);
            Assert.Equal(expected: valid, actual: File.ReadAllBytes(path: cache.FileFor(documentPath: path)));
        }

        // The control: the same file with the boot's own header is taken.
        File.WriteAllBytes(bytes: valid, path: beside);
        Assert.Equal(expected: 1L, actual: Boot(cache: new CompiledWorldCache(directory: directory.PathOf(name: "state-control")), path: path).Hits);
    }
    [Fact]
    public void AChunkWhoseVersionMovedIsRederivedAndTheRestKept() {
        using var directory = new TemporaryDirectory();
        var path = WriteWorld(directory: directory, patchSource: PatchBeside);
        var cache = new CompiledWorldCache(directory: directory.PathOf(name: "state/compiled"));
        var valid = Compile(path: path);

        Assert.True(condition: CompiledWorld.TryDecode(container: out var container, content: valid, header: out var header, reason: out var reason), userMessage: reason);
        File.WriteAllBytes(
            bytes: CompiledWorld.Encode(
                chunks: [.. container.Chunks.Select(selector: static chunk => ((chunk.Code == AssetChunk.Instance.Code)
                    ? new ContainerChunk(code: chunk.Code, inputs: chunk.Inputs, payload: chunk.Payload, version: (chunk.Version + 1))
                    : chunk))],
                header: header
            ),
            path: CompiledWorld.Beside(documentPath: path)
        );

        var boot = Boot(cache: cache, path: path);

        Assert.Equal(actual: (boot.Hits, boot.Derivations), expected: (1L, 1L));
        Assert.Equal(expected: ["DEFN"], actual: Codes(codes: boot.Resolution.Kept));
        Assert.Equal(expected: ["ASST"], actual: Codes(codes: boot.Resolution.Derived));
        Assert.Equal(expected: valid, actual: File.ReadAllBytes(path: cache.FileFor(documentPath: path)));
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
        Assert.Equal(expected: ["DEFN"], actual: Codes(codes: boot.Resolution.Kept));
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
                    ? new ContainerChunk(code: chunk.Code, inputs: chunk.Inputs, payload: chunk.Payload, version: (chunk.Version + 1))
                    : chunk))],
                header: header
            ),
            path: CompiledWorld.Beside(documentPath: path)
        );

        var boot = Boot(cache: cache, path: path);

        Assert.Equal(expected: ["DEFN"], actual: Codes(codes: boot.Resolution.Kept));
        Assert.Equal(expected: ["ASST", "DOCI"], actual: Codes(codes: boot.Resolution.Derived));
        Assert.Equal(expected: valid, actual: File.ReadAllBytes(path: cache.FileFor(documentPath: path)));
    }
    [Fact]
    public void EachDerivationsProductIsPinnedToItsVersion() {
        using var directory = new TemporaryDirectory();
        // A relative source no directory carries: the pin reads no file, so only the derivation can move it.
        var path = WriteWorld(directory: directory, patchSource: "compiled-world-law/absent.synth.json");

        Assert.True(condition: CompiledWorld.TryDecode(container: out var container, content: Compile(path: path), header: out _, reason: out var reason), userMessage: reason);

        var derived = container.Chunks.Select(selector: static chunk => (chunk.Code.ToString(), chunk.Version, chunk.Hash.ToString())).ToArray();

        Assert.Equal(expected: Codes(codes: CompiledWorldChunks.Standard.Select(selector: static chunk => chunk.Code)), actual: derived.Select(selector: static chunk => chunk.Item1));
        foreach (var (code, version, product) in Pins) {
            var actual = derived.Single(predicate: chunk => (chunk.Item1 == code));

            Assert.True(
                condition: ((actual.Version != version) || (actual.Item3 == product)),
                userMessage: $"{code}'s product at version {version} is {actual.Item3}, pinned as {product}: a deliberate change to the derivation bumps its Version and re-records this pin."
            );
            Assert.True(
                condition: (actual.Version == version),
                userMessage: $"{code} is at version {actual.Version}; re-record its pin at that version."
            );
        }
    }
    [Fact]
    public void EveryShippedWorldTheBuildCompiledBootsFromItsCompiledWorld() {
        var catalog = RepositoryPaths.Resolve(relativePath: "src/Puck.World/bin/Release/net10.0/Assets/worlds");
        var compiled = Directory.EnumerateFiles(path: catalog, searchOption: SearchOption.AllDirectories, searchPattern: ("*" + CompiledWorld.Extension)).Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.Contains(collection: compiled, filter: static file => file.EndsWith(comparisonType: StringComparison.Ordinal, value: $"puck{CompiledWorld.Extension}"));

        using var directory = new TemporaryDirectory();

        foreach (var file in compiled) {
            var document = (file[..^CompiledWorld.Extension.Length] + WorldDocumentName.DocumentSuffix);
            var boot = Boot(cache: new CompiledWorldCache(directory: directory.PathOf(name: Guid.NewGuid().ToString(format: "N"))), path: document);

            Assert.True(condition: ((boot.Hits == 1L) && (boot.Derivations == 0L)), userMessage: $"{document}: {boot.Resolution.Describe()}");
            Assert.Equal(expected: Path.GetFullPath(path: file), actual: boot.Resolution.LoadedFrom);
        }
    }
}

// A chunk a later package might register: the drawn document's id, read after ASST so it follows ASST's derivation.
internal sealed class DocumentIdChunk : ICompiledWorldChunk {
    private DocumentIdChunk() { }

    public static DocumentIdChunk Instance { get; } = new();

    public ChunkCode Code { get; } = ChunkCode.Parse(text: "DOCI");
    public IReadOnlyList<ChunkCode> DependsOn { get; } = [AssetChunk.Instance.Code];

    public bool DerivesOnBoot => true;
    public uint Version => 1;

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
