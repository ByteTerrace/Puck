using System.Text;
using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: loading CURV installs exact splines before a row can ask for their derivation.</summary>
public sealed class CurvatureChunkLawTests {
    [Fact]
    public void LoadingSeedsBothTheRowAndTheSharedShapeWithoutDeriving() {
        var row = new WorldCurveRow("cached", [
            new WorldCurveKnot(new DocumentVector3(x: 317, y: 1, z: 0), 0, 0),
            new WorldCurveKnot(new DocumentVector3(x: 321, y: 3, z: 0), 0, 0),
        ]);
        // Compile the Maths primitive directly: the World's row and shape caches have never seen this curve.
        var expected = CurvatureSpline.Compile([
            new CurvatureSplineKnot(X: FixedQ4816.FromInteger(value: 317), Z: FixedQ4816.Zero, Elevation: FixedQ4816.One, TangentYaw: FixedQ4816.Zero, Curvature: FixedQ4816.Zero),
            new CurvatureSplineKnot(X: FixedQ4816.FromInteger(value: 321), Z: FixedQ4816.Zero, Elevation: FixedQ4816.FromInteger(value: 3), TangentYaw: FixedQ4816.Zero, Curvature: FixedQ4816.Zero),
        ], closed: false);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(value: 1);
        expected.Write(writer: writer);
        writer.Flush();
        var definition = new WorldDefinition(CurvesRaw: [row]);
        var context = new CompiledWorldContext(definition, "boot", "curvature-law") { Drawn = definition };
        var work = new WorldBootWork();
        using var attribution = WorldBootWork.Attribute(work: work);

        Assert.True(condition: CurvatureChunk.Instance.TryLoad(context: context, payload: stream.ToArray(), reason: out var reason), userMessage: reason);
        var actual = row.Compiled;

        Assert.Same(actual, (row with { Name = "another-instance" }).Compiled);
        Assert.Equal(0, work.Read(kind: WorldBootWork.CurveCompiles));
        Assert.Equal(expected.TotalLengthRaw, actual.TotalLengthRaw);
        for (var index = 0; (index <= 32); index++) {
            var station = ((expected.TotalLengthRaw * index) / 32);

            Assert.Equal(expected.EvaluateRaw(arcRaw: station), actual.EvaluateRaw(arcRaw: station));
        }
    }
}
