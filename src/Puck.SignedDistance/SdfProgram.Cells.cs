using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // The point factor of each relief (Displace, NoiseDisplace, CellDisplace): how much its chain's point operations
    // stretch a derivative between the sample point and the point the relief samples. A relief adds to the accumulator in
    // field units and inherits no shape's distanceScale, so AnalyzeLipschitz charges its own slope, in the frame it samples,
    // times this factor, at its own instruction. One over the smallest axis of each Scale, times the factor of each warp
    // with a global bound; a relief after an operation with no global continuous derivative bound (a fold that jumps at a
    // seam, a reach-dependent warp) is refused by name, since no step bound covers it. Indexed by instruction; empty for a
    // program with no relief.
    private static float[] AnalyzeReliefPointFactors(SdfInstruction[] instructions) {
        if (!Array.Exists(
            array: instructions,
            match: static i => (i.Op is SdfOp.CellDisplace or SdfOp.NoiseDisplace or SdfOp.Displace)
        )) { return []; }
        var factors = new float[instructions.Length];
        var factor = 1f;
        (SdfOp Op, int Index)? unsupported = null;

        for (var index = 0; (index < instructions.Length); index++) {
            var instruction = instructions[index];

            switch (instruction.Op) {
                case SdfOp.ResetPoint:
                    factor = 1f;
                    unsupported = null;
                    break;
                case SdfOp.Scale:
                    factor /= SdfDerivedLanes.ScaleCorrection(scale: new Vector3(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z));
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
                case SdfOp.ReliefWeight:
                case SdfOp.LaneErode:
                case SdfOp.PushField:
                case SdfOp.PopField:
                    break;
                case SdfOp.RotatePlane:
                case SdfOp.Repeat:
                case SdfOp.RepeatLimited:
                case SdfOp.WallpaperFold:
                case SdfOp.LogSphere:
                case SdfOp.CellJitter:
                case SdfOp.RepeatPolar:
                case SdfOp.AxialProfile:
                case SdfOp.Shear:
                    unsupported ??= (instruction.Op, index);
                    break;
                case SdfOp.Displace:
                case SdfOp.NoiseDisplace:
                case SdfOp.CellDisplace:
                    if (unsupported is { } op) {
                        throw new ArgumentException(message: $"{instruction.Op} at instruction {index} samples the point {op.Op} at instruction {op.Index} moved, which has no global continuous derivative bound, so no step bound covers the relief. ResetPoint and restore the shape's rigid frame before the relief.");
                    }
                    if (!float.IsFinite(f: factor)) {
                        throw new ArgumentException(message: $"{instruction.Op} at instruction {index} has a nonfinite point derivative bound.");
                    }
                    factors[index] = factor;
                    break;
                default:
                    throw new ArgumentException(message: $"Instruction {index} is {instruction.Op}, which the relief point-factor analysis does not classify.");
            }
        }
        return factors;
    }
}
