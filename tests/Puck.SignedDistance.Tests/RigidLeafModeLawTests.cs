using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Checks that compiled rigid leaves retain the original mode-bearing shape header. These are host
/// packing laws; executing the mode gates and comparing rendered fields requires the GPU evaluator.</summary>
public sealed class RigidLeafModeLawTests {
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void RigidLeavesKeepModeFlagsAndDynamicSlot(bool detail, bool secondary, bool dynamic) {
        var instructions = new List<SdfInstruction> { Instruction(op: SdfOp.ResetPoint) };

        if (dynamic) {
            instructions.Add(item: Instruction(op: SdfOp.TransformDynamic, data0: new Vector4(7f, 0f, 0f, 0f)));
        }

        instructions.Add(item: Instruction(op: SdfOp.Translate, data0: new Vector4(2f, 3f, 4f, 0f)));
        instructions.Add(item: Instruction(op: SdfOp.Scale, data0: Vector4.One));
        var shapeIndex = instructions.Count;

        instructions.Add(item: Instruction(op: SdfOp.ShapeBlend, data0: new Vector4(1f, 0f, 0f, 0f)) with {
            Detail = detail,
            Secondary = secondary,
            Shape = (uint)SdfShapeType.Sphere
        });

        var words = Build(instructions: instructions).Words;
        var segmentHeader = SegmentHeader(words: words);

        Assert.Equal(expected: 1u, actual: words[segmentHeader]);
        var segmentMode = words[segmentHeader + 8];

        Assert.NotEqual(expected: 0u, actual: segmentMode & 0x80000000u);
        var plan = checked((int)words[segmentHeader + 2] * 4);

        Assert.Equal(expected: 1u, actual: words[plan + 1]);
        Assert.Equal(expected: dynamic ? 8u : 0u, actual: words[plan + 2]);
        var leaf = checked((int)words[plan] * 4);
        var referencedShape = words[leaf + 3] & 0x7FFFFFFFu;

        Assert.Equal(expected: (uint)shapeIndex, actual: referencedShape);
        var expectedHeader = (uint)SdfShapeType.Sphere | (detail ? 0x80000000u : 0u) | (secondary ? 0u : 0x40000000u);

        Assert.Equal(expected: expectedHeader, actual: words[checked((int)(referencedShape + 1) * 4 + 1)]);
    }

    [Fact]
    public void FlagSupportDoesNotCompileNonRigidScale() {
        var words = Build(instructions: [
            Instruction(op: SdfOp.ResetPoint),
            Instruction(op: SdfOp.Scale, data0: new Vector4(2f, 3f, 4f, 0f)),
            Instruction(op: SdfOp.ShapeBlend, data0: new Vector4(1f, 0f, 0f, 0f)) with {
                Detail = true,
                Secondary = false,
                Shape = (uint)SdfShapeType.Sphere
            }
        ]).Words;
        var segmentHeader = SegmentHeader(words: words);

        Assert.Equal(expected: 1u, actual: words[segmentHeader]);
        Assert.Equal(expected: 0u, actual: words[segmentHeader + 8] & 0x80000000u);
        var plan = checked((int)words[segmentHeader + 2] * 4);

        Assert.Equal(expected: 0u, actual: words[plan + 1]);
    }

    private static int SegmentHeader(ReadOnlySpan<uint> words) => checked((int)(words[3] + 20u * words[1] + 2u * words[0]) * 4);

    private static SdfProgram Build(IReadOnlyList<SdfInstruction> instructions) => new(
        instances: null,
        instructions: instructions,
        materials: [new SdfMaterial(Albedo: Vector3.One)],
        screenSurfaces: null
    );

    private static SdfInstruction Instruction(SdfOp op, Vector4 data0 = default) => new(
        Blend: (uint)SdfBlendOp.Union,
        Data0: data0,
        Data1: Vector4.Zero,
        Material: 0u,
        Op: op,
        Shape: 0u
    );
}
