using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // CellDisplace adds to the accumulator in field units; it does not inherit a shape's
    // distanceScale. Bound the sampling coordinates separately and add their derivative
    // at the instruction, including shape-free chains and previously composed seams.
    private static float[] AnalyzeCellPointFactors(SdfInstruction[] instructions) {
        if (!Array.Exists(instructions, static i => i.Op == SdfOp.CellDisplace)) { return []; }
        var factors = new float[instructions.Length];
        var factor = 1f;
        SdfOp? unsupported = null;
        for (var index = 0; index < instructions.Length; index++) {
            var instruction = instructions[index];
            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                    factor = 1f;
                    unsupported = null;
                    break;
                case SdfOp.Scale:
                    factor /= MathF.Min(instruction.Data0.X, MathF.Min(instruction.Data0.Y, instruction.Data0.Z));
                    break;
                case SdfOp.DomainWarp:
                    factor *= DisplaceWarpLipschitz(instruction);
                    break;
                case SdfOp.GaussianPush:
                    factor *= GaussianPushLipschitz(new(instruction.Data0.W, instruction.Data1.W,
                        BitConverter.UInt32BitsToSingle(instruction.Shape)),
                        new Vector3(instruction.Data1.X, instruction.Data1.Y, instruction.Data1.Z));
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
                        throw new ArgumentException($"CellDisplace at instruction {index} follows {op}, whose sampling coordinates lack a global continuous derivative bound. ResetPoint and restore the shape's rigid frame before cellular relief.");
                    }
                    if (!float.IsFinite(factor)) {
                        throw new ArgumentException($"CellDisplace at instruction {index} has a nonfinite coordinate derivative bound.");
                    }
                    factors[index] = factor;
                    break;
            }
        }
        return factors;
    }
}
