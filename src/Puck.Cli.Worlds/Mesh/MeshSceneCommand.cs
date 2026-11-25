using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.Cli.Mesh;

/// <summary>
/// <c>puck mesh scene --world &lt;path&gt;</c>: times the irradiance volume's trace scene (<see cref="SdfMeshIrradianceScene"/>)
/// over the static meshes a World stored for a world, with no device: a build from nothing, a trace of probe rays about one
/// draw, then the scene updated for a change of that draw alone (its mesh replaced by new geometry, then the draw moved,
/// then the original draws back, then moved again) and traced again. Each build prints the bytes it allocated, and the
/// first and the restored scene print their GPU export's size and the hash of the tree it holds. Exit codes: 0 timed;
/// 1 loading failed, or no static group had a stored mesh; 2 a usage error.
/// </summary>
public static class MeshSceneCommand {
    private const string Verb = "mesh scene";
    // The same finest High window and angular quadrature as the volume, over a fixed comparison reach.
    private const int Rays = SdfMeshProbeDepth.HitCount;
    private const float Reach = 200f;

    /// <summary>Creates the <c>scene</c> subverb.</summary>
    /// <returns>The subverb.</returns>
    public static Command Create() {
        var worldOption = new Option<string>(name: "--world") { Description = "The world document whose stored static meshes to build the scene of.", Required = true };
        var changeOption = new Option<string?>(name: "--change") { Description = "The static group to change, by text its label holds (a placement such as hall-slab-0); absent changes the group with the most triangles." };
        var cacheOption = new Option<string?>(name: "--cache-dir") { Description = "The device-cache root the World booted with (its --cache-dir); absent reads the per-user store." };
        var buildOption = new Option<string?>(name: "--build") { Description = "The directory of the World build whose bake stored the meshes; absent uses this tool's own build." };
        var identityOption = new Option<string?>(name: "--identity") { Description = "The bake identity the meshes are stored under (32 hex digits), in place of --build." };
        var backendOption = new Option<string>(name: "--backend") { DefaultValueFactory = static _ => "directx", Description = "The backend the World baked on: directx or vulkan." };
        var command = new Command(
            description: "Times the irradiance volume's CPU trace scene over a world's stored static meshes: a full build, a trace of probe rays, and the update for a change of one draw (new geometry, a move, the original back, a move again), with the geometries each update built and reused, the bytes each build allocated, and the GPU export's words and tree hash. Reads the store; never writes it.",
            name: "scene"
        ) { worldOption, changeOption, cacheOption, buildOption, identityOption, backendOption };

        command.SetAction(action: parseResult => Run(
            backend: parseResult.GetValue(option: backendOption)!,
            build: parseResult.GetValue(option: buildOption),
            cacheRoot: parseResult.GetValue(option: cacheOption),
            change: parseResult.GetValue(option: changeOption),
            identityText: parseResult.GetValue(option: identityOption),
            world: parseResult.GetValue(option: worldOption)!
        ));

        return command;
    }

