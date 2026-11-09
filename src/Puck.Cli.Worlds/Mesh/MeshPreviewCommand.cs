using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

using Puck.Assets;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.Cli.Mesh;

/// <summary>
/// <c>puck mesh preview --world &lt;path&gt; --prototype &lt;id&gt; --output &lt;dir&gt;</c>: bakes a prototype's rest mesh on
/// the CPU as <c>mesh audit --cpu</c> does (<see cref="RestScene"/> plans the groups a World's first frame bakes,
/// <see cref="MeshCpuBake"/> answers their lattices through the fixed-point field evaluator), joins every group into the
/// creation's author frame and draws it from a set of views (<see cref="MeshPreviewImage"/>): one PNG a view and a contact
/// sheet of them all, each captioned with what it leaves out. A prototype that no look, body or placement of the world
/// stands has no emitted geometry to bake. An animated creation is drawn in its pooled rest pose. A static placement keeps
/// the placement's own orientation, which the author frame's front assumes is unturned. Exit codes: 0 written; 2 loading
/// failed, a usage error, or the prototype baked no surface.
/// </summary>
public static class MeshPreviewCommand {
    private const string Verb = "mesh preview";
    // The groups with the most open edges, and the materials with the most triangles, a report lists.
    private const int WorstGroups = 8;

    /// <summary>Creates the <c>preview</c> subverb.</summary>
    /// <returns>The subverb.</returns>
    public static Command Create() {
        var worldOption = new Option<string>(name: "--world") { Description = "The world document whose prototype to preview.", Required = true };
        var prototypeOption = new Option<string>(name: "--prototype") { Description = "The prototype whose creation to bake and draw; a look, body or placement of the world must stand it.", Required = true };
        var outputOption = new Option<string>(name: "--output") { Description = "The directory to write the PNGs into: one per view, named <prototype>-<view>.png, and <prototype>-sheet.png holding them all.", Required = true };
        var viewsOption = new Option<string[]>(name: "--views") { AllowMultipleArgumentsPerToken = true, Description = "The views to draw: standard names (front, back, left, right, front-left, front-right, back-left, back-right, top, below; 'all', the default, is every one), or name=azimuth,elevation[,x,y,z,halfWidth] in degrees about the author frame (azimuth 0 is the front, +90 the subject's own left, elevation 90 from above), and with x,y,z,halfWidth a close window of that half width in metres about the author-frame point." };
        var sizeOption = new Option<string>(name: "--size") { DefaultValueFactory = static _ => "480x480", Description = "Each view's width and height in pixels, WxH; a caption strip is added under it." };
        var shadingOption = new Option<string>(name: "--shading") { DefaultValueFactory = static _ => "smooth", Description = "smooth lights each pixel by the mesh's normals interpolated across the triangle; flat lights each triangle by its own face normal, which shows the tessellation." };
        var openOption = new Option<bool>(name: "--open-edges") { DefaultValueFactory = static _ => true, Description = "Tint red each triangle that holds an edge no other triangle shares; false leaves every triangle in its material's colour." };
        var command = new Command(
            description: "Bakes a prototype's rest mesh on the CPU (the bake mesh audit --cpu measures), joins its groups in the creation's author frame and draws it from standard views (front, back, both sides, four three-quarter views, top, below) and any named view as lit, palette-coloured PNGs and one contact sheet, with no device. Paints, insets and wear are omitted and each image says so; an animated creation is drawn at its pooled rest pose. Reports triangles, groups and open edges.",
            name: "preview"
        ) { worldOption, prototypeOption, outputOption, viewsOption, sizeOption, shadingOption, openOption };

        command.SetAction(action: parseResult => Run(
            openEdges: parseResult.GetValue(option: openOption),
            output: parseResult.GetValue(option: outputOption)!,
            prototype: parseResult.GetValue(option: prototypeOption)!,
            shading: parseResult.GetValue(option: shadingOption)!,
            size: parseResult.GetValue(option: sizeOption)!,
            views: (parseResult.GetValue(option: viewsOption) ?? []),
            world: parseResult.GetValue(option: worldOption)!
        ));

        return command;
    }

