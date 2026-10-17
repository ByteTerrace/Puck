using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfProgramSideTableLawTests {
    [Fact]
    public void EachSideTableShapeReadsItsEntrysValuesWhereverItsProgramPackedIt() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        Vector2[] triangle = [new(x: 0f, y: 0.5f), new(x: 0.5f, y: -0.5f), new(x: -0.5f, y: -0.5f)];

        _ = builder.Sphere(material: material, radius: 0.5f)
            .ConvexPolygon(triangle, 0f, SdfLift.Extrude, 0.25f, material)
            .Sweep(a: Vector3.Zero, b: Vector3.UnitY, bulge: 0.1f, c: (2f * Vector3.UnitY), material: material, radiusEnd: 0.2f, radiusStart: 0.3f, strandOffset: 0f, strands: 1, twist: 0f)
            .Path(new([new(new(x: -0.5f, y: -0.5f), [new(new(x: 0.5f, y: -0.5f)), new(new(x: 0.5f, y: 0.5f)), new(new(x: -0.5f, y: 0.5f))])]), Vector2.One, 0.04f, material);

        var program = builder.Build();
        var shapes = Enumerable.Range(count: program.Instructions.Count, start: 0)
            .Where(predicate: index => (program.Instructions[index].Op == SdfOp.ShapeBlend))
            .ToDictionary(elementSelector: index => index, keySelector: index => (SdfShapeType)program.Instructions[index].Shape);

        Assert.True(condition: program.SideTableValues(instructionIndex: shapes[SdfShapeType.Sphere]).IsEmpty);
        Assert.Equal(
            actual: program.SideTableValues(instructionIndex: shapes[SdfShapeType.ConvexPolygon]).ToArray(),
            expected: [0f, 0.5f, 0.5f, -0.5f, -0.5f, -0.5f]
        );
        Assert.Equal(
            actual: program.SideTableValues(instructionIndex: shapes[SdfShapeType.Sweep]).ToArray(),
            expected: [0f, 0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f, 0.3f, 0.2f, 0.1f]
        );
        // Four edges of A, B and the radius at each.
        Assert.Equal(expected: 24, actual: program.SideTableValues(instructionIndex: shapes[SdfShapeType.Path]).Length);
    }
}
