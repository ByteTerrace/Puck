using System.CommandLine;
using System.Globalization;
using System.Numerics;
using System.Text;

using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.World;
using Puck.World.Client;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Mesh;

/// <summary>
/// <c>puck mesh audit --world &lt;path&gt;</c>: measures every baked mesh a World stored for a world against the field it was
/// baked from (<see cref="SdfMeshAudit"/>) and reports, per bake group, how far its vertices, centroids and edge midpoints
/// lie off the surface, how far its triangles' face and shading normals stray from the field's gradient, its folds, flips,
/// slivers and open edges, and the field's surface it leaves uncovered; then its worst clusters of flagged samples, each
/// at its rest-pose position with the shapes whose own surfaces lie nearest. The bake runs on the GPU alone, so the audit
/// reads the meshes a World boot stored (<see cref="SdfMeshStore"/>), under the bake identity of the build that booted it,
/// and never writes the store. Exit codes: 0 every selected group reported; 1 loading failed, or no selected group had a
/// stored mesh; 2 a usage error.
/// </summary>
public static class MeshCommand {
    private const string Verb = "mesh audit";
    // A stored mesh of another bake identity is a bake of a group's field when this share of its sampled vertices lies
    // within HistoryReach of that field.
    private const float HistoryShare = 0.99f;
    private const float HistoryReach = 0.001f;
    private const int HistorySamples = 512;

    /// <summary>Creates the <c>mesh</c> verb and its <c>audit</c> and <c>scene</c> subverbs.</summary>
    /// <returns>The verb.</returns>
    public static Command Create() => new(description: "Inspect the meshes the baked-mesh path stores.", name: "mesh") {
        AuditCommand(),
        MeshSceneCommand.Create(),
    };

