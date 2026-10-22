using System.Numerics;

using Puck.Cli.Mesh;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Puck.World;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Worlds.Tests.Mesh;

/// <summary>
/// <c>puck mesh audit</c> over the Moth's world: a mesh stored under a named bake identity for one of its groups is
/// found by the group's geometry, measured against the group's field, its defect located at the Moth's rest pose with the
/// shape there named, drawn, and found again among the store's history; a damaged entry and a missing mesh are reported
/// as such; and every usage the verb cannot serve is refused by name.
/// </summary>
public sealed class MeshCommandTests {
    // The identity the laws store meshes under: any value, named to the verb in place of a build.
    private static readonly UInt128 Identity = 0x5EED;

    private static (int ExitCode, string Output) Invoke(params string[] args) =>
        ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: args));
    private static string TempWorldCopy(TemporaryDirectory directory) => directory.WriteBytes(
        bytes: ShippedWorldDocuments.Composed(path: RepositoryPaths.Resolve(relativePath: $"{ShippedWorldDocuments.WorldDirectory}/debug/avatars/moth.puck")),
        name: "moth.world.json"
    );
    // The world's rest scene and its planned groups, as the verb plans them.
    private static (RestScene Scene, List<SdfMeshBakeGroup> Groups) Plan(string world) {
        Assert.True(condition: WorldDefinitionLoader.TryLoadFileForAdmission(admission: out var admission, contentHash: out _, documents: PuckDocumentComposer.Instance, path: world, proveNeighbours: false, reason: out var reason), userMessage: reason);

        var definition = admission!.Definition;
        var scene = RestScene.Compose(definition: definition, sourcePath: world);

        return (scene, SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: WorldRenderFarDistance.Resolve(defaults: definition.Render), program: scene.Program, transforms: scene.Transforms));
    }
    // The world with two boxes placed beside the Moth: a plain one, a static group whose shapes have no slot to be named
    // by, and a hexagonal prism, whose regular polygon lowers to the convex polygon of its vertices.
    private static string Placed(string path) {
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path));

        static WorldPrototype Prototype(string name, SdfPrismProfile? profile) {
            var canonical = CreationCanonicalizer.Canonicalize(new CreationDocument(
                Schema: CreationDocument.CurrentSchema,
                Name: name,
                Palette: null,
                Shapes: [new ShapeDocument(Id: 0, Name: "slab", Type: ((profile is null) ? SdfSolidPrimitive.Box : SdfSolidPrimitive.Prism), Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: new Vector3(value: 0.2f), Material: 0, Blend: SdfBlendOp.Union, Smooth: 0f, Group: 0, Profile: profile, Lift: ((profile is null) ? null : SdfLift.Extrude))],
                Frames: null
            ));

            return new WorldPrototype(name, canonical.Document, canonical.Hash);
        }

        WorldDefinitionSerialization.Save(
            definition: definition with {
                CreationsRaw = [.. definition.Creations, Prototype(name: "plain", profile: null), Prototype(name: "hexagonal", profile: new SdfPrismProfile(Kind: SdfPrismProfileKind.Polygon, Sides: 6))],
                PlacementRowsRaw = [
                    .. definition.Placements,
                    new WorldPlacement(Id: "plain", PrototypeId: "plain", Position: new Vector3(x: -3f, y: 1f, z: 0f), YawDegrees: 0f, Scale: 1f),
                    new WorldPlacement(Id: "hexagonal", PrototypeId: "hexagonal", Position: new Vector3(x: 3f, y: 1f, z: 0f), YawDegrees: 0f, Scale: 1f),
                ],
            },
            path: path
        );

        return path;
    }
    private static SdfMeshBakeGroup Group(RestScene scene, List<SdfMeshBakeGroup> groups, string shape) =>
        groups.Single(predicate: group => scene.Label(group: group, owner: scene.Owner(group: group)).EndsWith(value: $" {shape}", comparisonType: StringComparison.Ordinal));

    [Fact]
    public void AStoredMeshIsMeasuredLocatedNamedDrawnAndFoundInTheHistory() {
        using var directory = new TemporaryDirectory(prefix: "puck-mesh-audit-");
        var world = TempWorldCopy(directory: directory);
        var (scene, groups) = Plan(world: world);
        var hinge = Group(groups: groups, scene: scene, shape: "podHinge-left");
        var field = new SdfFieldEvaluator(program: SdfMeshAudit.Field(group: hinge, program: scene.Program), reading: SdfFieldReading.Render);
        var mesh = SdfMeshContour.Bake(field: field, high: hinge.High, low: hinge.Low, samples: 33);
        var positions = mesh.Positions.ToArray();
        var store = new SdfMeshStore(directory: directory.PathOf(name: Path.Combine("cache", "meshes")));
        var owner = scene.Owner(group: hinge)!;

        // One vertex pushed a centimetre off the surface: the defect the audit locates.
        positions[0] += (Vector3.Normalize(value: mesh.Normals.Span[0]) * 0.01f);
        Assert.True(condition: store.Write(geometry: hinge.Sealed, identity: Identity, materials: hinge.MaterialOrder, mesh: new SdfMesh(indices: mesh.Indices, normals: mesh.Normals, positions: positions, triangleMaterials: mesh.TriangleMaterials)));

        var images = directory.PathOf(name: "images");
        var (exitCode, output) = Invoke("mesh", "audit", "--world", world, "--prototype", "moth", "--group", "podHinge-left", "--identity", Identity.ToString(format: "x32", provider: null), "--cache-dir", directory.PathOf(name: "cache"), "--images", images, "--history", "--worst", "1");

        Assert.Equal(expected: 0, actual: exitCode);
        Assert.Contains(expectedSubstring: "1 group(s) of", actualString: output);
        Assert.Contains(expectedSubstring: ": 1 audited, 0 with no stored mesh, 0 refused", actualString: output);
        Assert.Contains(expectedSubstring: $"slot {hinge.Slot} moth body {owner.BodyIndex} podHinge-left: {mesh.TriangleCount} triangles", actualString: output);
        Assert.Contains(expectedSubstring: "history: 1 stored bake(s) of this field", actualString: output);
        Assert.Contains(expectedSubstring: "nearest podHinge-left", actualString: output);
        Assert.Contains(expectedSubstring: "(author (", actualString: output);
        Assert.Single(collection: Directory.GetFiles(path: images, searchPattern: "*.png"));
    }
    [Fact]
    public void AStoredDummyADamagedEntryAndAMissingMeshAreReportedAsSuch() {
        using var directory = new TemporaryDirectory(prefix: "puck-mesh-audit-");
        var world = Placed(path: TempWorldCopy(directory: directory));
        var (scene, groups) = Plan(world: world);
        var store = new SdfMeshStore(directory: directory.PathOf(name: Path.Combine("cache", "meshes")));
        var hexagonal = Group(groups: groups, scene: scene, shape: "hexagonal (hexagonal)");
        var plain = Group(groups: groups, scene: scene, shape: "plain (plain)");
        var hinge = Group(groups: groups, scene: scene, shape: "podHinge-right");
        var dummy = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });
        var box = new SdfFieldEvaluator(program: SdfMeshAudit.Field(group: plain, program: scene.Program), reading: SdfFieldReading.Render);

        Assert.True(condition: store.Write(geometry: hexagonal.Sealed, identity: Identity, materials: hexagonal.MaterialOrder, mesh: dummy));
        Assert.True(condition: store.Write(geometry: plain.Sealed, identity: Identity, materials: plain.MaterialOrder, mesh: SdfMeshContour.Bake(field: box, high: plain.High, low: plain.Low, samples: 25)));

        var sound = store.EntryPaths();

        Assert.True(condition: store.Write(geometry: hinge.Sealed, identity: Identity, materials: hinge.MaterialOrder, mesh: dummy));

        var damaged = store.EntryPaths().Single(predicate: path => !sound.Contains(value: path));

        File.WriteAllBytes(bytes: File.ReadAllBytes(path: damaged)[..20], path: damaged);

        var identity = Identity.ToString(format: "x32", provider: null);
        var (refusal, output) = Invoke("mesh", "audit", "--world", world, "--group", "hexagonal", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        // A regular polygon lowers to the convex polygon of its vertices, which the reading takes, so the stored dummy
        // mesh is measured against it.
        Assert.Equal(expected: 0, actual: refusal);
        Assert.Contains(expectedSubstring: "hexagonal (hexagonal): 1 triangles", actualString: output);

        var (audited, report) = Invoke("mesh", "audit", "--world", world, "--group", "plain", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 0, actual: audited);
        Assert.Contains(expectedSubstring: "plain (plain): ", actualString: report);
        Assert.Contains(expectedSubstring: " triangles, ", actualString: report);

        // Mirrored hinges share one geometry, so both read the one damaged entry.
        var (truncated, damage) = Invoke("mesh", "audit", "--world", world, "--group", "podHinge", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 0, actual: truncated);
        Assert.Contains(expectedSubstring: "2 group(s) of", actualString: damage);
        Assert.Contains(expectedSubstring: "is truncated", actualString: damage);

        var (absent, guidance) = Invoke("mesh", "audit", "--world", world, "--group", "podCap-left", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 1, actual: absent);
        Assert.Contains(expectedSubstring: "podCap-left: no stored mesh", actualString: guidance);
        Assert.Contains(expectedSubstring: "the store holds no mesh of the selected groups", actualString: guidance);
    }
    // With --cpu the verb bakes the selected groups itself, through the bake's own lattices answered by the field
    // evaluator, and reads no store: a plain box audits clean, and a group whose field the evaluator refuses is named.
    [Fact]
    public void AnOfflineBakeAuditsAGroupWithNoDeviceAndNoStore() {
        using var directory = new TemporaryDirectory(prefix: "puck-mesh-audit-");
        var world = Placed(path: TempWorldCopy(directory: directory));
        var (baked, report) = Invoke("mesh", "audit", "--cpu", "--world", world, "--group", "plain", "--cache-dir", directory.PathOf(name: "empty"));

        Assert.Equal(expected: 0, actual: baked);
        Assert.Contains(expectedSubstring: "baked offline on the CPU", actualString: report);
        Assert.Contains(expectedSubstring: ": 1 audited, 0 with no stored mesh, 0 refused", actualString: report);
        Assert.Matches(expectedRegexPattern: @"plain \(plain\): \d+ triangles, .* score 0\.0", actualString: report);
        Assert.Contains(expectedSubstring: "topology: 0 folds, 0 flipped", actualString: report);

        var (prism, hexagon) = Invoke("mesh", "audit", "--cpu", "--world", world, "--group", "hexagonal");

        Assert.Equal(expected: 0, actual: prism);
        Assert.Matches(expectedRegexPattern: @"hexagonal \(hexagonal\): \d+ triangles, ", actualString: hexagon);
    }
    [Fact]
    public void EveryUsageTheVerbCannotServeIsRefusedByName() {
        using var directory = new TemporaryDirectory(prefix: "puck-mesh-audit-");
        var world = TempWorldCopy(directory: directory);
        var malformed = directory.WriteText(name: "malformed.world.json", text: "[1, 2, 3]");

        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "audit", "--world", world, "--backend", "metal"), text: "unknown --backend 'metal'"));
        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "audit", "--world", world, "--history"), text: "--history reads every stored mesh"));
        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "audit", "--world", world, "--identity", "not-hex"), text: "--identity takes 32 hex digits"));
        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "audit", "--world", world, "--identity", "5", "--build", directory.RootPath), text: "--identity takes 32 hex digits"));
        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "audit", "--world", world, "--build", directory.RootPath, "--backend", "vulkan"), text: "cannot read the bake identity"));
        Assert.Equal(expected: (1, true), actual: Refused(output: Invoke("mesh", "audit", "--world", malformed), text: "failed to load"));
        // This build's own identity, read from its assemblies and kernels, keys nothing in an empty store.
        Assert.Equal(expected: (1, true), actual: Refused(output: Invoke("mesh", "audit", "--world", world, "--group", "podCap-left", "--cache-dir", directory.PathOf(name: "empty")), text: ", build "));
    }
    // mesh scene builds the irradiance trace scene over the stored static meshes, traces it, updates it for one draw's new
    // geometry (building that alone), a move and a return (building nothing), and traces each answer alike; a change no
    // label holds, an empty store and the usages audit refuses are refused by name.
    [Fact]
    public void TheSceneVerbTimesABuildAndAOneDrawChangeOverStoredStaticMeshes() {
        using var directory = new TemporaryDirectory(prefix: "puck-mesh-scene-");
        var world = Placed(path: TempWorldCopy(directory: directory));
        var (scene, groups) = Plan(world: world);
        var store = new SdfMeshStore(directory: directory.PathOf(name: Path.Combine("cache", "meshes")));
        var identity = Identity.ToString(format: "x32", provider: null);

        SdfMeshBakeGroup Placement(string name) =>
            groups.Single(predicate: group => scene.Label(group: group, owner: null).Contains(value: $" {name} ({name})", comparisonType: StringComparison.Ordinal));

        foreach (var (placement, samples) in new[] { ("plain", 17), ("hexagonal", 9) }) {
            var group = Placement(name: placement);
            var plain = Placement(name: "plain");
            var mesh = SdfMeshContour.Bake(field: new SdfFieldEvaluator(program: SdfMeshAudit.Field(group: plain, program: scene.Program), reading: SdfFieldReading.Render), high: plain.High, low: plain.Low, samples: samples);

            Assert.True(condition: store.Write(geometry: group.Sealed, identity: Identity, materials: group.MaterialOrder, mesh: mesh));
        }

        var (changed, report) = Invoke("mesh", "scene", "--world", world, "--change", "hexagonal", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 0, actual: changed);
        Assert.Matches(expectedRegexPattern: @"2 static draw\(s\) with stored meshes \(\d+ without\)", actualString: report);
        Assert.Contains(expectedSubstring: "change: ", actualString: report);
        Assert.Matches(expectedRegexPattern: @"build: .* geometries=2 built=2 ", actualString: report);
        Assert.Matches(expectedRegexPattern: @"new geometry: .* built=1 .* reused=1 ", actualString: report);
        Assert.Matches(expectedRegexPattern: @"move: .* built=0 ", actualString: report);
        Assert.Matches(expectedRegexPattern: @"back: .* built=0 built-triangles=0 reused=2 ", actualString: report);
        Assert.Single(collection: report.Split(separator: '\n').Where(predicate: static line => line.Contains(value: " trace: ", comparisonType: StringComparison.Ordinal)).Select(selector: static line => line[..line.IndexOf(value: " seconds=", comparisonType: StringComparison.Ordinal)]).Distinct());

        var (largest, largestReport) = Invoke("mesh", "scene", "--world", world, "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 0, actual: largest);
        Assert.Contains(expectedSubstring: "plain (plain)", actualString: largestReport.Split(separator: '\n')[1]);

        var malformed = directory.WriteText(name: "malformed.world.json", text: "[1, 2, 3]");

        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "scene", "--world", world, "--change", "nothing-holds-this", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache")), text: "has a label holding 'nothing-holds-this'"));
        Assert.Equal(expected: (1, true), actual: Refused(output: Invoke("mesh", "scene", "--world", world, "--identity", identity, "--cache-dir", directory.PathOf(name: "empty")), text: "holds no static mesh"));
        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "scene", "--world", world, "--backend", "metal"), text: "unknown --backend 'metal'"));
        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "scene", "--world", world, "--identity", "not-hex"), text: "mesh scene: --identity takes 32 hex digits"));
        Assert.Equal(expected: (1, true), actual: Refused(output: Invoke("mesh", "scene", "--world", malformed), text: "failed to load"));
    }
    // mesh identity prints this build's bake identity with its parts, names nothing changed against itself, says how the
    // walk reached a member, and refuses a backend it has no kernels for and a directory holding no build.
    [Fact]
    public void TheIdentityVerbPrintsABuildsIdentityItsPartsAndWhatDiffers() {
        using var directory = new TemporaryDirectory(prefix: "puck-mesh-identity-");
        var build = AppContext.BaseDirectory;
        var (printed, report) = Invoke("mesh", "identity", "--build", build, "--against", build, "--members", "--why", "SdfDualContouring.Extract");

        Assert.Equal(expected: 0, actual: printed);
        Assert.Contains(expectedSubstring: SdfMeshBakeClosure.Of(directory: build).Hash.ToString(format: "x32", provider: null), actualString: report);
        Assert.Contains(expectedSubstring: "Puck.SignedDistance ", actualString: report);
        Assert.Contains(expectedSubstring: "the same identity", actualString: report);
        Assert.Contains(expectedSubstring: "code 0 member(s) differ", actualString: report);
        Assert.Contains(expectedSubstring: "  why Puck.SignedDistance!Puck.SignedDistance.Baking.SdfDualContouring.Extract", actualString: report);
        Assert.Contains(expectedSubstring: "    from Puck.SdfVm!", actualString: report);

        var (named, against) = Invoke("mesh", "identity");

        Assert.Equal(expected: (0, false), actual: (named, against.Contains(value: "against", comparisonType: StringComparison.Ordinal)));
        Assert.Equal(expected: (2, true), actual: Refused(output: Invoke("mesh", "identity", "--backend", "metal"), text: "unknown --backend 'metal'"));
        Assert.Equal(expected: (1, true), actual: Refused(output: Invoke("mesh", "identity", "--build", directory.RootPath), text: "cannot read the bake identity"));
        Assert.Equal(expected: (1, true), actual: Refused(output: Invoke("mesh", "identity", "--against", directory.RootPath), text: "cannot read the bake identity"));
    }
    [Fact]
    public void TheRestScenesSurroundSendsAndAnswersNothing() {
        var inert = new RestScene.Inert();
        var answered = false;

        inert.Query(completion: _ => answered = true, query: default!);
        inert.SubmitIntent(submission: default);
        inert.SubmitSession(completion: _ => answered = true, request: default!);
        inert.SubmitCue(eventToken: "cue", site: null);
        inert.BeginTick(tick: 1UL);

        Assert.False(condition: answered);
        Assert.Equal(expected: 0L, actual: inert.SubmitEnvelope(payload: default!, principal: default));
        Assert.Equal(expected: WorldBodyContactMode.Solid, actual: inert.LocalBodyContact(index: 0));
        Assert.Equal(expected: default, actual: inert.LocalEntityAddress(index: 0));
        Assert.False(condition: inert.TryResolve(adjacencyName: "east", neighbour: out var neighbour));
        Assert.Null(@object: neighbour);
        Assert.Empty(collection: inert.Visuals());
        Assert.False(condition: inert.TryLocalDepartedFrom(departedFrom: out _, index: 0));
    }

    private static (int ExitCode, bool Named) Refused((int ExitCode, string Output) output, string text) =>
        (output.ExitCode, output.Output.Contains(value: text, comparisonType: StringComparison.Ordinal));
}
