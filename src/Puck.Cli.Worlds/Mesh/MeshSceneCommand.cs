using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using Puck.SdfVm;
using Puck.World;
using Puck.World.Client;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Mesh;

/// <summary>
/// <c>puck mesh scene --world &lt;path&gt;</c>: times the irradiance volume's trace scene (<see cref="SdfMeshIrradianceScene"/>)
/// over the static meshes a World stored for a world, with no device: a build from nothing, a trace of probe rays about one
/// draw, then the scene updated for a change of that draw alone (its mesh replaced by new geometry, then the draw moved,
/// then the original draws back) and traced again. Exit codes: 0 timed; 1 loading failed, or no static group had a stored
/// mesh; 2 a usage error.
/// </summary>
public static class MeshSceneCommand {
    private const string Verb = "mesh scene";
    // The probes the trace takes about the changed draw: a High-tier window (48 x 16 x 48 cells of 1 m), each tracing as
    // many rays as a High-tier probe as far as the debug world's far distance.
    private const int Rays = 128;
    private const float Reach = 200f;
    private static readonly (int X, int Y, int Z) Window = (48, 16, 48);

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
            description: "Times the irradiance volume's CPU trace scene over a world's stored static meshes: a full build, a trace of probe rays, and the update for a change of one draw (new geometry, a move, the original back), with the geometries each update built and reused. Reads the store; never writes it.",
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
        var extension = backend switch {
            "directx" => ".dxil",
            "vulkan" => ".spv",
            _ => null,
        };

        if (extension is null) {
            Console.Error.WriteLine(value: $"{Verb}: unknown --backend '{backend}'; expected directx or vulkan.");
            return 2;
        }

        var fullPath = Path.GetFullPath(path: world);

        // A world's document parses masks and bindings through the Schema hooks, which the CLI installs per invocation.
        _ = CliWorldVocabulary.EnsureInstalled();

        if (!WorldDefinitionLoader.TryLoadFileForAdmission(admission: out var admission, contentHash: out _, documents: PuckDocumentComposer.Instance, path: fullPath, proveNeighbours: false, reason: out var reason)) {
            Console.Error.WriteLine(value: $"{Verb}: '{fullPath}' failed to load — {reason}");
            return 1;
        }

        var buildDirectory = Path.GetFullPath(path: (build ?? AppContext.BaseDirectory));

        if (!MeshCommand.TryIdentity(build: build, buildDirectory: buildDirectory, extension: extension, identity: out var identity, named: identityText, verb: Verb)) { return 2; }

