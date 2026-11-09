using System.CommandLine;
using System.Globalization;
using System.Numerics;
using System.Text;

using Puck.Cli.Host;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.World.Client;

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

    /// <summary>Creates the <c>mesh</c> verb and its <c>audit</c>, <c>preview</c>, <c>scene</c> and <c>identity</c> subverbs.</summary>
    /// <returns>The verb.</returns>
    public static Command Create() => new(description: "Inspect the meshes the baked-mesh path stores.", name: "mesh") {
        AuditCommand(),
        MeshPreviewCommand.Create(),
        MeshSceneCommand.Create(),
        MeshIdentityCommand.Create(),
    };

    private static Command AuditCommand() {
        var worldOption = new Option<string>(name: "--world") { Description = "The world document whose meshes to audit.", Required = true };
        var prototypeOption = new Option<string?>(name: "--prototype") { Description = "Audit this prototype's groups, from bodies and static or animated placements." };
        var groupOption = new Option<string?>(name: "--group") { Description = "Audit only the groups whose label holds this text, such as a shape's name (podBand1-left) or a slot (slot 6745)." };
        var cacheOption = new Option<string?>(name: "--cache-dir") { Description = "The device-cache root the World booted with (its --cache-dir); the meshes are its meshes subdirectory. Absent reads the per-user store (%LOCALAPPDATA%/Puck/meshes on Windows)." };
        var buildOption = new Option<string?>(name: "--build") { Description = "The directory of the World build whose bake stored the meshes (its Puck.World.dll's directory); the store keys each mesh by that build's bake identity. Absent uses this tool's own build." };
        var identityOption = new Option<string?>(name: "--identity") { Description = "The bake identity the meshes are stored under, as a report prints it (32 hex digits), in place of --build: a build since rebuilt keeps its stored meshes under the identity it had." };
        var backendOption = new Option<string>(name: "--backend") { DefaultValueFactory = static _ => "directx", Description = "The backend the World baked on, whose bake kernels key the meshes: directx or vulkan." };
        var topOption = CliOptions.Count(defaultValue: 3, description: "The clusters each group reports, worst first.", minimum: 1, name: "--top");
        var worstOption = CliOptions.Count(defaultValue: 10, description: "The clusters the closing list reports across every group, worst first.", minimum: 0, name: "--worst");
        var imagesOption = new Option<string?>(name: "--images") { Description = "A directory to write a PNG of each cluster of the closing list into: the mesh around its worst sample from three sides, shading normals beside face normals." };
        var historyOption = new Option<bool>(name: "--history") { Description = "Also audit, for each selected group, every mesh the store holds under any bake identity whose vertices lie on the group's field, oldest first: how earlier builds' bakes of the same geometry compare. Needs --group." };
        var cpuOption = new Option<bool>(name: "--cpu") { Description = "Bake each selected group offline on the CPU, the bake's own lattices answered through the field evaluator (MeshCpuBake), and audit that mesh in place of a stored one: a bake change measured with no device. Reads no store." };
        var poseOption = new Option<string?>("--pose") { Description = "Audit a skin against its posed field: slotOffset=tx,ty,tz,qx,qy,qz,qw; repeat separated by semicolons. Transforms are in the group's frame. Unnamed slots hold rest." };
        var levelOption = CliOptions.Count(defaultValue: 0, description: "Measure this rendered level, clamped to each piece's last available level. Keeps level zero's error thresholds and coverage lattice.", minimum: 0, name: "--level");
        var fieldLevelsOption = new Option<bool>(name: "--field-levels") { Description = "With --cpu, also contour each group's field levels (cells of twice the previous, offset outward by half a cell, per member where its shapes ride several slots, to an empty level) and measure them and the collapse's levels by the error contract: both geometric directions, enclosure, material and normal runs, vanished parts, open edges, flips and missing crossings, field evaluations and each path's own processor work." };
        var aimOption = new Option<string>(name: "--field-aim") { DefaultValueFactory = static _ => "local", Description = "How --field-levels converts a level's offset to field units to aim its contour: local, by each sample's own slope, or lattice, by the lattice's median slope. The oracle measures where either landed." };
        aimOption.AcceptOnlyFromAmong("local", "lattice");
        var command = new Command(
            description: "Measures each baked mesh a World stored for a world against the field it was baked from: off-surface error at vertices, centroids and edge midpoints, face and shading normal error against the field gradient, smooth vertices' shading normals against the gradient at the vertex (rough), folds, flips, slivers, open edges and uncovered surface, then each group's worst defect clusters with their rest-pose positions and the shapes nearest them. Reads the store, or with --cpu bakes offline; never writes the store.",
            name: "audit"
        ) { worldOption, prototypeOption, groupOption, cacheOption, buildOption, identityOption, backendOption, topOption, worstOption, imagesOption, historyOption, cpuOption, poseOption, levelOption, fieldLevelsOption, aimOption };

        command.SetAction(action: parseResult => Run(request: new AuditRequest(
            Backend: parseResult.GetValue(option: backendOption)!,
            Build: parseResult.GetValue(option: buildOption),
            Cpu: parseResult.GetValue(option: cpuOption),
            Pose: parseResult.GetValue(poseOption),
            FieldLevels: parseResult.GetValue(option: fieldLevelsOption),
            LocalAim: (parseResult.GetValue(option: aimOption) == "local"),
            CacheRoot: parseResult.GetValue(option: cacheOption),
            Group: parseResult.GetValue(option: groupOption),
            History: parseResult.GetValue(option: historyOption),
            Identity: parseResult.GetValue(option: identityOption),
            Images: parseResult.GetValue(option: imagesOption),
            Level: parseResult.GetValue(option: levelOption),
            Prototype: parseResult.GetValue(option: prototypeOption),
            Top: parseResult.GetValue(option: topOption),
            World: parseResult.GetValue(option: worldOption)!,
            Worst: parseResult.GetValue(option: worstOption)
        )));

        return command;
    }
    private static int Run(AuditRequest request) {
        var poses = new Dictionary<int, Puck.SignedDistance.DynamicTransform>();
        if (request.Pose is { } poseText) {
            try {
                foreach (var entry in poseText.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                    var pair = entry.Split('=');
                    var offset = int.Parse(pair[0], CultureInfo.InvariantCulture);
                    var values = pair[1].Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    if ((pair.Length != 2) || (values.Length != 7) || values.Any(v => !float.IsFinite(v))) { throw new FormatException(); }
                    var rotation = new Quaternion(values[3], values[4], values[5], values[6]);
                    if (!(rotation.LengthSquared() > 0f)) { throw new FormatException(); }
                    poses.Add(offset, new(new(values[0], values[1], values[2]), Quaternion.Normalize(rotation), Vector4.Zero));
                }
            } catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException) {
                Console.Error.WriteLine("mesh audit: --pose needs slotOffset=tx,ty,tz,qx,qy,qz,qw entries.");
                return 2;
            }
            if (request.History || request.FieldLevels) { Console.Error.WriteLine("mesh audit: --pose measures one skin, without --history or --field-levels."); return 2; }
        }
        if (!CliWorldDocuments.TryExtension(backend: request.Backend, extension: out var extension, verb: Verb)) {
            return 2;
        }
        if (request.History && (request.Group is null)) {
            Console.Error.WriteLine(value: $"{Verb}: --history reads every stored mesh for each group, so it needs --group to name the groups.");
            return 2;
        }

        if (request.Cpu && request.History) {
            Console.Error.WriteLine(value: $"{Verb}: --history reads the stored meshes of each group, and --cpu reads no stored bake.");
            return 2;
        }

        if (request.FieldLevels && (!request.Cpu || (request.Level > 0))) {
            Console.Error.WriteLine(value: $"{Verb}: --field-levels contours each group's field again offline, so it needs --cpu, and measures every level itself, so it takes no --level.");
            return 2;
        }

        if (request.Cpu && ((request.Build is not null) || (request.Identity is not null))) {
            Console.Error.WriteLine(value: $"{Verb}: --cpu derives meshes with this tool's code and reads no stored bake; --build and --identity select stored meshes instead.");
            return 2;
        }

        if (!CliWorldDocuments.TryLoad(definition: out var definition, fullPath: out var fullPath, path: request.World, verb: Verb)) {
            return CliExit.Refused;
        }

        var buildDirectory = Path.GetFullPath(path: (request.Build ?? AppContext.BaseDirectory));
        var meshes = CliWorldDocuments.MeshStore(cacheRoot: request.CacheRoot);

        var identity = default(UInt128);

        if (!request.Cpu && !TryIdentity(build: request.Build, buildDirectory: buildDirectory, extension: extension, identity: out identity, named: request.Identity, verb: Verb)) { return 2; }

        var scene = RestScene.Compose(definition: definition, sourcePath: fullPath);
        var farDistance = WorldRenderFarDistance.Resolve(defaults: definition.Render);
        var groups = scene.Plan(farDistance: farDistance);
        var store = new SdfMeshStore(directory: meshes);
        var options = new SdfMeshAuditOptions { Clusters = Math.Max(val1: request.Top, val2: request.Worst), Level = request.Level };
        var results = new Outcome?[groups.Count];
        var selected = groups.Where(predicate: group => Selected(group: group, request: request, scene: scene)).ToArray();
        var evaluations = new Dictionary<int, long>();
        var baked = (request.Cpu ? MeshCpuBake.Bake(evaluations: evaluations, farDistance: farDistance, groups: selected, scene: scene) : null);

        _ = Parallel.For(fromInclusive: 0, toExclusive: groups.Count, body: ordinal => {
            var group = groups[ordinal];

            if (!selected.Contains(value: group)) { return; }
            results[ordinal] = Audit(baked: baked, group: group, options: options, owner: scene.Owner(group: group), scene: scene, store: store, identity: identity, poses: poses);
        });

        var audited = results.Count(predicate: static result => (result?.Report is not null));
        var stored = results.Count(predicate: static result => (result is { Stored: true }));
        var considered = results.Count(predicate: static result => (result is not null));
        var output = new StringBuilder();

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"mesh audit: {CliPaths.ToDisplay(fullPath: fullPath)}, {considered} group(s) of {groups.Count}: {audited} audited, {(considered - stored)} with no stored mesh, {(stored - audited)} refused\n");
        if (request.Level > 0) { output.Append(provider: CultureInfo.InvariantCulture, handler: $"  rendered level {request.Level}, thresholds from level zero\n"); }
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

        WriteWorst(images: request.Images, level: request.Level, ordered: ordered, output: output, results: results, worst: request.Worst);
        if (request.FieldLevels) {
            FieldLevels(evaluations: evaluations, groups: groups, options: new SdfMeshFieldLevelOptions { LocalAim = request.LocalAim }, output: output, results: results, scene: scene);
        }
        Console.Out.Write(value: output.ToString());

        if ((stored == 0) && request.Cpu) {
            return CliExit.Empty(verb: Verb, selection: "the selected groups", why: "the CPU bake found no surface in them");
        }
        if (stored == 0) {
            return CliExit.Empty(verb: Verb, selection: "the selected groups", why: $"the store holds no mesh of them under bake identity {identity:x32}: boot the world once with the build whose meshes to audit (Puck.World --world {CliPaths.ToDisplay(fullPath: fullPath)}{((request.CacheRoot is null) ? string.Empty : $" --cache-dir {request.CacheRoot}")}), then audit with --build naming its directory");
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
            identity = MeshBuildIdentity.Read(buildDirectory: buildDirectory, extension: extension).Value;
            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)) {
            Console.Error.WriteLine(value: $"{verb}: cannot read the bake identity of the build in '{buildDirectory}' — {exception.Message}");
            identity = default;
            return false;
        }
    }
    // Selection uses emission identity, not the presence of a body or a parsed display label.
    private static bool Selected(SdfMeshBakeGroup group, RestScene scene, AuditRequest request) {
        var owner = scene.Owner(group: group);

        return (((request.Prototype is null) || string.Equals(a: scene.Prototype(group), b: request.Prototype, comparisonType: StringComparison.Ordinal))
            && ((request.Group is null) || scene.Label(group: group, owner: owner).Contains(value: request.Group, comparisonType: StringComparison.Ordinal)));
    }
    // One group's audit: its stored mesh, or with baked its offline bake, measured against its field, or why it was not.
    private static Outcome Audit(SdfMeshBakeGroup group, RestScene scene, RestBody? owner, SdfMeshStore store, UInt128 identity, SdfMeshAuditOptions options, Dictionary<int, SdfMesh>? baked, IReadOnlyDictionary<int, Puck.SignedDistance.DynamicTransform> poses) {
        var label = $"{scene.Label(group: group, owner: owner)} geometry={group.Sealed.Hash:x32}{(poses.Count > 0 ? " posed" : string.Empty)}";
        var toWorld = scene.ToWorld(group: group);

        if (!SdfMeshAudit.TryEvaluator(field: out var field, program: SdfMeshAudit.Field(group: group, program: scene.Program, poses: poses), refusal: out var why)) {
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
            if ((own is not null) && (poses.Count > 0)) { own = SdfMeshSkin.Pose(own, poses); }

            return new Outcome(Field: field, Label: label, Mesh: own, Owner: owner, Refusal: ((own is null) ? "no surface baked" : null), Report: ((own is null) ? null : SdfMeshAudit.Audit(field: field, mesh: own, options: options, shapes: shapes)), Stored: (own is not null), ToWorld: toWorld);
        }
        if (!store.TryPeek(geometry: group.Sealed, identity: identity, materials: out _, mesh: out var mesh, refusal: out var refusal)) {
            return new Outcome(Field: field, Label: label, Mesh: null, Owner: owner, Refusal: (refusal ?? "no stored mesh"), Report: null, Stored: (refusal is not null), ToWorld: toWorld);
        }
        if ((mesh is not null) && (poses.Count > 0)) { mesh = SdfMeshSkin.Pose(mesh, poses); }

        return new Outcome(Field: field, Label: label, Mesh: mesh, Owner: owner, Refusal: null, Report: ((mesh is null) ? null : SdfMeshAudit.Audit(field: field, mesh: mesh, options: options, shapes: shapes)), Stored: true, ToWorld: toWorld);
    }
    // Every stored mesh of the group's material-place count whose vertices lie on its field, whatever identity stored it, oldest
    // first: the bakes of one geometry by each build that baked it.
    private static void History(SdfMeshBakeGroup group, SdfFieldEvaluator field, SdfMeshStore store, SdfMeshAuditOptions options, StringBuilder output) {
        var bakes = new List<(DateTime Written, string Name, SdfMeshAuditReport Report)>();

        foreach (var path in store.EntryPaths()) {
            if (!SdfMeshStore.TryReadFile(materials: out var materials, mesh: out var mesh, path: path) || (mesh is null) || (materials.Length != group.MaterialOrder.Length) || !LiesOn(field: field, mesh: mesh)) { continue; }
            bakes.Add(item: (File.GetCreationTimeUtc(path: path), Path.GetFileNameWithoutExtension(path: path), SdfMeshAudit.Audit(field: field, mesh: mesh, options: options, shapes: [])));
        }

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  history: {bakes.Count} stored bake(s) of this field\n");
        foreach (var (written, name, report) in bakes.OrderBy(keySelector: static bake => bake.Written).ThenBy(keySelector: static bake => bake.Name, comparer: StringComparer.Ordinal)) {
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"    {written:yyyy-MM-dd HH:mm}Z {name[..8]}: {report.Triangles} triangles, score {report.Score:0.0}, face p99 {report.NormalError.P99:0.0} over {report.NormalError.Over}, shading p99 {report.ShadingError.P99:0.0} over {report.ShadingError.Over}, rough p99 {report.RoughError.P99:0.0} over {report.RoughError.Over}, loose {report.SlopeDeficit.Over}, {report.Folds} folds, {report.Flipped} flipped, off p99 {Millimetres(metres: report.CentroidError.P99)} mm, {report.MissingCrossings} uncovered\n");
        }
    }
    // Whether a mesh's vertices lie on a field: a sample of them spread over its vertex list, nearly all within reach.
    private static bool LiesOn(SdfMesh mesh, SdfFieldEvaluator field) {
        var positions = mesh.Positions.Span;

        if (positions.IsEmpty) { return false; }

        var step = Math.Max(val1: 1, val2: (positions.Length / HistorySamples));
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

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"group {ordinal} {outcome.Label}: {report.Triangles} triangles, {report.Vertices} vertices, cell {Millimetres(metres: report.Cell)} mm, score {report.Score:0.0}{((outcome.Mesh is { } mesh) ? $", box centre {Point(point: outcome.ToWorld(arg: ((mesh.PositionLow + mesh.PositionHigh) * 0.5f)))}" : string.Empty)}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  off surface (mm): vertex {Statistic(statistic: report.VertexError, scale: 1000f)} | centroid {Statistic(statistic: report.CentroidError, scale: 1000f)} | midpoint {Statistic(statistic: report.MidpointError, scale: 1000f)}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  normal (deg): face {Statistic(statistic: report.NormalError, scale: 1f)} | shading {Statistic(statistic: report.ShadingError, scale: 1f)} | rough {Statistic(statistic: report.RoughError, scale: 1f)}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  field slope deficit: {Statistic(statistic: report.SlopeDeficit, scale: 1f)}{((report.LooseShapes.Count == 0) ? string.Empty : $"; loose near {string.Join(separator: ", ", values: report.LooseShapes.Select(selector: static shape => $"{shape.Name} {shape.Count}"))}")}\n");
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"  topology: {report.Folds} folds, {report.Flipped} flipped, {report.Slivers} slivers, {report.OpenEdges} open edges, {report.NonManifoldEdges} non-manifold edges; surface: {report.Crossings} crossings, {report.MissingCrossings} uncovered\n");
        if (outcome.Mesh is { } skin) {
            var influences = SdfMeshSkin.Audit(skin);
            output.Append(CultureInfo.InvariantCulture, $"  skin: {influences.Blended} blend-zone vertices, {influences.SplitPlaces} places with split tuples\n");
        }
        for (var rank = 0; (rank < Math.Min(val1: top, val2: report.Clusters.Count)); rank++) {
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"  cluster {(rank + 1)}: {Describe(cluster: report.Clusters[rank], outcome: outcome)}\n");
        }
    }
    // Each audited group's collapse levels and the field levels that follow them (MeshFieldLevels), in group order, then
    // the totals the plan's clauses read.
    private static void FieldLevels(IReadOnlyList<SdfMeshBakeGroup> groups, Outcome?[] results, RestScene scene, Dictionary<int, long> evaluations, SdfMeshFieldLevelOptions options, StringBuilder output) {
        var measured = new MeshFieldLevels.Group?[groups.Count];

        _ = Parallel.For(fromInclusive: 0, toExclusive: groups.Count, body: ordinal => {
            if (results[ordinal] is not { Mesh: { } mesh, Report: { } report }) { return; }
            measured[ordinal] = MeshFieldLevels.Measure(bakeEvaluations: evaluations.GetValueOrDefault(key: groups[ordinal].Selector), group: groups[ordinal], mesh: mesh, options: options, reference: report, scene: scene);
        });

        var all = Enumerable.Range(start: 0, count: groups.Count).Where(predicate: ordinal => (measured[ordinal] is not null)).ToArray();

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels: {all.Length} group(s), offset {options.OffsetShare} of a level's cell aimed by {(options.LocalAim ? "each sample's slope" : "the lattice's median slope")}, near-coplanar faces flattened within {options.FlattenShare} of it, tolerance {options.ToleranceShare} of level zero's cell; each field level follows the collapse's coarsest\n");
        foreach (var ordinal in all) {
            MeshFieldLevels.Write(group: measured[ordinal]!, label: string.Create(provider: CultureInfo.InvariantCulture, handler: $"group {ordinal} {results[ordinal]!.Label}"), output: output);
        }

        var chosen = all.Select(selector: ordinal => measured[ordinal]!).ToArray();
        var collapseDeepest = chosen.Select(selector: static group => group.Collapse.Count).DefaultIfEmpty(defaultValue: 0).Max();
        var line = new StringBuilder();

        line.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels total: L0 {chosen.Sum(selector: static group => (long)group.Triangles)}");
        for (var level = 1; (level <= collapseDeepest); level++) {
            line.Append(provider: CultureInfo.InvariantCulture, handler: $" C{level} {chosen.Sum(selector: group => (long)((group.Collapse.Count == 0) ? group.Triangles : group.Collapse[(Math.Min(val1: level, val2: group.Collapse.Count) - 1)].Triangles))}");
        }
        output.Append(value: line.Append(value: '\n').ToString());

        var collapseFloor = chosen.Sum(selector: static group => (long)group.Follow.Triangles);
        var unlevelled = chosen.Count(predicate: static group => (group.Collapse.Count == 0));

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels floor: collapse coarsest {collapseFloor} ({unlevelled} group(s) with no collapse level, which level zero stands for)\n");

        foreach (var (name, verdicts) in new (string, Func<MeshFieldLevels.Group, IReadOnlyList<MeshFieldLevels.Verdict>>)[] { ("with normals", static group => group.Shaded), ("without normals", static group => group.Unshaded) }) {
            var depth = chosen.Select(selector: group => verdicts(arg: group).Count(predicate: static verdict => verdict.Kept)).DefaultIfEmpty(defaultValue: 0).Max();
            var appended = new StringBuilder();

            for (var at = 1; (at <= depth); at++) {
                appended.Append(provider: CultureInfo.InvariantCulture, handler: $" A{at} {chosen.Sum(selector: group => (long)MeshFieldLevels.Floor(group: group, verdicts: verdicts(arg: group).Where(predicate: static verdict => verdict.Kept).Take(count: at).ToArray()).Triangles)}");
            }

            var floor = chosen.Sum(selector: group => (long)MeshFieldLevels.Floor(group: group, verdicts: verdicts(arg: group)).Triangles);
            var gained = chosen.Count(predicate: group => verdicts(arg: group).Any(predicate: static verdict => verdict.Kept));
            var empty = chosen.Count(predicate: group => MeshFieldLevels.ReachesEmpty(verdicts: verdicts(arg: group)));
            var share = chosen.Select(selector: group => (double)(MeshFieldLevels.Floor(group: group, verdicts: verdicts(arg: group)).Error / group.HalfDiagonal)).Order().ToArray();
            var refusals = chosen.SelectMany(selector: group => verdicts(arg: group).Where(predicate: static verdict => (verdict.Measured && !verdict.Kept))).ToArray();
            var reasons = refusals.SelectMany(selector: static verdict => verdict.Reasons.Split(separator: ' ').Where(predicate: static word => char.IsLetter(c: word[0]) && (word != "mm"))).GroupBy(keySelector: static word => word).OrderBy(keySelector: static named => named.Key, comparer: StringComparer.Ordinal);

            output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels {name}: kept{appended} | coarsest nonempty {floor} ({((double)collapseFloor / Math.Max(val1: 1L, val2: floor)):0.0}x below the collapse's); {gained} group(s) gain a field level, {empty} run to the empty level unbroken; coarsest error over half-diagonal p50 {Quantile(sorted: share, at: 0.5):0.###} p90 {Quantile(sorted: share, at: 0.9):0.###}\n");
            output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels {name} refusals: {refusals.Length} level(s); {string.Join(separator: ", ", values: reasons.Select(selector: static named => $"{named.Key} {named.Count()}"))}\n");
        }

        // What each binding clause rejects: the floor were it left out, a diagnosis rather than a judgement (a level with
        // refused witnesses has no bound, and one with surface outside it no enclosure).
        var waivers = new StringBuilder();

        foreach (var (normals, waived) in new (bool, string[])[] { (false, ["refused"]), (false, ["outside"]), (false, ["refused", "outside"]), (true, ["refused", "outside"]), (true, ["refused", "outside", "dominated"]) }) {
            var floor = chosen.Sum(selector: group => (long)MeshFieldLevels.Floor(group: group, verdicts: MeshFieldLevels.Judge(group: group, normals: normals, waived: waived)).Triangles);

            waivers.Append(provider: CultureInfo.InvariantCulture, handler: $"{((waivers.Length == 0) ? string.Empty : "; ")}{(normals ? "with" : "without")} normals less {string.Join(separator: "+", values: waived)} {floor} ({((double)collapseFloor / Math.Max(val1: 1L, val2: floor)):0.0}x)");
        }
        output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels waived: {waivers}\n");

        var rows = chosen.SelectMany(selector: static group => group.Field).ToArray();
        var binds = rows.Where(predicate: static row => (row.Error is not null)).Select(selector: static row => MeshFieldLevels.Binds(error: row.Error!)).GroupBy(keySelector: static name => name).OrderBy(keySelector: static named => named.Key, comparer: StringComparer.Ordinal);

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels binding: {string.Join(separator: ", ", values: binds.Select(selector: static named => $"{named.Key} {named.Count()}"))}; vanished {rows.Sum(selector: static row => row.Vanished)} component(s), {rows.Sum(selector: static row => (long)(row.Whole - row.Triangles))} triangle(s)\n");

        var bakeEvaluations = chosen.Sum(selector: static group => group.BakeEvaluations);
        var contour = chosen.Sum(selector: static group => group.ContourEvaluations);
        var oracle = chosen.Sum(selector: static group => group.OracleEvaluations);
        var contourCycles = chosen.Aggregate(seed: 0UL, func: static (sum, group) => (sum + group.ContourCycles));
        var oracleCycles = chosen.Aggregate(seed: 0UL, func: static (sum, group) => (sum + group.OracleCycles));
        var collapseCycles = chosen.Aggregate(seed: 0UL, func: static (sum, group) => (sum + group.CollapseCycles));

        output.Append(provider: CultureInfo.InvariantCulture, handler: $"field levels cost: bake evaluations {bakeEvaluations}, contour evaluations {contour} ({(((double)contour) / Math.Max(val1: 1L, val2: bakeEvaluations)):0.00}x the bake's), oracle evaluations {oracle} (the audit's, not the bake's); {ThreadCycles.Unit} contour {contourCycles} ({(((double)contourCycles) / Math.Max(val1: 1UL, val2: collapseCycles)):0.00}x the collapse's {collapseCycles}), oracle {oracleCycles}\n");
    }
    private static double Quantile(double[] sorted, double at) => ((sorted.Length == 0) ? 0.0 : sorted[(int)((sorted.Length - 1) * at)]);
    // The worst clusters across every group, each with its image when a directory is named.
    private static void WriteWorst(Outcome?[] results, int[] ordered, int worst, string? images, int level, StringBuilder output) {
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

            output.Append(provider: CultureInfo.InvariantCulture, handler: $"  {(rank + 1),2}. group {ordinal} {outcome.Label}: {Describe(cluster: cluster, outcome: outcome)}\n");
            if ((images is not null) && (outcome is { Mesh: { } mesh, Field: { } field, Report: { } report })) {
                var path = Path.Combine(path1: Path.GetFullPath(path: images), path2: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(rank + 1):00}-group{ordinal}.png"));

                _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
                // The window spans eight cells either way of the worst sample, so a few cells of the defect and the surface
                // beside it are seen.
                MeshAuditImage.Write(axis: cluster.Axis, center: cluster.Worst, halfWidth: Math.Clamp(value: (8f * report.Cell), min: 0.02f, max: 0.08f), mesh: SdfMeshAudit.Surface(mesh: mesh, level: level), path: path, toward: SdfMeshAudit.Gradient(field: field, point: cluster.Worst, step: 0.004f));
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
        builder.Append(provider: CultureInfo.InvariantCulture, handler: $" extent {Millimetres(metres: cluster.Extent.X)}x{Millimetres(metres: cluster.Extent.Y)}x{Millimetres(metres: cluster.Extent.Z)} mm, {cluster.Samples} samples ({cluster.Off} off, {cluster.Turned} turned, {cluster.Shaded} shaded, {cluster.Rough} rough, {cluster.Folded} folded, {cluster.Missing} uncovered), worst {Millimetres(metres: cluster.WorstError)} mm / {cluster.WorstNormal:0} deg at {Point(point: outcome.ToWorld(arg: cluster.Worst))}");
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
        string.Create(provider: CultureInfo.InvariantCulture, handler: $"p50 {(statistic.P50 * scale):0.00} p99 {(statistic.P99 * scale):0.00} max {(statistic.Max * scale):0.00} over {statistic.Over}/{statistic.Count}");
    // A length in metres as millimetres, to a tenth by default.
    internal static string Millimetres(float metres, string format = "0.0") => (metres * 1000f).ToString(format: format, provider: CultureInfo.InvariantCulture);
    private static string Point(Vector3 point) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"({point.X:0.000}, {point.Y:0.000}, {point.Z:0.000})");
    private static string Direction(Vector3 direction) => string.Create(provider: CultureInfo.InvariantCulture, handler: $"({direction.X:0.00}, {direction.Y:0.00}, {direction.Z:0.00})");

    // What the verb was asked: the world, which groups, which store and identity, and what to report.
    private sealed record AuditRequest(string World, string? Prototype, string? Group, string? CacheRoot, string? Build, string? Identity, string Backend, int Top, int Worst, string? Images, bool History, bool Cpu, int Level, bool FieldLevels, bool LocalAim, string? Pose);
    // A group's audit: its report, or why there is none, with its label, the map from its frame to world space, and the
    // mesh and fields it was measured with.
    private sealed record Outcome(string Label, string? Refusal, SdfMeshAuditReport? Report, bool Stored, Func<Vector3, Vector3> ToWorld, RestBody? Owner, SdfMesh? Mesh, SdfFieldEvaluator? Field);
}
