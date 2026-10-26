using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class RetainedEmissionLawTests {
    [InlineData(0)]
    [InlineData(3)]
    [Theory]
    public void RetainedInstancesRelocateEveryProfileWithoutChangingPackedWords(int prefix) {
        static SdfProgramBuilder Builder(int count) {
            var builder = new SdfProgramBuilder();

            builder.AddMaterial(material: new SdfMaterial(Vector3.One));
            for (var i = 0; (i < count); i++) {
                builder.BeginInstance(Vector3.Zero, 5).ResetPoint().Sphere(1, 0).EndInstance();
            }
            return builder;
        }
        static void Emit(SdfProgramBuilder builder) {
            builder.BeginInstance(Vector3.Zero, 5).ResetPoint().ConvexPolygon(
                [new(x: -1, y: -1), new(x: -1, y: 1), new(x: 1, y: 1), new(x: 1, y: -1)], 0, SdfLift.Extrude, 0.2f, 0).EndInstance();
            var path = new SdfPathProfile([new SdfPathContour(new(x: -1, y: -1), [new(new(x: 1, y: -1)), new(new(x: 1, y: 1)), new(new(x: -1, y: 1))])]);

            builder.BeginInstance(Vector3.Zero, 5).ResetPoint().Path(path, Vector2.One, 0.2f, 0).EndInstance();
            builder.BeginInstance(Vector3.Zero, 5).ResetPoint().Sweep(Vector3.Zero, Vector3.UnitY, Vector3.One, 0.1f, 0.1f, 0, 1, 0, 0, 0).EndInstance();
        }
        var source = Builder(count: 1);
        var mark = source.MarkEmission();

        Emit(builder: source);
        var retained = source.RetainEmission(mark: mark);
        var replay = Builder(count: prefix);

        replay.AppendEmission(emission: retained);
        var direct = Builder(count: prefix);

        Emit(builder: direct);
        Assert.Equal(direct.Build().Words.ToArray(), replay.Build().Words.ToArray());
    }
}