    private static Command AuditCommand() {
        var worldOption = new Option<string>(name: "--world") { Description = "The world document whose meshes to audit.", Required = true };
        var prototypeOption = new Option<string?>(name: "--prototype") { Description = "Audit only the groups of bodies wearing this prototype's creation." };
        var groupOption = new Option<string?>(name: "--group") { Description = "Audit only the groups whose label holds this text, such as a shape's name (podBand1-left) or a slot (slot 6745)." };
        var cacheOption = new Option<string?>(name: "--cache-dir") { Description = "The device-cache root the World booted with (its --cache-dir); the meshes are its meshes subdirectory. Absent reads the per-user store (%LOCALAPPDATA%/Puck/meshes on Windows)." };
        var buildOption = new Option<string?>(name: "--build") { Description = "The directory of the World build whose bake stored the meshes (its Puck.World.dll's directory); the store keys each mesh by that build's bake identity. Absent uses this tool's own build." };
        var identityOption = new Option<string?>(name: "--identity") { Description = "The bake identity the meshes are stored under, as a report prints it (32 hex digits), in place of --build: a build since rebuilt keeps its stored meshes under the identity it had." };
        var backendOption = new Option<string>(name: "--backend") { DefaultValueFactory = static _ => "directx", Description = "The backend the World baked on, whose bake kernels key the meshes: directx or vulkan." };
        var topOption = new Option<int>(name: "--top") { DefaultValueFactory = static _ => 3, Description = "The clusters each group reports, worst first." };
        var worstOption = new Option<int>(name: "--worst") { DefaultValueFactory = static _ => 10, Description = "The clusters the closing list reports across every group, worst first." };
        var imagesOption = new Option<string?>(name: "--images") { Description = "A directory to write a PNG of each cluster of the closing list into: the mesh around its worst sample from three sides, shading normals beside face normals." };
        var historyOption = new Option<bool>(name: "--history") { Description = "Also audit, for each selected group, every mesh the store holds under any bake identity whose vertices lie on the group's field, oldest first: how earlier builds' bakes of the same geometry compare. Needs --group." };
        var cpuOption = new Option<bool>(name: "--cpu") { Description = "Bake each selected group offline on the CPU, the bake's own lattices answered through the field evaluator (MeshCpuBake), and audit that mesh in place of a stored one: a bake change measured with no device. Reads no store." };
        var command = new Command(
            description: "Measures each baked mesh a World stored for a world against the field it was baked from: off-surface error at vertices, centroids and edge midpoints, face and shading normal error against the field gradient, folds, flips, slivers, open edges and uncovered surface, then each group's worst defect clusters with their rest-pose positions and the shapes nearest them. Reads the store, or with --cpu bakes offline; never writes the store.",
            name: "audit"
        ) { worldOption, prototypeOption, groupOption, cacheOption, buildOption, identityOption, backendOption, topOption, worstOption, imagesOption, historyOption, cpuOption };

        command.SetAction(action: parseResult => Run(request: new AuditRequest(
            Backend: parseResult.GetValue(option: backendOption)!,
            Build: parseResult.GetValue(option: buildOption),
            Cpu: parseResult.GetValue(option: cpuOption),
            CacheRoot: parseResult.GetValue(option: cacheOption),
            Group: parseResult.GetValue(option: groupOption),
            History: parseResult.GetValue(option: historyOption),
            Identity: parseResult.GetValue(option: identityOption),
            Images: parseResult.GetValue(option: imagesOption),
            Prototype: parseResult.GetValue(option: prototypeOption),
            Top: parseResult.GetValue(option: topOption),
            World: parseResult.GetValue(option: worldOption)!,
            Worst: parseResult.GetValue(option: worstOption)
        )));

        return command;
    }
    private static int Run(AuditRequest request) {
        var extension = request.Backend switch {
            "directx" => ".dxil",
            "vulkan" => ".spv",
            _ => null,
        };

        if (extension is null) {
            Console.Error.WriteLine(value: $"{Verb}: unknown --backend '{request.Backend}'; expected directx or vulkan.");
            return 2;
        }
        if (request.History && (request.Group is null)) {
            Console.Error.WriteLine(value: $"{Verb}: --history reads every stored mesh for each group, so it needs --group to name the groups.");
            return 2;
        }

        var fullPath = Path.GetFullPath(path: request.World);

        if (!WorldDefinitionLoader.TryLoadFileForAdmission(admission: out var admission, contentHash: out _, documents: PuckDocumentComposer.Instance, path: fullPath, proveNeighbours: false, reason: out var reason)) {
            Console.Error.WriteLine(value: $"{Verb}: '{fullPath}' failed to load — {reason}");
            return 1;
        }

        var definition = admission!.Definition;
        var buildDirectory = Path.GetFullPath(path: (request.Build ?? AppContext.BaseDirectory));
        var meshes = Path.GetFullPath(path: ((request.CacheRoot is null) ? Puck.Abstractions.PuckUserDirectory.Resolve(name: "meshes") : Path.Join(path1: request.CacheRoot, path2: "meshes")));

        var identity = default(UInt128);

        if (!request.Cpu && !TryIdentity(build: request.Build, buildDirectory: buildDirectory, extension: extension, identity: out identity, named: request.Identity, verb: Verb)) { return 2; }

        var scene = RestScene.Compose(definition: definition, sourcePath: fullPath);
        var farDistance = WorldRenderFarDistance.Resolve(defaults: definition.Render);
        var groups = SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: farDistance, program: scene.Program, transforms: scene.Transforms);
        var store = new SdfMeshStore(directory: meshes);
        var options = new SdfMeshAuditOptions { Clusters = Math.Max(request.Top, request.Worst) };
        var results = new Outcome?[groups.Count];
        var selected = groups.Where(predicate: group => Selected(group: group, request: request, scene: scene)).ToArray();
        var baked = (request.Cpu
            ? MeshCpuBake.Bake(
                fields: selected
                    .Where(predicate: group => SdfMeshAudit.TryEvaluator(field: out _, program: SdfMeshAudit.Field(group: group, program: scene.Program), refusal: out _))
                    .ToDictionary(keySelector: static group => group.Selector, elementSelector: group => SdfMeshAudit.Field(group: group, program: scene.Program)),
                frame: new SdfFrame(Program: scene.Program, ProgramChanged: true, Time: 0f, Views: []) { DynamicTransforms = scene.Transforms, FarDistance = farDistance }
            )
            : null);