        var definition = admission!.Definition;
        var meshes = Path.GetFullPath(path: ((cacheRoot is null) ? Puck.Abstractions.PuckUserDirectory.Resolve(name: "meshes") : Path.Join(path1: cacheRoot, path2: "meshes")));
        var scene = RestScene.Compose(definition: definition, sourcePath: fullPath);
        var groups = SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: WorldRenderFarDistance.Resolve(defaults: definition.Render), program: scene.Program, transforms: scene.Transforms);
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
            draws.Add(item: new SdfMeshDraw(Identity: group, Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity));
            labels.Add(item: scene.Label(group: group, owner: null));
        }
        if (draws.Count == 0) {
            Console.Error.WriteLine(value: $"{Verb}: the store {meshes} holds no static mesh of the world under bake identity {identity:x32}. Boot the world once with the build whose meshes to read, then name its directory with --build.");
            return 1;
        }

        var changed = -1;

        for (var index = 0; (index < draws.Count); index++) {
            var selected = ((change is null)
                ? ((changed < 0) || (draws[index].Mesh.TriangleCount > draws[changed].Mesh.TriangleCount))
                : (labels[index].Contains(value: change, comparisonType: StringComparison.Ordinal) && ((changed < 0) || (draws[index].Mesh.TriangleCount > draws[changed].Mesh.TriangleCount))));

            if (selected) { changed = index; }
        }
        if (changed < 0) {
            Console.Error.WriteLine(value: $"{Verb}: no static group with a stored mesh has a label holding '{change}'.");
            return 2;
        }

        var target = draws[changed];
        var copy = new SdfMesh(indices: target.Mesh.Indices.ToArray(), normals: target.Mesh.Normals.ToArray(), positions: target.Mesh.Positions.ToArray(), triangleMaterials: target.Mesh.TriangleMaterials.ToArray());
        var replaced = new List<SdfMeshDraw>(collection: draws) { [changed] = target with { Mesh = copy } };
        var moved = new List<SdfMeshDraw>(collection: draws) { [changed] = target with { ObjectToWorld = (target.ObjectToWorld * Matrix4x4.CreateTranslation(position: new Vector3(x: 0f, y: 0.001f, z: 0f))) } };
        var center = Center(mesh: target.Mesh);
        var output = Console.Out;
        var triangles = draws.Sum(selector: static draw => (long)draw.Mesh.TriangleCount);

        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"mesh scene: {CliPaths.ToDisplay(fullPath: fullPath)}, {draws.Count} static draw(s) with stored meshes ({missing} without), {triangles} triangles; store {meshes}, bake identity {identity:x32}"));
        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  change: {labels[changed]}, {target.Mesh.TriangleCount} triangles, probes about ({center.X:0.0}, {center.Y:0.0}, {center.Z:0.0})"));

        var full = Timed(label: "build", build: () => SdfMeshIrradianceScene.Build(draws: draws), output: output);

        Trace(scene: full, center: center, label: "trace", output: output);

        var swapped = Timed(label: "new geometry", build: () => SdfMeshIrradianceScene.Build(draws: replaced, previous: full), output: output);

        Trace(scene: swapped, center: center, label: "trace", output: output);

        var shifted = Timed(label: "move", build: () => SdfMeshIrradianceScene.Build(draws: moved, previous: swapped), output: output);
        var back = Timed(label: "back", build: () => SdfMeshIrradianceScene.Build(draws: draws, previous: shifted), output: output);

        Trace(scene: back, center: center, label: "trace", output: output);
        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  working set: {(Environment.WorkingSet / (1 << 20))} MiB, peak {(Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20))} MiB"));
        return 0;
    }
    private static SdfMeshIrradianceScene Timed(string label, Func<SdfMeshIrradianceScene?> build, TextWriter output) {
        var cpu = Processor();
        var clock = Stopwatch.StartNew();
        var scene = build()!;

        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {label}: seconds={clock.Elapsed.TotalSeconds:0.000} cpu-seconds={(Processor() - cpu):0.000} triangles={scene.TriangleCount} geometries={scene.GeometryCount} built={scene.GeometriesBuilt} built-triangles={scene.TrianglesBuilt} reused={scene.GeometriesReused} retained-triangles={scene.TrianglesRetained}"));
        return scene;
    }
    // One sweep of a High-tier window's probes about a point, as the volume traces it: each probe's rays spread over the
    // sphere, and from each front-facing hit a shadow ray toward a sun. It reports the rays, the hits, the mean hit
    // distance and the lit hits, which two scenes of the same geometry answer alike.
    private static void Trace(SdfMeshIrradianceScene scene, Vector3 center, string label, TextWriter output) {
        var low = new Vector3(x: (MathF.Floor(center.X) - (Window.X / 2)), y: (MathF.Floor(center.Y) - (Window.Y / 2)), z: (MathF.Floor(center.Z) - (Window.Z / 2)));
        var probes = new List<Vector3>();
        var sun = Vector3.Normalize(value: new Vector3(x: 0.4f, y: 0.8f, z: 0.3f));

        for (var z = 0; (z < Window.Z); z++) {
            for (var y = 0; (y < Window.Y); y++) {
                for (var x = 0; (x < Window.X); x++) {
                    probes.Add(item: (low + new Vector3(x: (x + 0.5f), y: (y + 0.5f), z: (z + 0.5f))));
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
                var direction = Fibonacci(index: ray, count: Rays);

                if (!scene.Intersect(origin: probes[probe], direction: direction, maximum: Reach, distance: out var distance, material: out _, normal: out var normal)) { continue; }
                hits[probe]++;
                distances[probe] += distance;
                if ((Vector3.Dot(vector1: normal, vector2: direction) < 0f) && scene.Unblocked(direction: sun, maximum: Reach, origin: ((probes[probe] + (direction * distance)) + (normal * 1.0e-3f)))) {
                    lit[probe]++;
                }
            }
        });

        var total = ((long)probes.Count * Rays);
        var hit = hits.Sum(selector: static count => (long)count);

        output.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {label}: probes={probes.Count} rays={total} hits={hit} lit={lit.Sum(selector: static count => (long)count)} mean-distance={((hit == 0) ? 0d : (distances.Sum() / hit)):0.000000} seconds={clock.Elapsed.TotalSeconds:0.000} cpu-seconds={(Processor() - cpu):0.000}"));
    }
    // The processor time the process has spent, in seconds: on a loaded machine a fairer measure of the work than the
    // wall clock.
    private static double Processor() {
        using var process = Process.GetCurrentProcess();

        return process.TotalProcessorTime.TotalSeconds;
    }
    private static Vector3 Center(SdfMesh mesh) {
        var low = new Vector3(value: float.PositiveInfinity);
        var high = new Vector3(value: float.NegativeInfinity);

        foreach (var position in mesh.Positions.Span) {
            low = Vector3.Min(value1: low, value2: position);
            high = Vector3.Max(value1: high, value2: position);
        }

        return ((low + high) * 0.5f);
    }
    // The index-th of count directions spread evenly over the sphere (a spherical Fibonacci lattice).
    private static Vector3 Fibonacci(int index, int count) {
        const float GoldenAngle = 2.39996323f;
        var z = (1f - (((2f * index) + 1f) / count));
        var radius = MathF.Sqrt(MathF.Max(0f, (1f - (z * z))));
        var angle = (index * GoldenAngle);

        return new Vector3(x: (radius * MathF.Cos(angle)), y: z, z: (radius * MathF.Sin(angle)));
    }
}