    private static int Run(string world, string prototype, string output, string[] views, string size, string shading, bool openEdges) {
        var parts = size.Split(separator: ['x', 'X']);

        if ((parts.Length != 2) || !int.TryParse(provider: CultureInfo.InvariantCulture, result: out var width, s: parts[0], style: NumberStyles.None) || !int.TryParse(provider: CultureInfo.InvariantCulture, result: out var height, s: parts[1], style: NumberStyles.None) || (width < 64) || (height < 64) || (width > 4096) || (height > 4096)) {
            return CliExit.Refuse(verb: Verb, what: $"--size {size}", why: "a size is WxH pixels, each from 64 to 4096");
        }
        if (!string.Equals(a: shading, b: "smooth", comparisonType: StringComparison.Ordinal) && !string.Equals(a: shading, b: "flat", comparisonType: StringComparison.Ordinal)) {
            return CliExit.Refuse(verb: Verb, what: $"--shading {shading}", why: "shading is smooth or flat");
        }
        if (!MeshPreviewView.TryParse(tokens: views, views: out var chosen, error: out var error)) {
            return CliExit.Refuse(verb: Verb, what: "--views", why: error);
        }
        if (!CliWorldDocuments.TryLoad(definition: out var definition, fullPath: out var fullPath, path: world, verb: Verb)) {
            return CliExit.Refused;
        }
        if (!definition.Creations.Any(predicate: creation => string.Equals(a: creation.Id, b: prototype, comparisonType: StringComparison.Ordinal))) {
            return CliExit.Refuse(verb: Verb, what: $"'{prototype}'", why: $"'{fullPath}' names no such prototype; prototypes: {string.Join(separator: ", ", values: definition.Creations.Select(selector: static creation => creation.Id.Value))}");
        }

        var clock = Stopwatch.StartNew();
        var scene = RestScene.Compose(definition: definition, sourcePath: fullPath);
        var farDistance = WorldRenderFarDistance.Resolve(defaults: definition.Render);
        var groups = scene.Plan(farDistance: farDistance);
        var named = groups.Where(predicate: group => string.Equals(a: scene.Prototype(group), b: prototype, comparisonType: StringComparison.Ordinal)).ToArray();

        if (named.Length == 0) {
            return CliExit.Empty(verb: Verb, selection: $"prototype '{prototype}'", why: "has no bake group in the world's rest scene: no look, body or placement stands it");
        }

        // One instance's groups: a prototype stood more than once is previewed as its first.
        var first = Instance(group: named[0], scene: scene);
        var selected = named.Where(predicate: group => (Instance(group: group, scene: scene) == first)).ToArray();
        var anchor = selected.FirstOrDefault(predicate: static group => group.Dynamic);
        var baked = MeshCpuBake.Bake(farDistance: farDistance, groups: selected, scene: scene);
        var bakeSeconds = clock.Elapsed.TotalSeconds;
        var subject = Join(anchor: anchor, baked: baked, scene: scene, selected: selected, openEdges: openEdges, report: out var report);

        if (subject is null) {
            return CliExit.Empty(verb: Verb, selection: $"prototype '{prototype}'", why: $"baked no surface in {selected.Length} group(s) on the CPU ({report.Trim()})");
        }

        var directory = Path.GetFullPath(path: output);

        _ = Directory.CreateDirectory(path: directory);

        var animated = selected.Any(predicate: static group => group.Dynamic);
        var low = subject.Mesh.PositionLow;
        var high = subject.Mesh.PositionHigh;
        var flat = string.Equals(a: shading, b: "flat", comparisonType: StringComparison.Ordinal);
        var tiles = new List<byte[]>();
        var files = new List<string>();
        var drawn = Stopwatch.StartNew();

        foreach (var view in chosen) {
            var window = MeshPreviewImage.Frame(aspect: (width / (float)height), mesh: subject.Mesh, view: view);
            var caption = new[] {
                string.Create(provider: CultureInfo.InvariantCulture, handler: $"{prototype} {view.Name} az {view.Azimuth:0} el {view.Elevation:0} span {(2f * window.HalfWidth):0.00} m"),
                $"cpu bake, {shading} lambert, palette albedo only",
                $"paints omitted; {(animated ? "pooled rest pose" : "static")}{(openEdges ? "; red = open edge" : string.Empty)}",
            };
            var rgba = MeshPreviewImage.Render(caption: caption, flat: flat, height: height, subject: subject, view: window, width: width);
            var path = Path.Combine(path1: directory, path2: $"{prototype}-{view.Name}.png");

            PngEncoder.Write(height: (height + MeshPreviewImage.StripHeight), path: path, rgba: rgba, width: width);
            tiles.Add(item: rgba);
            files.Add(item: path);
        }

        var (sheetWidth, sheetHeight, sheet) = MeshPreviewImage.Sheet(columns: Math.Min(val1: 5, val2: tiles.Count), tileHeight: (height + MeshPreviewImage.StripHeight), tileWidth: width, tiles: tiles);
        var sheetPath = Path.Combine(path1: directory, path2: $"{prototype}-sheet.png");

        PngEncoder.Write(height: sheetHeight, path: sheetPath, rgba: sheet, width: sheetWidth);
        files.Add(item: sheetPath);

        var text = new StringBuilder();

        text.Append(provider: CultureInfo.InvariantCulture, handler: $"mesh preview: {CliPaths.ToDisplay(fullPath: fullPath)} prototype {prototype}: {selected.Length} group(s), {subject.Mesh.TriangleCount} triangles, {subject.Mesh.Positions.Length} vertices, {subject.OpenTriangles.Count(predicate: static held => held)} triangles on open edges\n");
        text.Append(provider: CultureInfo.InvariantCulture, handler: $"  {(animated ? "animated: the pooled rest pose" : "static")}, baked on the CPU through the field evaluator in {bakeSeconds:0.0} s, {chosen.Count} view(s) drawn in {drawn.Elapsed.TotalSeconds:0.0} s\n");
        text.Append(provider: CultureInfo.InvariantCulture, handler: $"  author frame (+Z front, +X the subject's left, metres): x {low.X:0.000}..{high.X:0.000}, y {low.Y:0.000}..{high.Y:0.000}, z {low.Z:0.000}..{high.Z:0.000}\n");
        text.Append(value: report);
        foreach (var file in files) {
            text.Append(provider: CultureInfo.InvariantCulture, handler: $"  {CliPaths.ToDisplay(fullPath: file)}\n");
        }

        Console.Out.Write(value: text.ToString());

        return CliExit.Success;
    }
    // The instance a group belongs to: its body or its placement.
    private static string Instance(SdfMeshBakeGroup group, RestScene scene) =>
        string.Create(provider: CultureInfo.InvariantCulture, handler: $"{scene.Owner(group: group)?.BodyIndex}/{scene.Placement(group: group)}");
    // The groups' meshes joined in the author frame, with a line per group and the palette colours' use; null when none baked.
    private static MeshPreviewSubject? Join(RestScene scene, SdfMeshBakeGroup[] selected, SdfMeshBakeGroup? anchor, Dictionary<int, SdfMesh> baked, bool openEdges, out string report) {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<uint>();
        var materials = new List<uint>();
        var albedo = new List<Vector3>();
        var open = new List<bool>();
        var text = new StringBuilder();
        var palette = scene.Program.Appearance.Materials;
        var use = new SortedDictionary<uint, int>();
        var measured = new List<(string Label, int Triangles, int Open, int NonManifold)>();

        foreach (var group in selected) {
            var label = scene.Label(group: group, owner: scene.Owner(group: group));

            if (!baked.TryGetValue(key: group.Selector, value: out var mesh)) {
                text.Append(provider: CultureInfo.InvariantCulture, handler: $"  group {label}: baked no surface, or the field evaluator refuses it\n");
                continue;
            }

            var map = ToAuthor(anchor: anchor, group: group, scene: scene);
            var origin = map(Vector3.Zero);
            var edges = SdfMeshAudit.EdgeCounts(mesh: mesh);
            var offset = (uint)positions.Count;

            foreach (var position in mesh.Positions.Span) { positions.Add(item: map(position)); }
            foreach (var normal in mesh.Normals.Span) {
                var turned = (map(normal) - origin);

                normals.Add(item: ((turned.LengthSquared() > 0f) ? Vector3.Normalize(value: turned) : Vector3.Zero));
            }
            foreach (var index in mesh.Indices.Span) { indices.Add(item: (offset + index)); }
            for (var triangle = 0; (triangle < mesh.TriangleCount); triangle++) {
                var id = (mesh.TriangleMaterials.IsEmpty ? 0u : mesh.TriangleMaterials.Span[triangle]);

                materials.Add(item: id);
                use[id] = (use.GetValueOrDefault(key: id) + 1);
                open.Add(item: (openEdges && edges.OpenTriangles[triangle]));
                // The render path writes albedo to a UNORM surface unconverted (HexColor), so it is the display colour.
                albedo.Add(item: Vector3.Clamp(value1: (((int)id < palette.Count) ? palette[(int)id].Albedo : new Vector3(value: 0.5f)), min: Vector3.Zero, max: Vector3.One));
            }

            measured.Add(item: (label, mesh.TriangleCount, edges.Open, edges.NonManifold));
        }

        text.Append(provider: CultureInfo.InvariantCulture, handler: $"  groups: {measured.Count} baked, {measured.Count(predicate: static group => ((group.Open + group.NonManifold) > 0))} with open or non-manifold edges; open edges {measured.Sum(selector: static group => group.Open)}, non-manifold edges {measured.Sum(selector: static group => group.NonManifold)}\n");
        foreach (var group in measured.Where(predicate: static group => ((group.Open + group.NonManifold) > 0)).OrderByDescending(keySelector: static group => (group.Open + group.NonManifold)).Take(count: WorstGroups)) {
            text.Append(provider: CultureInfo.InvariantCulture, handler: $"    {group.Label}: {group.Triangles} triangles, {group.Open} open edges, {group.NonManifold} non-manifold edges\n");
        }

        report = text.ToString();
        if (positions.Count == 0) { return null; }
        text.Clear();
        text.Append(provider: CultureInfo.InvariantCulture, handler: $"{report}  triangles by program material id ({use.Count} in use, largest first): {string.Join(separator: ", ", values: use.OrderByDescending(keySelector: static pair => pair.Value).Take(count: WorstGroups).Select(selector: static pair => $"{pair.Key}={pair.Value}"))}\n");
        report = text.ToString();
        if (normals.Count != positions.Count) { normals.Clear(); }

        return new MeshPreviewSubject(Albedo: [.. albedo], Mesh: new SdfMesh(indices: indices.ToArray(), normals: normals.ToArray(), positions: positions.ToArray(), triangleMaterials: materials.ToArray()), OpenTriangles: [.. open]);
    }
    // A group's frame into the creation's author frame: the world point it poses through its slot, taken back through the
    // body that wears the creation, or for a placement through the first dynamic group's slot, then turned half about up
    // (Puck.World.Authoring.CreationFrame). A static group is already in the world, kept as placed.
    private static Func<Vector3, Vector3> ToAuthor(RestScene scene, SdfMeshBakeGroup group, SdfMeshBakeGroup? anchor) {
        var toWorld = scene.ToWorld(group: group);

        if (scene.Owner(group: group) is { } owner) { return point => owner.ToAuthor(world: toWorld(point)); }
        if (!group.Dynamic || (anchor is null) || (anchor.Slot >= scene.Transforms.Count)) { return static point => new Vector3(x: -point.X, y: point.Y, z: -point.Z); }

        var root = scene.Transforms[anchor.Slot];
        var back = Quaternion.Inverse(value: ((root.Orientation == default) ? Quaternion.Identity : root.Orientation));

        return point => {
            var local = (Vector3.Transform(value: (toWorld(point) - root.Position), rotation: back) / root.Scale);

            return new Vector3(x: -local.X, y: local.Y, z: -local.Z);
        };
    }
}
