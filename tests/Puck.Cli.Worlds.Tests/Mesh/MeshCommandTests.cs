using System.Numerics;

using Puck.Cli.Mesh;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Puck.World;
using Puck.World.Client;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Worlds.Tests.Mesh;

/// <summary>
/// <c>puck mesh audit</c> over the Moth's world: a mesh stored under a named bake identity for one of its groups is
/// found by the group's geometry, measured against the group's field, its defect located at the Moth's rest pose with the
/// shape there named, drawn, and found again among the store's history; a stored mesh of a group whose field the
/// evaluator refuses, and a damaged entry, are reported as such; and every usage the verb cannot serve is refused by name.
/// </summary>
public sealed class MeshCommandTests {
    // The identity the laws store meshes under: any value, named to the verb in place of a build.
    private static readonly UInt128 Identity = 0x5EED;

    private static (int ExitCode, string Output) Invoke(params string[] args) =>
        ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: args));
    private static string TempWorldCopy(TemporaryDirectory directory) => directory.WriteBytes(
        bytes: ShippedWorldDocuments.Composed(path: RepositoryPaths.Resolve(relativePath: $"{ShippedWorldDocuments.WorldDirectory}/avatars/moth.puck")),
        name: "moth.world.json"
    );
    // The world's rest scene and its planned groups, as the verb plans them.
    private static (RestScene Scene, List<SdfMeshBakeGroup> Groups) Plan(string world) {
        Assert.True(condition: WorldDefinitionLoader.TryLoadFileForAdmission(admission: out var admission, contentHash: out _, documents: PuckDocumentComposer.Instance, path: world, proveNeighbours: false, reason: out var reason), userMessage: reason);

        var definition = admission!.Definition;
        var scene = RestScene.Compose(definition: definition, sourcePath: world);

        return (scene, SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: WorldRenderFarDistance.Resolve(defaults: definition.Render), program: scene.Program, transforms: scene.Transforms));
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
    public void ARefusedFieldADamagedEntryAndAMissingMeshAreReportedAsSuch() {
        using var directory = new TemporaryDirectory(prefix: "puck-mesh-audit-");
        var world = TempWorldCopy(directory: directory);
        var (scene, groups) = Plan(world: world);
        var store = new SdfMeshStore(directory: directory.PathOf(name: Path.Combine("cache", "meshes")));
        var helmet = Group(groups: groups, scene: scene, shape: "helmet");
        var hinge = Group(groups: groups, scene: scene, shape: "podHinge-right");
        var dummy = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });

        Assert.True(condition: store.Write(geometry: helmet.Sealed, identity: Identity, materials: helmet.MaterialOrder, mesh: dummy));

        var helmetEntry = Assert.Single(collection: store.EntryPaths());

        Assert.True(condition: store.Write(geometry: hinge.Sealed, identity: Identity, materials: hinge.MaterialOrder, mesh: dummy));

        var damaged = store.EntryPaths().Single(predicate: path => (path != helmetEntry));

        File.WriteAllBytes(bytes: File.ReadAllBytes(path: damaged)[..20], path: damaged);

        var identity = Identity.ToString(format: "x32", provider: null);
        var (refused, output) = Invoke("mesh", "audit", "--world", world, "--group", "helmet", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 0, actual: refused);
        Assert.Contains(expectedSubstring: "helmet: field refused: SdfFieldEvaluator cannot interpret", actualString: output);

        var (truncated, damage) = Invoke("mesh", "audit", "--world", world, "--group", "podHinge-right", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 0, actual: truncated);
        Assert.Contains(expectedSubstring: "is truncated", actualString: damage);

        var (absent, guidance) = Invoke("mesh", "audit", "--world", world, "--group", "podCap-left", "--identity", identity, "--cache-dir", directory.PathOf(name: "cache"));

        Assert.Equal(expected: 1, actual: absent);
        Assert.Contains(expectedSubstring: "podCap-left: no stored mesh", actualString: guidance);
        Assert.Contains(expectedSubstring: "the store holds no mesh of the selected groups", actualString: guidance);
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
    }

    private static (int ExitCode, bool Named) Refused((int ExitCode, string Output) output, string text) =>
        (output.ExitCode, output.Output.Contains(value: text, comparisonType: StringComparison.Ordinal));
}
