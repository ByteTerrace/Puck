using System.Numerics;

using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Checks that compiled rigid leaves retain the original mode-bearing shape header. These are host
/// packing laws; executing the mode gates and comparing rendered fields requires the GPU evaluator.</summary>
public sealed class RigidLeafModeLawTests {
    private static SdfProgram Build(IReadOnlyList<SdfInstruction> instructions) => new(
        instances: null,
        instructions: instructions,
        materials: [new SdfMaterial(Albedo: Vector3.One)],
        screenSurfaces: null
    );
    private static SdfInstruction Instruction(SdfOp op, Vector4 data0 = default) => new(
        Blend: ((uint)SdfBlendOp.Union),
        Data0: data0,
        Data1: Vector4.Zero,
        Material: 0u,
        Op: op,
        Shape: 0u
    );
    private static int SegmentHeader(ReadOnlySpan<uint> words) => checked((((int)((words[3] + (20u * words[1])) + (2u * words[0]))) * 4));

    [Fact]
    public void FlagSupportDoesNotCompileNonRigidScale() {
        var words = Build(instructions: [
            Instruction(op: SdfOp.ResetPoint),
            Instruction(
                op: SdfOp.Scale,
                data0: new Vector4(
                    w: 0f,
                    x: 2f,
                    y: 3f,
                    z: 4f
                )
            ),
            Instruction(
                op: SdfOp.ShapeBlend,
                data0: new Vector4(
                    w: 0f,
                    x: 1f,
                    y: 0f,
                    z: 0f
                )
            ) with {
                Detail = true,
                Secondary = false,
                Shape = ((uint)SdfShapeType.Sphere),
            }
        ]).Words;
        var segmentHeader = SegmentHeader(words: words);

        Assert.Equal(
            expected: 1u,
            actual: words[segmentHeader]
        );
        Assert.Equal(
            expected: 0u,
            actual: words[(segmentHeader + 8)] & 0x80000000u
        );
        var plan = checked((((int)words[(segmentHeader + 2)]) * 4));

        Assert.Equal(
            expected: 0u,
            actual: words[(plan + 1)]
        );
    }
    [Fact]
    public void FoldRunCompilesIntoAFoldedLeafAndItsExtensionSlot() {
        var foldFirst = 2;
        var words = Build(instructions: [
            Instruction(op: SdfOp.ResetPoint),
            Instruction(
                op: SdfOp.Translate,
                data0: new Vector4(w: 0f, x: 5f, y: 6f, z: 7f)
            ),
            Instruction(
                op: SdfOp.SymmetryPlane,
                data0: new Vector4(w: 0f, x: 0f, y: 1f, z: 0f)
            ),
            // A domain repeat shifts by its origin around the fold: the run ends at the fold, and the pose after it takes the shift back.
            Instruction(op: SdfOp.Translate),
            Instruction(
                op: SdfOp.RepeatLimited,
                data0: new Vector4(w: 0f, x: 0.5f, y: 1f, z: 1f)
            ) with {
                Data1 = new Vector4(w: 0f, x: 3f, y: 0f, z: 0f),
            },
            Instruction(op: SdfOp.Translate),
            Instruction(
                op: SdfOp.Translate,
                data0: new Vector4(w: 0f, x: 1f, y: 2f, z: 3f)
            ),
            Instruction(
                op: SdfOp.ShapeBlend,
                data0: new Vector4(w: 0f, x: 0.25f, y: 0f, z: 0f)
            ) with {
                Shape = ((uint)SdfShapeType.Sphere),
            }
        ]).Words;
        var segmentHeader = SegmentHeader(words: words);

        Assert.NotEqual(
            actual: words[(segmentHeader + 8)] & 0x80000000u,
            expected: 0u
        );
        var plan = checked((((int)words[(segmentHeader + 2)]) * 4));

        // The folded leaf and its extension slot.
        Assert.Equal(
            expected: 2u,
            actual: words[(plan + 1)]
        );
        var leaf = checked((((int)words[plan]) * 4));

        Assert.Equal(
            expected: 0x40000000u,
            actual: words[(leaf + 3)] & 0x40000000u
        );
        Assert.Equal(
            expected: 7u,
            actual: words[(leaf + 3)] & SdfProgram.RigidLeafShapeMask
        );
        // The pose after the folds starts at identity; the pose before them is the extension's.
        Assert.Equal(
            expected: 1f,
            actual: BitConverter.UInt32BitsToSingle(value: words[leaf])
        );
        Assert.Equal(
            expected: -1f,
            actual: BitConverter.UInt32BitsToSingle(value: words[((leaf + 8) + 3)])
        );
        var extension = (leaf + 12);

        Assert.Equal(
            expected: 5f,
            actual: BitConverter.UInt32BitsToSingle(value: words[extension])
        );
        Assert.Equal(
            expected: ((uint)foldFirst) | 0x80000000u,
            actual: words[(extension + 3)]
        );
        Assert.Equal(
            expected: 3u,
            actual: words[(extension + 8)]
        );
    }
    [Fact]
    public void AFoldOnADynamicChainStaysGeneric() {
        var words = Build(instructions: [
            Instruction(op: SdfOp.ResetPoint),
            Instruction(
                op: SdfOp.TransformDynamic,
                data0: new Vector4(w: 0f, x: 3f, y: 0f, z: 0f)
            ),
            Instruction(
                op: SdfOp.SymmetryPlane,
                data0: new Vector4(w: 0f, x: 1f, y: 0f, z: 0f)
            ),
            Instruction(
                op: SdfOp.ShapeBlend,
                data0: new Vector4(w: 0f, x: 0.25f, y: 0f, z: 0f)
            ) with {
                Shape = ((uint)SdfShapeType.Sphere),
            }
        ]).Words;
        var segmentHeader = SegmentHeader(words: words);

        Assert.Equal(
            expected: 0u,
            actual: words[(segmentHeader + 8)] & 0x80000000u
        );
    }
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [Theory]
    public void RigidLeavesKeepModeFlagsAndDynamicSlot(bool detail, bool secondary, bool dynamic) {
        var instructions = new List<SdfInstruction> { Instruction(op: SdfOp.ResetPoint) };

        if (dynamic) {
            instructions.Add(item: Instruction(
                op: SdfOp.TransformDynamic,
                data0: new Vector4(
                    w: 0f,
                    x: 7f,
                    y: 0f,
                    z: 0f
                )
            ));
        }

        instructions.Add(item: Instruction(
            op: SdfOp.Translate,
            data0: new Vector4(
                w: 0f,
                x: 2f,
                y: 3f,
                z: 4f
            )
        ));
        instructions.Add(item: Instruction(
            op: SdfOp.Scale,
            data0: Vector4.One
        ));
        var shapeIndex = instructions.Count;

        instructions.Add(item: Instruction(
            op: SdfOp.ShapeBlend,
            data0: new Vector4(
                w: 0f,
                x: 1f,
                y: 0f,
                z: 0f
            )
        ) with {
            Detail = detail,
            Secondary = secondary,
            Shape = ((uint)SdfShapeType.Sphere),
        });

        var words = Build(instructions: instructions).Words;
        var segmentHeader = SegmentHeader(words: words);

        Assert.Equal(
            expected: 1u,
            actual: words[segmentHeader]
        );
        var segmentMode = words[(segmentHeader + 8)];

        Assert.NotEqual(
            actual: segmentMode & 0x80000000u,
            expected: 0u
        );
        var plan = checked((((int)words[(segmentHeader + 2)]) * 4));

        Assert.Equal(
            expected: 1u,
            actual: words[(plan + 1)]
        );
        Assert.Equal(
            expected: (dynamic
            ? 8u
            : 0u),
            actual: words[(plan + 2)]
        );
        var leaf = checked((((int)words[plan]) * 4));
        var referencedShape = words[(leaf + 3)] & 0x7FFFFFFFu;

        Assert.Equal(
            actual: referencedShape,
            expected: ((uint)shapeIndex)
        );
        var expectedHeader = ((uint)SdfShapeType.Sphere) | (detail
            ? 0x80000000u
            : 0u) | (secondary
            ? 0u
            : 0x40000000u
        );

        Assert.Equal(
            expected: expectedHeader,
            actual: words[checked(((((int)(referencedShape + 1)) * 4) + 1))]
        );
    }
}
