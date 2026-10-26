using System.Numerics;
using Puck.SignedDistance;
using Puck.Text;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a plan made at one frame's transforms and refreshed at another's (<see cref="SdfMeshBakeGroup.Refresh{T}"/>)
/// holds, for every group the refresh names, the group the whole plan (<see cref="SdfMeshBakeGroup.Plan{T}"/>) makes at
/// the second frame's transforms, at the same place; a dynamic group the refresh does not name, and a static group whose
/// geometry reads no slot, stay as planned.
/// </summary>
public sealed class SdfMeshBakeGroupPlanLawTests {
    private static SdfMeshBakeGroup Create(int selector, bool dynamic) => new(dynamic: dynamic, selector: selector);

    private static (int Selector, SdfMeshGeometry Geometry, int Instances, int First, bool Glass, Vector3 Low, Vector3 High, string Poses) Shape(SdfMeshBakeGroup group) =>
        (group.Selector, group.Sealed, group.Instances, group.FirstInstance, group.Glass, group.Low, group.High, string.Join(separator: ',', values: group.PoseRows));

    [Fact]
    public void GlyphEligibilityFollowsItsStaticDynamicOrWorldGroup() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        var atlas = new FontAtlas(FontAtlasKind.Mtsdf, "test://glyph", 32, 8, 2, 2, default, [], []);
        void Glyph(int? slot = null) {
            _ = builder.ResetPoint();
            if (slot is { } dynamicSlot) { _ = builder.TransformDynamic(dynamicSlot); }
            _ = builder.Glyph(atlas, Vector2.Zero, Vector2.One, 0.5f, 0.5f, 0.02f, 0.25f, material);
        }

