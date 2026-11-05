using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfProgramSideTableLawTests {
    [Fact]
    public void SampledSnapshotsSurviveProducerMutationRelocationAndExcerpt() {
        float[] values = [-1, 1, 2, 3, 4, 5, 6, 7];
        var expected = values.ToArray();
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        var mark = builder.MarkEmission();
        builder.BeginInstance(Vector3.One, 2).ResetPoint()
            .SampledValues(Vector3.Zero, 1, 2, 2, 2, 0, material, values).EndInstance();
        var retained = builder.RetainEmission(mark);
        var capacity = builder.MeasureCapacity();
        values[0] = 99;
        var original = builder.Build();
        Assert.True(capacity.Words >= original.PartCompilationWordCapacity);
        builder.Clear();
        builder.AddMaterial(new(Vector3.One));
        builder.BeginInstance(Vector3.Zero, 1).ResetPoint().Sphere(1, material).EndInstance();
        builder.AppendEmission(retained);
        var relocated = builder.Build();
        var excerpt = relocated.Excerpt(Enumerable.Range(0, relocated.Instructions.Count).ToArray());
        foreach (var program in new[] { original, relocated, excerpt }) {
            var index = Enumerable.Range(0, program.Instructions.Count)
                .Single(i => program.Instructions[i].Op == SdfOp.ShapeBlend &&
                    (SdfShapeType)program.Instructions[i].Shape == SdfShapeType.SampledRegion);
            Assert.Equal(expected, program.SideTableValues(index).ToArray());
            var address = BitConverter.SingleToUInt32Bits(program.Instructions[index].Data1.Z);
            Assert.NotEqual(0u, address & 0x80000000u);
            var words = program.Words.Slice((int)(address & 0x7fffffffu), expected.Length).ToArray();
            Assert.Equal(expected, words.Select(BitConverter.UInt32BitsToSingle).ToArray());
        }
    }

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
