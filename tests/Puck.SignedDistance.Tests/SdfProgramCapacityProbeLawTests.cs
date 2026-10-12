using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Laws for the capacity probe's measurement (<see cref="SdfProgramBuilder.MeasureCapacity"/>), which reads a
/// builder's emission without building it: its words never fall below the built program's
/// <see cref="SdfProgram.PartCompilationWordCapacity"/>, and its instances are the built program's.</summary>
public sealed class SdfProgramCapacityProbeLawTests {
    // Instances of translated, rotated and scaled shapes beside a reserved path table, which only a probe reserves.
    private static SdfProgramBuilder Instanced() {
        var builder = new SdfProgramBuilder();
        var stone = builder.AddMaterial(material: new(Vector3.One));
        var moss = builder.AddMaterial(material: new(new Vector3(x: 0.2f, y: 0.6f, z: 0.2f)));

        builder.ReservePathTables(shapeCount: 3);
        for (var index = 0; (index < 6); index++) {
            _ = builder.BeginInstance(boundCenter: new Vector3(x: (index * 3f), y: 0f, z: 0f), boundRadius: 2f);
            _ = builder.ResetPoint();
            _ = builder.Translate(offset: new Vector3(x: (index * 3f), y: 1f, z: 0.5f));
            _ = builder.Rotate(rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (0.3f * (index + 1))));
            _ = builder.Box(halfExtents: new Vector3(x: 0.5f, y: 0.75f, z: 0.5f), material: stone, round: 0.05f);
            _ = builder.Sphere(blend: SdfBlendOp.SmoothUnion, material: moss, radius: 0.6f, smooth: 0.1f);
            _ = builder.EndInstance();
        }
        return builder;
    }
    // World segments only, with the side tables a polygon and a sweep add and a wallpaper fold's leaf extension.
    private static SdfProgramBuilder World() {
        var builder = new SdfProgramBuilder();
        var stone = builder.AddMaterial(material: new(Vector3.One));

        _ = builder.ResetPoint();
        _ = builder.Box(halfExtents: new Vector3(x: 4f, y: 0.25f, z: 4f), material: stone, round: 0f);
        _ = builder.ResetPoint();
        _ = builder.Translate(offset: new Vector3(x: 0f, y: 1f, z: 0f));
        _ = builder.ConvexPolygon(cornerRadius: 0f, lift: SdfLift.Extrude, liftAmount: 0.5f, material: stone, vertices: [new(x: 0f, y: 0f), new(x: 0.5f, y: 1f), new(x: 1f, y: 0f)]);
        _ = builder.ResetPoint();
        _ = builder.Sweep(a: Vector3.Zero, b: new Vector3(x: 1f, y: 2f, z: 0f), bulge: 0f, c: new Vector3(x: 2f, y: 0f, z: 0f), material: stone, radiusEnd: 0.1f, radiusStart: 0.2f, strandOffset: 0f, strands: 1, twist: 0f);
        _ = builder.ResetPoint();
        _ = builder.WallpaperFold(cell: new Vector2(x: 2f, y: 2f), group: SdfWallpaperGroup.P4M, limit: new Vector2(x: 3f, y: 3f));
        _ = builder.Sphere(material: stone, radius: 0.4f);

        return builder;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheMeasuredCapacityBoundsTheBuiltProgramsAndCountsItsInstances(int scene) {
        var builder = ((scene == 0) ? Instanced() : World());
        var measured = builder.MeasureCapacity();
        var built = builder.Build();

        Assert.InRange(actual: built.PartCompilationWordCapacity, high: measured.Words, low: 1);
        Assert.Equal(actual: measured.Instances, expected: built.Instances.Count);
    }
    [Fact]
    public void AnEmptyBuilderMeasuresAtLeastItsBuiltProgram() {
        var builder = new SdfProgramBuilder();

        Assert.True(condition: (builder.MeasureCapacity().Words >= builder.Build().PartCompilationWordCapacity));
    }
}
