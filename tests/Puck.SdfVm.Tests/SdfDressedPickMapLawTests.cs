using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a visibility identity's mesh source indexes the dressed draw list (the host's draws, then one draw per baked
/// group, then the portal-straddle copies), and the dressed map names what each draws: a host draw as the host's map
/// does, a baked group through its first instance, a copy through what it was copied from, and a draw naming nothing,
/// the world group's included, as nothing.
/// </summary>
public sealed class SdfDressedPickMapLawTests {
    private sealed class HostMap : ISdfPickMap {
        public object? Resolve(uint identity) => (SdfVisibility.KindOf(identity: identity) switch {
            SdfVisibilityKind.Sdf => $"instance{SdfVisibility.SourceOf(identity: identity) - 1}",
            SdfVisibilityKind.Mesh => $"host{SdfVisibility.SourceOf(identity: identity)}",
            _ => null,
        });
        public string? MaterialName(uint identity, int material) => ((Resolve(identity: identity) is string target) ? $"{target}/{material}" : null);
    }

    private static readonly SdfMesh Triangle = new(
        indices: new uint[] { 0, 1, 2 },
        positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }
    );

    private static SdfMeshDraw Draw(object identity, int? slot = null) => new(Identity: identity, Material: 0, Mesh: Triangle, ObjectToWorld: Matrix4x4.Identity) { FrameSlot = slot };
    private static uint Mesh(uint source) => SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: source);

    [Fact]
    public void ADrawNamesWhatItDrawsWhetherTheHostListedItOrTheBakeAddedIt() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.BeginInstance(bakesWithPrevious: false, boundCenter: Vector3.Zero, boundRadius: 0.6f).ResetPoint().Sphere(material: material, radius: 0.5f).EndInstance()
            .BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 0.6f).ResetPoint().TransformDynamic(slot: 0).Sphere(material: material, radius: 0.5f).EndInstance();

        var program = builder.Build();
        var transforms = new DynamicTransform[] { new(Orientation: Quaternion.Identity, Position: Vector3.Zero) };
        var groups = SdfMeshBakeGroup.Plan(create: static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector), farDistance: 100f, program: program, transforms: transforms);
        var staticGroup = groups.Single(predicate: static group => !group.Dynamic);
        var dynamicGroup = groups.Single(predicate: static group => group.Dynamic);
        var world = new SdfMeshBakeGroup(dynamic: false, selector: SdfMeshBakeGroup.WorldSelector);

        Assert.Equal(expected: (0, 1), actual: (staticGroup.FirstInstance, dynamicGroup.FirstInstance));
        Assert.Equal(expected: -1, actual: world.FirstInstance);

        // Two host draws, the first in the straddling slot; then the baked draws; then the straddle's copies.
        var draws = new List<SdfMeshDraw> {
            Draw(identity: "wall"), Draw(identity: "statue", slot: 5),
            Draw(identity: staticGroup), Draw(identity: dynamicGroup, slot: 5), Draw(identity: world),
        };

        SdfSlotClip.Apply(
            clips: [new SdfSlotClip(CopyPlane: Vector4.UnitY, CopyRotation: Quaternion.Identity, CopyTranslation: Vector3.Zero, FirstSlot: 5, Plane: Vector4.UnitY, SlotCount: 1)],
            draws: draws
        );

        var map = new SdfDressedPickMap(draws: draws, hostDraws: 2, inner: new HostMap());

        Assert.Equal(expected: 7, actual: draws.Count);
        Assert.Equal(expected: ["host0", "host1", "instance0", "instance1", null], actual: Enumerable.Range(start: 0, count: 5).Select(selector: index => map.Resolve(identity: Mesh(source: (uint)index))));
        // The copies: of the host's statue (draw 1), then of the dynamic group (draw 3).
        Assert.Equal(expected: ["host1", "instance1"], actual: new[] { map.Resolve(identity: Mesh(source: 5)), map.Resolve(identity: Mesh(source: 6)) });
        Assert.Null(@object: map.Resolve(identity: Mesh(source: 7)));
        Assert.Equal(expected: "instance1", actual: map.Resolve(identity: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: 2)));
        Assert.Equal(expected: "instance0/4", actual: map.MaterialName(identity: Mesh(source: 2), material: 4));
        Assert.Null(@object: map.Resolve(identity: 0u));
        Assert.Equal(expected: 2, actual: map.HostDraws);

        // A copy whose original the host never listed names nothing.
        var orphan = new SdfDressedPickMap(draws: [Draw(identity: "wall"), Draw(identity: staticGroup), SdfSlotCopy(of: "gone")], hostDraws: 1, inner: new HostMap());

        Assert.Null(@object: orphan.Resolve(identity: Mesh(source: 2)));
    }
    [Fact]
    public void TheMapMatchesTheListItWasMadeFromAndNoOther() {
        var map = new SdfDressedPickMap(draws: [Draw(identity: "wall"), Draw(identity: "floor")], hostDraws: 2, inner: new HostMap());

        Assert.True(condition: map.Matches(draws: [Draw(identity: "wall"), Draw(identity: "floor")]));
        Assert.False(condition: map.Matches(draws: [Draw(identity: "wall")]));
        Assert.False(condition: map.Matches(draws: [Draw(identity: "wall"), Draw(identity: "roof")]));
    }

    // A copy of an identity no host draw carries, as the straddle makes one.
    private static SdfMeshDraw SdfSlotCopy(object of) {
        var draws = new List<SdfMeshDraw> { Draw(identity: of, slot: 0) };

        SdfSlotClip.Apply(clips: [new SdfSlotClip(CopyPlane: Vector4.UnitY, CopyRotation: Quaternion.Identity, CopyTranslation: Vector3.Zero, FirstSlot: 0, Plane: Vector4.UnitY, SlotCount: 1)], draws: draws);

        return draws[1];
    }
}
