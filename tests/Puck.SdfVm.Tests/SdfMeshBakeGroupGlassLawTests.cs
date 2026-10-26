using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a glass instance bakes into a group of its own (<see cref="SdfMeshBakeGroup.Glass"/>): a static instance
/// whose shapes all carry glass materials never joins the run of the static instance before it, and the instance after
/// it never joins it, whatever either declares (<see cref="SdfInstanceRange.BakesWithPrevious"/>); a dynamic group is
/// glass only while every instance riding its slot is; the world group never is; and a glass draw stays out of the
/// irradiance volume's static scene.
/// </summary>
public sealed class SdfMeshBakeGroupGlassLawTests {
    private static readonly SdfGlass Acrylic = new(Depth: 0.004f, Ior: 1.49f);

    private static List<SdfMeshBakeGroup> Plan(SdfProgram program, IReadOnlyList<DynamicTransform>? transforms = null) =>
        SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: 100f, program: program, transforms: (transforms ?? []));

    [Fact]
    public void AStaticGlassInstanceNeverBakesWithAnOpaqueOne() {
        var builder = new SdfProgramBuilder();
        var opaque = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var glass = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.9f, y: 0.5f, z: 0.1f), Glass: Acrylic));
        // Four static instances each asking to bake with the one before: opaque, glass, opaque, opaque.
        var materials = new[] { opaque, glass, opaque, opaque };

        for (var index = 0; (index < materials.Length); index++) {
            _ = builder.BeginInstance(bakesWithPrevious: (index > 0), boundCenter: new Vector3(x: index, y: 0f, z: 0f), boundRadius: 0.6f)
                .ResetPoint().Translate(offset: new Vector3(x: index, y: 0f, z: 0f)).Sphere(material: materials[index], radius: 0.5f)
                .EndInstance();
        }

        var groups = Plan(program: builder.Build());

        // The glass instance splits the run in three: the first opaque alone, the glass alone, the last two together (a
        // union, which the plan lists first).
        Assert.Equal(expected: 3, actual: groups.Count);
        Assert.Equal(
            expected: [(-1, false, 1), (-2, true, 1), (-3, false, 2)],
            actual: groups.OrderByDescending(keySelector: static group => group.Selector).Select(selector: static group => (group.Selector, group.Glass, group.Instances))
        );
    }
    [Fact]
    public void ADynamicGroupIsGlassOnlyWhileEveryInstanceOnItsSlotIs() {
        var builder = new SdfProgramBuilder();
        var opaque = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var glass = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Glass: Acrylic));

        _ = builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 0.6f)
            .ResetPoint().TransformDynamic(slot: 0).Sphere(material: glass, radius: 0.5f).EndInstance()
            .BeginInstanceDynamic(slot: 1, boundOffset: Vector3.Zero, boundRadius: 0.6f)
            .ResetPoint().TransformDynamic(slot: 1).Sphere(material: glass, radius: 0.5f).EndInstance()
            .BeginInstanceDynamic(slot: 1, boundOffset: Vector3.UnitX, boundRadius: 0.6f)
            .ResetPoint().TransformDynamic(slot: 1).Translate(offset: Vector3.UnitX).Sphere(material: opaque, radius: 0.5f).EndInstance();

        var transforms = new DynamicTransform[] { new(Orientation: Quaternion.Identity, Position: Vector3.Zero), new(Orientation: Quaternion.Identity, Position: Vector3.Zero) };
        var groups = Plan(program: builder.Build(), transforms: transforms);

        Assert.Equal(expected: 2, actual: groups.Count);
        Assert.True(condition: groups.Single(predicate: static group => (group.Slot == 0)).Glass);
        Assert.False(condition: groups.Single(predicate: static group => (group.Slot == 1)).Glass);
    }
    [Fact]
    public void TheWorldGroupIsNeverGlassAndAGlassDrawLightsNoProbe() {
        var builder = new SdfProgramBuilder();
        var glass = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One, Glass: Acrylic));

        _ = builder.ResetPoint().Sphere(material: glass, radius: 0.5f);

        var world = Assert.Single(collection: Plan(program: builder.Build()));

        Assert.True(condition: world.IsWorld);
        Assert.False(condition: world.Glass);

        var mesh = new SdfMesh(indices: new uint[] { 0, 1, 2 }, positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY });

        Assert.False(condition: SdfMeshIrradianceScene.IsStatic(draw: new SdfMeshDraw(Identity: 1, Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity) { Glass = true }));
        Assert.True(condition: SdfMeshIrradianceScene.IsStatic(draw: new SdfMeshDraw(Identity: 1, Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity)));
        Assert.Equal(expected: 0, actual: SdfMeshIrradianceScene.Build(draws: [new SdfMeshDraw(Identity: 1, Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity) { Glass = true }]).TriangleCount);
    }
}