        _ = Parallel.For(fromInclusive: 0, toExclusive: groups.Count, body: ordinal => {
            var group = groups[ordinal];

            if (!selected.Contains(value: group)) { return; }
            results[ordinal] = Audit(baked: baked, group: group, options: options, owner: scene.Owner(group: group), scene: scene, store: store, identity: identity);
        });

        var audited = results.Count(predicate: static result => (result?.Report is not null));
        var stored = results.Count(predicate: static result => (result is { Stored: true }));
        var considered = results.Count(predicate: static result => (result is not null));
        var output = new StringBuilder();

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"mesh audit: {CliPaths.ToDisplay(fullPath: fullPath)}, {considered} group(s) of {groups.Count}: {audited} audited, {considered - stored} with no stored mesh, {stored - audited} refused\n");
        output.Append(value: (request.Cpu
            ? "  baked offline on the CPU through the field evaluator\n"
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"  store {meshes}, bake identity {identity:x32} ({((request.Identity is null) ? $"{request.Backend}, build {buildDirectory}" : "named")})\n")));

        var ordered = Enumerable.Range(start: 0, count: groups.Count)
            .Where(predicate: ordinal => (results[ordinal] is not null))
            .OrderByDescending(keySelector: ordinal => (results[ordinal]!.Report?.Score ?? -1f))
            .ThenBy(keySelector: static ordinal => ordinal)
            .ToArray();

        foreach (var ordinal in ordered) {
            Write(ordinal: ordinal, outcome: results[ordinal]!, output: output, top: request.Top);
            if (request.History && (results[ordinal]!.Field is { } field)) {
                History(field: field, group: groups[ordinal], options: options, output: output, store: store);
            }
        }

        WriteWorst(images: request.Images, ordered: ordered, output: output, results: results, worst: request.Worst);
        Console.Out.Write(value: output.ToString());

        if ((stored == 0) && request.Cpu) {
            Console.Error.WriteLine(value: $"{Verb}: the CPU bake found no surface in the selected groups.");
            return 1;
        }
        if (stored == 0) {
            Console.Error.WriteLine(value: $"{Verb}: the store holds no mesh of the selected groups under bake identity {identity:x32}. Boot the world once with the build whose meshes to audit (Puck.World --world {CliPaths.ToDisplay(fullPath: fullPath)}{((request.CacheRoot is null) ? string.Empty : $" --cache-dir {request.CacheRoot}")}) to bake and store them, then audit with --build naming its directory.");
            return 1;
        }

        return 0;
    }
    /// <summary>Resolves the bake identity stored meshes are read under: the one named, or the identity of the build in
    /// <paramref name="buildDirectory"/>; a refusal is written to the error stream under the verb's name.</summary>
    /// <param name="named">The identity named (32 hex digits), or <see langword="null"/> to read the build's.</param>
    /// <param name="build">The build directory named, which <paramref name="named"/> excludes.</param>
    /// <param name="buildDirectory">The build directory to read the identity of.</param>
    /// <param name="extension">The bytecode extension of the backend that baked.</param>
    /// <param name="verb">The verb a refusal names.</param>
    /// <param name="identity">The identity.</param>
    /// <returns><see langword="true"/> when the identity was resolved.</returns>
    internal static bool TryIdentity(string? named, string? build, string buildDirectory, string extension, string verb, out UInt128 identity) {
        if (named is not null) {
            if ((build is null) && UInt128.TryParse(s: named, style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out identity)) { return true; }

            Console.Error.WriteLine(value: $"{verb}: --identity takes 32 hex digits, and names the identity in place of --build.");
            identity = default;
            return false;
        }

        try {
            identity = SdfMeshStore.BuildIdentityOf(
                code: SdfMeshStore.CodeIdentityOf(directory: buildDirectory),
                kernels: SdfKernelSet.Load(bytecodeExtension: extension, directory: SdfKernelSet.PassesDirectory(tree: Path.Combine(buildDirectory, "Assets", "Shaders", "Sdf")))
            );
            return true;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) {
            Console.Error.WriteLine(value: $"{verb}: cannot read the bake identity of the build in '{buildDirectory}' — {exception.Message}");
            identity = default;
            return false;
        }
    }
    // Whether the request selects a group: by the prototype its body wears and by its label.
    private static bool Selected(SdfMeshBakeGroup group, RestScene scene, AuditRequest request) {
        var owner = scene.Owner(group: group);

        return ((request.Prototype is null) || ((owner is not null) && string.Equals(a: owner.Creation.Id, b: request.Prototype, comparisonType: StringComparison.Ordinal)))
            && ((request.Group is null) || scene.Label(group: group, owner: owner).Contains(value: request.Group, comparisonType: StringComparison.Ordinal));
    }
    // One group's audit: its stored mesh, or with baked its offline bake, measured against its field, or why it was not.
    private static Outcome Audit(SdfMeshBakeGroup group, RestScene scene, RestBody? owner, SdfMeshStore store, UInt128 identity, SdfMeshAuditOptions options, Dictionary<int, SdfMesh>? baked) {
        var label = scene.Label(group: group, owner: owner);
        var toWorld = scene.ToWorld(group: group);

        if (!SdfMeshAudit.TryEvaluator(field: out var field, program: SdfMeshAudit.Field(group: group, program: scene.Program), refusal: out var why)) {
            var held = ((baked is null) && store.TryPeek(geometry: group.Sealed, identity: identity, materials: out _, mesh: out _, refusal: out _));

            return new Outcome(Field: null, Label: label, Mesh: null, Owner: owner, Refusal: ((held || (baked is not null)) ? $"field {why}" : "no stored mesh"), Report: null, Stored: held, ToWorld: toWorld);
        }

        // A shape whose own field no evaluator reads cannot be named nearest; the group's field still holds it.
        var shapes = SdfMeshAudit.ShapeFields(group: group, program: scene.Program)
            .Select(selector: shape => (Name: scene.ShapeName(group: group, instruction: shape.Instruction), Field: (SdfMeshAudit.TryEvaluator(field: out var own, program: shape.Field, refusal: out _) ? own : null)))
            .Where(predicate: static shape => (shape.Field is not null))
            .Select(selector: static shape => (shape.Name, shape.Field!))
            .ToList();

        if (baked is not null) {
            var own = baked.GetValueOrDefault(key: group.Selector);

            return new Outcome(Field: field, Label: label, Mesh: own, Owner: owner, Refusal: ((own is null) ? "no surface baked" : null), Report: ((own is null) ? null : SdfMeshAudit.Audit(field: field, mesh: own, options: options, shapes: shapes)), Stored: (own is not null), ToWorld: toWorld);
        }
        if (!store.TryPeek(geometry: group.Sealed, identity: identity, materials: out _, mesh: out var mesh, refusal: out var refusal)) {
            return new Outcome(Field: field, Label: label, Mesh: null, Owner: owner, Refusal: (refusal ?? "no stored mesh"), Report: null, Stored: (refusal is not null), ToWorld: toWorld);
        }

        return new Outcome(Field: field, Label: label, Mesh: mesh, Owner: owner, Refusal: null, Report: SdfMeshAudit.Audit(field: field, mesh: mesh, options: options, shapes: shapes), Stored: true, ToWorld: toWorld);
    }
    // Every stored mesh of the group's materials whose vertices lie on its field, whatever identity stored it, oldest
    // first: the bakes of one geometry by each build that baked it.
    private static void History(SdfMeshBakeGroup group, SdfFieldEvaluator field, SdfMeshStore store, SdfMeshAuditOptions options, StringBuilder output) {
        var bakes = new List<(DateTime Written, string Name, SdfMeshAuditReport Report)>();

        foreach (var path in store.EntryPaths()) {
            if (!SdfMeshStore.TryReadFile(materials: out var materials, mesh: out var mesh, path: path) || !materials.AsSpan().SequenceEqual(other: group.MaterialOrder) || !LiesOn(field: field, mesh: mesh)) { continue; }
            bakes.Add(item: (File.GetCreationTimeUtc(path: path), Path.GetFileNameWithoutExtension(path: path), SdfMeshAudit.Audit(field: field, mesh: mesh, options: options, shapes: [])));
        }

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  history: {bakes.Count} stored bake(s) of this field\n");
        foreach (var (written, name, report) in bakes.OrderBy(keySelector: static bake => bake.Written).ThenBy(keySelector: static bake => bake.Name, comparer: StringComparer.Ordinal)) {
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"    {written:yyyy-MM-dd HH:mm}Z {name[..8]}: {report.Triangles} triangles, score {report.Score:0.0}, face p99 {report.NormalError.P99:0.0} over {report.NormalError.Over}, shading p99 {report.ShadingError.P99:0.0} over {report.ShadingError.Over}, loose {report.SlopeDeficit.Over}, {report.Folds} folds, {report.Flipped} flipped, off p99 {Millimetres(metres: report.CentroidError.P99)} mm, {report.MissingCrossings} uncovered\n");
        }
    }
    // Whether a mesh's vertices lie on a field: a sample of them spread over its vertex list, nearly all within reach.
    private static bool LiesOn(SdfMesh mesh, SdfFieldEvaluator field) {
        var positions = mesh.Positions.Span;

        if (positions.IsEmpty) { return false; }

        var step = Math.Max(1, (positions.Length / HistorySamples));
        var near = 0;
        var count = 0;

        for (var vertex = 0; (vertex < positions.Length); vertex += step) {
            count++;
            near += ((MathF.Abs(x: SdfMeshAudit.Distance(field: field, point: positions[vertex])) <= HistoryReach) ? 1 : 0);
        }

        return (near >= (HistoryShare * count));
    }
    private static void Write(int ordinal, Outcome outcome, StringBuilder output, int top) {
        if (outcome.Report is not { } report) {
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"group {ordinal} {outcome.Label}: {outcome.Refusal}\n");
            return;
        }

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"group {ordinal} {outcome.Label}: {report.Triangles} triangles, {report.Vertices} vertices, cell {Millimetres(metres: report.Cell)} mm, score {report.Score:0.0}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  off surface (mm): vertex {Statistic(statistic: report.VertexError, scale: 1000f)} | centroid {Statistic(statistic: report.CentroidError, scale: 1000f)} | midpoint {Statistic(statistic: report.MidpointError, scale: 1000f)}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  normal (deg): face {Statistic(statistic: report.NormalError, scale: 1f)} | shading {Statistic(statistic: report.ShadingError, scale: 1f)}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  field slope deficit: {Statistic(statistic: report.SlopeDeficit, scale: 1f)}{((report.LooseShapes.Count == 0) ? string.Empty : $"; loose near {string.Join(separator: ", ", values: report.LooseShapes.Select(selector: static shape => $"{shape.Name} {shape.Count}"))}")}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  topology: {report.Folds} folds, {report.Flipped} flipped, {report.Slivers} slivers, {report.OpenEdges} open edges, {report.NonManifoldEdges} non-manifold edges; surface: {report.Crossings} crossings, {report.MissingCrossings} uncovered\n");
        for (var rank = 0; (rank < Math.Min(top, report.Clusters.Count)); rank++) {
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"  cluster {rank + 1}: {Describe(cluster: report.Clusters[rank], outcome: outcome)}\n");
        }
    }
    // The worst clusters across every group, each with its image when a directory is named.
    private static void WriteWorst(Outcome?[] results, int[] ordered, int worst, string? images, StringBuilder output) {
        var clusters = ordered
            .Where(predicate: ordinal => (results[ordinal]!.Report is not null))
            .SelectMany(selector: ordinal => results[ordinal]!.Report!.Clusters.Select(selector: cluster => (Ordinal: ordinal, Cluster: cluster)))
            .OrderByDescending(keySelector: static entry => entry.Cluster.Score)
            .ThenBy(keySelector: static entry => entry.Ordinal)
            .Take(count: worst)
            .ToArray();

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"worst {clusters.Length} cluster(s):\n");
        for (var rank = 0; (rank < clusters.Length); rank++) {
            var (ordinal, cluster) = clusters[rank];
            var outcome = results[ordinal]!;

            output.Append(provider: CultureInfo.InvariantCulture, handler: $"  {rank + 1,2}. group {ordinal} {outcome.Label}: {Describe(cluster: cluster, outcome: outcome)}\n");
            if ((images is not null) && (outcome is { Mesh: { } mesh, Field: { } field, Report: { } report })) {
                var path = Path.Combine(path1: Path.GetFullPath(path: images), path2: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{rank + 1:00}-group{ordinal}.png"));

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
                // The window spans eight cells either way of the worst sample, so a few cells of the defect and the surface
                // beside it are seen.
                MeshAuditImage.Write(axis: cluster.Axis, center: cluster.Worst, halfWidth: Math.Clamp(value: (8f * report.Cell), min: 0.02f, max: 0.08f), mesh: mesh, path: path, toward: SdfMeshAudit.Gradient(field: field, point: cluster.Worst, step: 0.004f));
                output.Append(provider: CultureInfo.InvariantCulture, handler: $"      image {CliPaths.ToDisplay(fullPath: path)}\n");
            }
        }
    }
    private static string Describe(SdfMeshAuditCluster cluster, Outcome outcome) {
        var world = outcome.ToWorld(arg: cluster.Center);
        var builder = new StringBuilder();

        builder.Append(provider: CultureInfo.InvariantCulture, handler: $"score {cluster.Score:0.0} at {Point(point: world)}");
        if (outcome.Owner is { } owner) {
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $" (author {Point(point: owner.ToAuthor(world: world))})");
        }
        builder.Append(provider: CultureInfo.InvariantCulture, handler: $" extent {Millimetres(metres: cluster.Extent.X)}x{Millimetres(metres: cluster.Extent.Y)}x{Millimetres(metres: cluster.Extent.Z)} mm, {cluster.Samples} samples ({cluster.Off} off, {cluster.Turned} turned, {cluster.Shaded} shaded, {cluster.Folded} folded, {cluster.Missing} uncovered), worst {Millimetres(metres: cluster.WorstError)} mm / {cluster.WorstNormal:0} deg at {Point(point: outcome.ToWorld(arg: cluster.Worst))}");
        if (cluster.WorstFacing != Vector3.Zero) {
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $" facing {Direction(direction: cluster.WorstFacing)} against gradient {Direction(direction: cluster.WorstGradient)}");
        }
        if (cluster.Shapes.Count > 0) {
            builder.Append(value: "; nearest ");
            builder.AppendJoin(separator: ", ", values: cluster.Shapes.Select(selector: static shape => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{shape.Name} {Millimetres(metres: shape.Distance)} mm")));
        }

        return builder.ToString();
    }
    private static string Statistic(SdfMeshAuditStatistic statistic, float scale) =>
        string.Create(provider: CultureInfo.InvariantCulture, handler: $"p50 {statistic.P50 * scale:0.00} p99 {statistic.P99 * scale:0.00} max {statistic.Max * scale:0.00} over {statistic.Over}/{statistic.Count}");
    private static string Millimetres(float metres) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"{metres * 1000f:0.0}");
    private static string Point(Vector3 point) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"({point.X:0.000}, {point.Y:0.000}, {point.Z:0.000})");
    private static string Direction(Vector3 direction) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"({direction.X:0.00}, {direction.Y:0.00}, {direction.Z:0.00})");

    // What the verb was asked: the world, which groups, which store and identity, and what to report.
    private sealed record AuditRequest(string World, string? Prototype, string? Group, string? CacheRoot, string? Build, string? Identity, string Backend, int Top, int Worst, string? Images, bool History, bool Cpu);
    // A group's audit: its report, or why there is none, with its label, the map from its frame to world space, and the
    // mesh and fields it was measured with.
    private sealed record Outcome(string Label, string? Refusal, SdfMeshAuditReport? Report, bool Stored, Func<Vector3, Vector3> ToWorld, RestBody? Owner, SdfMesh? Mesh, SdfFieldEvaluator? Field);
}