    private static int Run(string world, string? change, string? cacheRoot, string? build, string? identityText, string backend) {
        if (!CliWorldDocuments.TryExtension(backend: backend, extension: out var extension, verb: Verb)) {
            return 2;
        }

        if (!CliWorldDocuments.TryLoad(definition: out var definition, fullPath: out var fullPath, path: world, verb: Verb)) {
            return CliExit.Refused;
        }

        var buildDirectory = Path.GetFullPath(path: (build ?? AppContext.BaseDirectory));

        if (!MeshCommand.TryIdentity(build: build, buildDirectory: buildDirectory, extension: extension, identity: out var identity, named: identityText, verb: Verb)) { return 2; }

        var meshes = CliWorldDocuments.MeshStore(cacheRoot: cacheRoot);
        var scene = RestScene.Compose(definition: definition, sourcePath: fullPath);
        var groups = scene.Plan(farDistance: WorldRenderFarDistance.Resolve(defaults: definition.Render));
        var store = new SdfMeshStore(directory: meshes);
        var draws = new List<SdfMeshDraw>();
        var labels = new List<string>();
        var missing = 0;

        foreach (var group in groups) {
            if (group.Dynamic || group.Glass) { continue; }
            if (!store.TryPeek(geometry: group.Sealed, identity: identity, materials: out _, mesh: out var mesh, refusal: out _)) {
                missing++;
                continue;
            }
            if (mesh is null) { continue; }
            draws.Add(item: new SdfMeshDraw(identity: group, material: 0, mesh: mesh, objectToWorld: Matrix4x4.Identity));
            labels.Add(item: scene.Label(group: group, owner: null));
        }
        if (draws.Count == 0) {
            return CliExit.Empty(verb: Verb, selection: CliPaths.ToDisplay(fullPath: meshes), why: $"holds no static mesh of the world under bake identity {identity:x32}: boot the world once with the build whose meshes to read, then name its directory with --build");
        }

        var changed = -1;

        for (var index = 0; (index < draws.Count); index++) {
            var selected = ((change is null)
                ? ((changed < 0) || (draws[index].Mesh.TriangleCount > draws[changed].Mesh.TriangleCount))
                : (labels[index].Contains(comparisonType: StringComparison.Ordinal, value: change) && ((changed < 0) || (draws[index].Mesh.TriangleCount > draws[changed].Mesh.TriangleCount))));

            if (selected) { changed = index; }
        }
        if (changed < 0) {
            Console.Error.WriteLine(value: $"{Verb}: no static group with a stored mesh has a label holding '{change}'.");
            return 2;
        }

        var target = draws[changed];
        SdfMesh copy;

        using (var held = target.Mesh.Hold()) {
            copy = new SdfMesh(indices: held.Indices.ToArray(), normals: held.Normals.ToArray(), positions: held.Positions.ToArray(), triangleMaterials: held.TriangleMaterials.ToArray());
        }
        var replaced = new List<SdfMeshDraw>(collection: draws) { [changed] = target with { Mesh = copy } };
        var moved = new List<SdfMeshDraw>(collection: draws) { [changed] = target.PlacedAt(objectToWorld: (target.FixedPlacement * Matrix4x4.CreateTranslation(position: new Vector3(x: 0f, y: 0.001f, z: 0f)))) };
        var center = ((target.Mesh.PositionLow + target.Mesh.PositionHigh) * 0.5f);
        var output = Console.Out;
        var triangles = draws.Sum(selector: static draw => ((long)draw.Mesh.TriangleCount));

        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"mesh scene: {CliPaths.ToDisplay(fullPath: fullPath)}, {draws.Count} static draw(s) with stored meshes ({missing} without), {triangles} triangles; store {meshes}, bake identity {identity:x32}"));
        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  change: {labels[changed]}, {target.Mesh.TriangleCount} triangles, probes about ({center.X:0.0}, {center.Y:0.0}, {center.Z:0.0})"));

        var full = Timed(label: "build", build: () => SdfMeshIrradianceScene.Build(draws: draws), output: output);

        Export(draws: draws, output: output, scene: full);
        Trace(center: center, label: "trace", output: output, scene: full);

        // Each scene is released once the next is built from it, as the volume releases the scene it replaces, so a later
        // build rents the arrays an earlier one returned.
        var swapped = Timed(label: "new geometry", build: () => SdfMeshIrradianceScene.Build(draws: replaced, previous: full), output: output);

        full.Dispose();
        Trace(center: center, label: "trace", output: output, scene: swapped);

        var shifted = Timed(label: "move", build: () => SdfMeshIrradianceScene.Build(draws: moved, previous: swapped), output: output);

        swapped.Dispose();

        var back = Timed(label: "back", build: () => SdfMeshIrradianceScene.Build(draws: draws, previous: shifted), output: output);

        shifted.Dispose();
        Export(draws: draws, output: output, scene: back);
        Trace(center: center, label: "trace", output: output, scene: back);

        var again = Timed(label: "move again", build: () => SdfMeshIrradianceScene.Build(draws: moved, previous: back), output: output);

        back.Dispose();
        again.Dispose();
        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  working set: {(Environment.WorkingSet / (1 << 20))} MiB, peak {(Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20))} MiB"));
        return 0;
    }
    // Allocated bytes are the whole process's, every thread's (the build is parallel), so they include the little the
    // runtime allocates meanwhile.
    private static SdfMeshIrradianceScene Timed(string label, Func<SdfMeshIrradianceScene?> build, TextWriter output) {
        var cpu = Processor();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var scene = build()!;
        var seconds = clock.Elapsed.TotalSeconds;

        allocated = (GC.GetTotalAllocatedBytes(precise: true) - allocated);
        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {label}: seconds={seconds:0.000} cpu-seconds={(Processor() - cpu):0.000} allocated-bytes={allocated} draws={scene.DrawCount} triangles={scene.TriangleCount} geometries={scene.GeometryCount} built={scene.GeometriesBuilt} built-triangles={scene.TrianglesBuilt} reused={scene.GeometriesReused} retained-triangles={scene.TrianglesRetained} top-depth={scene.TopDepth} geometry-depth={scene.GeometryDepth} median-splits={scene.MedianSplits} prefix-words={scene.PrefixWords} hierarchy-bytes={scene.HierarchyBytes}"));
        return scene;
    }
    // The scene's GPU export over a mesh region planned from the draws: its words, the bytes exporting it allocated beyond
    // the returned words themselves, and a hash of the tree it holds (TreeHash), which two builds that claim to export the
    // same scene must print alike.
    private static void Export(SdfMeshIrradianceScene scene, List<SdfMeshDraw> draws, TextWriter output) {
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);

        _ = SdfMeshRegion.Plan(draws: draws, meshes: placements);

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var words = scene.ExportGpu(placements: placements);

        allocated = (GC.GetTotalAllocatedBytes(precise: true) - allocated - ((long)words.Length * sizeof(uint)));
        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  export: words={words.Length} prefix-words={scene.PrefixWords} allocated-bytes-beyond-words={allocated} tree-hash={TreeHash(words: words):x16}"));
    }
    // The exported tree as the trace walks it from word zero's root, nearer child first, independent of where the export
    // placed each node: every node's bounds and leaf count, each top-level leaf's instance record, braid frame and the geometry subtree
    // it names, and each geometry leaf's triangle ordinals. A parallel hierarchy build numbers its nodes in whatever order
    // its tasks reach them, so the words themselves differ between two runs that build the same tree.
    private static ulong TreeHash(uint[] words) {
        var hash = Puck.Maths.Fnv1aHash.Create();

        if (words[0] != 0u) { Node(word: (int)words[0], top: true); }
        return hash.Value;

        // A top-level node holds float bounds; a geometry node its packed steps (SdfMeshProbeTrace.PackedNodeWords).
        void Node(int word, bool top) {
            var stride = (top ? SdfMeshProbeTrace.NodeWords : SdfMeshProbeTrace.PackedNodeWords);
            var link = words[(word + stride - 1)];
            var count = (int)(link & ((1u << SdfMeshProbeTrace.NodeCountBits) - 1u));
            var first = (int)(link >> SdfMeshProbeTrace.NodeCountBits);

            hash.Add(values: System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: words.AsSpan(start: word, length: (stride - 1))));
            hash.Add(value: (uint)count);
            if (count == 0) {
                Node(top: top, word: first);
                Node(top: top, word: (first + stride));
                return;
            }
            for (var leaf = 0; (leaf < count); leaf++) {
                if (!top) {
                    hash.Add(value: words[(first + leaf)]);
                    continue;
                }

                var entry = (first + (leaf * SdfMeshProbeTrace.EntryWords));

                hash.Add(values: System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: words.AsSpan(start: (int)words[entry], length: SdfMeshProbeTrace.InstanceWords)));
                hash.Add(values: System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: words.AsSpan(start: (int)words[(entry + 2)], length: SdfMeshProbeTrace.FrameWords)));
                Node(top: false, word: (int)words[(entry + 1)]);
            }
        }
    }
    // One sweep of a High-tier window's probes about a point, as the volume traces it: each probe's rays spread over the
    // sphere, and from each front-facing hit a shadow ray toward a sun. It reports the rays, the hits, the mean hit
    // distance and the lit hits, which two scenes of the same geometry answer alike.
    private static void Trace(SdfMeshIrradianceScene scene, Vector3 center, string label, TextWriter output) {
        // Read here rather than in a static field, so creating the command for help initializes nothing from Puck.SdfVm.
        var window = SdfMeshIrradiance.LevelsOf(SdfIndirectTier.High)[0];
        var dims = window.Dims;
        var cell = center / window.Spacing;
        var low = new Vector3(x: (MathF.Floor(x: cell.X) - (dims.X / 2)), y: (MathF.Floor(x: cell.Y) - (dims.Y / 2)), z: (MathF.Floor(x: cell.Z) - (dims.Z / 2)));
        var probes = new List<Vector3>();
        var sun = Vector3.Normalize(value: new Vector3(x: 0.4f, y: 0.8f, z: 0.3f));

        for (var z = 0; (z < dims.Z); z++) {
            for (var y = 0; (y < dims.Y); y++) {
                for (var x = 0; (x < dims.X); x++) {
                    probes.Add(item: ((low + new Vector3(x: (x + 0.5f), y: (y + 0.5f), z: (z + 0.5f))) * window.Spacing));
                }
            }
        }

        var hits = new int[probes.Count];
        var lit = new int[probes.Count];
        var distances = new double[probes.Count];
        var cpu = Processor();
        var clock = Stopwatch.StartNew();

        _ = Parallel.For(fromInclusive: 0, toExclusive: probes.Count, body: probe => {
            for (var ray = 0; (ray < Rays); ray++) {
                var direction = SdfMeshProbeDepth.RayDirections[ray];

                if (!scene.Intersect(origin: probes[probe], direction: direction, maximum: Reach, distance: out var distance, material: out _, normal: out var normal)) { continue; }
                hits[probe]++;
                distances[probe] += distance;
                if ((Vector3.Dot(vector1: normal, vector2: direction) < 0f) && scene.Unblocked(direction: sun, maximum: Reach, origin: ((probes[probe] + (direction * distance)) + (normal * 1.0e-3f)))) {
                    lit[probe]++;
                }
            }
        });

        var total = (((long)probes.Count) * Rays);
        var hit = hits.Sum(selector: static count => ((long)count));

        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {label}: probes={probes.Count} rays={total} hits={hit} lit={lit.Sum(selector: static count => ((long)count))} mean-distance={((hit == 0) ? 0d : (distances.Sum() / hit)):0.000000} seconds={clock.Elapsed.TotalSeconds:0.000} cpu-seconds={(Processor() - cpu):0.000}"));
    }
    // The processor time the process has spent, in seconds: on a loaded machine a fairer measure of the work than the
    // wall clock.
    private static double Processor() {
        using var process = Process.GetCurrentProcess();

        return process.TotalProcessorTime.TotalSeconds;
    }
}
