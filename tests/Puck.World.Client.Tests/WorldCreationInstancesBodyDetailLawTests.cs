using System.Numerics;

using Puck.SignedDistance;
using Puck.SignedDistance.Baking;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: a pooled body's instance declares the bake detail its round sections ask for at the look's scale
/// (<see cref="SdfInstanceRange.Detail"/>, <see cref="SdfSampledLattice.RoundCell"/>: a third of the radius, or the cell
/// whose chord keeps the outline within the silhouette error, whichever is finer); a flat-faced shape declares none, and a
/// blend group declares each member's own detail as a part of its own, the group's instance carrying the finest.
/// </summary>
public sealed class WorldCreationInstancesBodyDetailLawTests {
    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 scale, int group = 0) => new(
        Id: id,
        Name: null,
        Type: type,
        Position: new Vector3(x: (0.2f * id), y: 0f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: scale,
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: group
    );

    [Fact]
    public void EachRoundSectionDeclaresItsRoundCellAndAGroupItsFinestMember() {
        ShapeDocument[] shapes = [
            Shape(id: 1, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.Torus),
            Shape(id: 2, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.RoundCone),
            (Shape(id: 3, scale: new Vector3(x: 0.02f, y: 0.03f, z: 0.1f), type: SdfSolidPrimitive.Superellipsoid) with { Exponent = 4f }),
            (Shape(id: 4, scale: new Vector3(x: 0.02f, y: 0.03f, z: 0.1f), type: SdfSolidPrimitive.Prism) with { Profile = new SdfPrismProfile(Kind: SdfPrismProfileKind.Ellipse) }),
            Shape(id: 5, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.Box),
            Shape(group: 1, id: 6, scale: new Vector3(value: 0.03f), type: SdfSolidPrimitive.Sphere),
            Shape(group: 1, id: 7, scale: new Vector3(x: 0.006f, y: 0.05f, z: 0.006f), type: SdfSolidPrimitive.Capsule),
        ];
        var program = CreationFixtures.EmitPool(
            bodyScale: 1f,
            name: "detail",
            shapes: shapes
        );
        var details = program.Instances
            .Where(predicate: static instance => (instance.Active && instance.IsDynamic))
            .Select(selector: static instance => instance.Detail)
            .Order()
            .ToArray();
        var superellipsoid = (0.03f * (SdfProgramBuilder.MinSuperellipsoidExponent / 4f));

        Assert.Equal(
            [
                0f,
                SdfSampledLattice.RoundCell(radius: 0.006f),
                SdfSampledLattice.RoundCell(radius: (0.4f * 0.03f)),
                SdfSampledLattice.RoundCell(radius: ((0.02f * 0.02f) / 0.03f)),
                SdfSampledLattice.RoundCell(radius: superellipsoid),
                SdfSampledLattice.RoundCell(radius: (0.5f * 0.03f)),
            ],
            details,
            new FloatTolerance()
        );
    }

    private sealed class FloatTolerance : IEqualityComparer<float> {
        public bool Equals(float x, float y) => (MathF.Abs((x - y)) <= 1.0e-6f);
        public int GetHashCode(float obj) => 0;
    }
}
