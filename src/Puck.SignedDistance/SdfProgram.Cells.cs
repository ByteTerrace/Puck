using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // CellDisplace adds to the accumulator in field units; it does not inherit a shape's
    // distanceScale. Bound the sampling coordinates separately and add their derivative
    // at the instruction, including shape-free chains and previously composed seams.
    private static float[] AnalyzeCellPointFactors(SdfInstruction[] instructions) {
        if (!Array.Exists(
            array: instructions,
            match: static i => (i.Op == SdfOp.CellDisplace)
        )) { return []; }
        var factors = new float[instructions.Length];
        var factor = 1f;
        SdfOp? unsupported = null;

        for (var index = 0; (index < instructions.Length); index++) {
            var instruction = instructions[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                    factor = 1f;
                    unsupported = null;
                    break;
                case SdfOp.Scale:
                    // A mirror (SdfProgramBuilder.MirrorX) carries its sign in the scale; the cell factor is a length.
                    factor /= MathF.Min(
                        x: MathF.Abs(x: instruction.Data0.X),
                        y: MathF.Min(
                            x: MathF.Abs(x: instruction.Data0.Y),
                            y: MathF.Abs(x: instruction.Data0.Z)
                        )
                    );
                    break;
                case SdfOp.DomainWarp:
                    factor *= DisplaceWarpLipschitz(instruction: instruction);
                    break;
                case SdfOp.GaussianPush:
                    factor *= GaussianPushLipschitz(
                        new(
                            x: instruction.Data0.W,
                            y: instruction.Data1.W,
                            z: BitConverter.UInt32BitsToSingle(value: instruction.Shape)
                        ),
                        new Vector3(
                            x: instruction.Data1.X,
                            y: instruction.Data1.Y,
                            z: instruction.Data1.Z
                        )
                    );
                    break;
                case SdfOp.Translate:
                case SdfOp.Rotate:
                case SdfOp.TransformDynamic:
                case SdfOp.SymmetryPlane:
                case SdfOp.Elongate:
                case SdfOp.ShapeBlend:
                case SdfOp.Onion:
                case SdfOp.Dilate:
                case SdfOp.Displace:
                case SdfOp.NoiseDisplace:
                case SdfOp.LaneErode:
                case SdfOp.PushField:
                case SdfOp.PopField:
                    break;
                default:
                    unsupported ??= instruction.Op;
                    break;
                case SdfOp.CellDisplace:
                    if (unsupported is { } op) {
                        throw new ArgumentException(message: $"CellDisplace at instruction {index} follows {op}, whose sampling coordinates lack a global continuous derivative bound. ResetPoint and restore the shape's rigid frame before cellular relief.");
                    }
                    if (!float.IsFinite(f: factor)) {
                        throw new ArgumentException(message: $"CellDisplace at instruction {index} has a nonfinite coordinate derivative bound.");
                    }
                    factors[index] = factor;
                    break;
            }
        }
        return factors;
    }
}