        // World text, a static union with text, a plain static group, and separate dynamic text/plain groups.
        Glyph();
        _ = builder.BeginInstance(Vector3.Zero, 1f).ResetPoint().Sphere(0.5f, material).EndInstance();
        _ = builder.BeginInstance(Vector3.Zero, 1f, bakesWithPrevious: true);
        Glyph();
        _ = builder.EndInstance();
        _ = builder.BeginInstance(Vector3.Zero, 1f).ResetPoint().Sphere(0.5f, material).EndInstance();
        _ = builder.BeginInstanceDynamic(0, Vector3.Zero, 1f);
        Glyph(slot: 0);
        _ = builder.EndInstance();
        _ = builder.BeginInstanceDynamic(1, Vector3.Zero, 1f).ResetPoint().TransformDynamic(1).Sphere(0.5f, material).EndInstance();
        var program = builder.Build();
        DynamicTransform[] transforms = [new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity)];
        var groups = SdfMeshBakeGroup.Plan(program, transforms, 50f, Create);
        Assert.True(Assert.Single(groups, static group => group.IsWorld).Geometry.ContainsGlyph);
        Assert.True(Assert.Single(groups, static group => !group.Dynamic && group.FirstInstance == 0).Geometry.ContainsGlyph);
        Assert.False(Assert.Single(groups, static group => !group.Dynamic && group.FirstInstance == 2).Geometry.ContainsGlyph);
        Assert.True(Assert.Single(groups, static group => group.Dynamic && group.Slot == 0).Geometry.ContainsGlyph);
        Assert.False(Assert.Single(groups, static group => group.Dynamic && group.Slot == 1).Geometry.ContainsGlyph);
        Assert.Equal(2, SdfMeshBakeGroup.Refresh(groups, program, transforms, 50f, static _ => true, Create));
        Assert.True(Assert.Single(groups, static group => group.Dynamic && group.Slot == 0).Geometry.ContainsGlyph);
        Assert.False(Assert.Single(groups, static group => group.Dynamic && group.Slot == 1).Geometry.ContainsGlyph);
    }

    [Fact]
    public void ARefreshedPlanHoldsTheWholePlanOfTheGroupsItNames() {
        var builder = new SdfProgramBuilder();
        var white = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

        // A world segment reading slot 1, then static runs and unions interleaved with two dynamic slots' instances, each
        // chain also reading slot 0's pose, and a static instance that reads slot 0.
        _ = builder.ResetPoint().TransformDynamic(slot: 1).Box(halfExtents: new Vector3(x: 4f, y: 0.1f, z: 4f), material: white, round: 0f);

        for (var index = 0; (index < 6); index++) {
            if ((index % 3) == 1) {
                _ = builder.BeginInstanceDynamic(slot: (index % 2), boundOffset: Vector3.UnitY, boundRadius: 0.6f)
                    .ResetPoint().TransformDynamic(slot: (index % 2)).Sphere(material: red, radius: 0.5f)
                    .ResetPoint().TransformDynamic(slot: 0).Sphere(material: white, radius: 0.25f).EndInstance();
            }

            _ = builder.BeginInstance(bakesWithPrevious: ((index % 3) == 2), boundCenter: new Vector3(x: index, y: 0f, z: 0f), boundRadius: 0.6f)
                .ResetPoint().Translate(offset: new Vector3(x: index, y: 0f, z: 0f)).Sphere(material: (((index % 2) == 0) ? white : red), radius: 0.5f)
                .EndInstance();
        }

        _ = builder.BeginInstance(bakesWithPrevious: false, boundCenter: Vector3.Zero, boundRadius: 2f)
            .ResetPoint().TransformDynamic(slot: 0).Sphere(material: white, radius: 0.5f).EndInstance();

        var program = builder.Build();
        static DynamicTransform[] At(float shift) => [
            new(Orientation: Quaternion.CreateFromYawPitchRoll(yaw: shift, pitch: 0f, roll: 0f), Position: new Vector3(x: (1f + shift), y: 2f, z: 3f)),
            new(Orientation: Quaternion.CreateFromYawPitchRoll(yaw: (0.5f + shift), pitch: 0f, roll: 0f), Position: (Vector3.UnitY * (1f + shift))),
        ];
        var late = At(shift: 0.75f);
        var whole = SdfMeshBakeGroup.Plan(create: Create, farDistance: 50f, program: program, transforms: late);
        var refreshed = SdfMeshBakeGroup.Plan(create: Create, farDistance: 50f, program: program, transforms: At(shift: 0f));
        var planned = refreshed.ToArray();
        // Every group but the dynamic group of slot 0 is named.
        var count = SdfMeshBakeGroup.Refresh(
            create: Create,
            farDistance: 50f,
            groups: refreshed,
            program: program,
            stale: static group => !(group.Dynamic && (group.Slot == 0)),
            transforms: late
        );

        Assert.Contains(collection: whole, filter: static group => group.IsWorld);
        Assert.Contains(collection: whole, filter: static group => (group.Instances > 1));
        Assert.Equal(expected: whole.Count, actual: refreshed.Count);
        // The world group, slot 1's group and the static group reading slot 0.
        Assert.Equal(expected: 3, actual: count);

        for (var index = 0; (index < whole.Count); index++) {
            var stays = ((whole[index].Dynamic && (whole[index].Slot == 0)) || (!whole[index].Dynamic && !planned[index].Geometry.ReadsSlots));

            Assert.Equal(expected: Shape(group: (stays ? planned[index] : whole[index])), actual: Shape(group: refreshed[index]));
            Assert.Equal(expected: stays, actual: ReferenceEquals(objA: planned[index], objB: refreshed[index]));
        }

        // A static group reading no slot plans alike at both transforms; the dynamic group of slot 0 does not.
        Assert.Contains(collection: Enumerable.Range(start: 0, count: whole.Count), filter: index => (!whole[index].Dynamic && !planned[index].Geometry.ReadsSlots && (Shape(group: whole[index]) == Shape(group: planned[index]))));
        Assert.Contains(collection: Enumerable.Range(start: 0, count: whole.Count), filter: index => (whole[index].Dynamic && (whole[index].Slot == 0) && (Shape(group: whole[index]) != Shape(group: planned[index]))));
    }
}
