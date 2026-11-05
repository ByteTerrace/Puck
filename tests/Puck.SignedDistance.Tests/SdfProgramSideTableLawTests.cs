using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfProgramSideTableLawTests {
    // A sampled instruction names its field by a handle, never its values: no voxel enters the packed words, so a field
    // publishing new values moves no program word, while the program's set and a fresh capture hold the newest snapshot.
    // Relocation and excerpt rebind the same field.
    [Fact]
    public void ASampledFieldsValuesStayOutOfTheProgramWordsThroughPublishingRelocationAndExcerpt() {
        float[] values = [-1, 1, 2, 3, 4, 5, 6, 7];
        var field = new SdfSampledField(dimX: 2, dimY: 2, dimZ: 2, values: values);
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new(Vector3.One));
        var mark = builder.MarkEmission();
        builder.BeginInstance(Vector3.One, 2).ResetPoint()
            .SampledValues(boxMin: Vector3.Zero, cellSize: 1f, field: field, boundaryFloor: 0f, material: material).EndInstance();
        var retained = builder.RetainEmission(mark);
        var capacity = builder.MeasureCapacity();
        values[0] = 99;
        var original = builder.Build();
        Assert.True(capacity.Words >= original.PartCompilationWordCapacity);
        Assert.Equal(-1f, original.Sampled[0].Values[0]);
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
            var handle = (int)BitConverter.SingleToUInt32Bits(program.Instructions[index].Data1.Z);

            Assert.Same(field, program.SampledFields[handle]);
            Assert.True(program.SideTableValues(index).IsEmpty);
        }

        var words = original.Words.ToArray();
        var published = field.Publish(values: [5, 5, 5, 5, 5, 5, 5, 5]);
        var captured = SdfSampledSet.Capture(fields: original.SampledFields, previous: original.Sampled);

        Assert.Equal(words, original.Words.ToArray());
        Assert.NotSame(original.Sampled, captured);
        Assert.Same(published, captured[0]);
        Assert.Same(captured, SdfSampledSet.Capture(fields: original.SampledFields, previous: captured));
        _ = Assert.Throws<ArgumentException>(() => field.Publish(values: [1, 2, 3]));
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
