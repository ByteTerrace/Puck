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
            match: static i => (SdfFieldOps.ReliefPointFactorOf(value: i.Op) == SdfReliefPointFactorAction.SampleRelief)
        )) { return []; }
        var factors = new float[instructions.Length];
        var factor = 1f;
        (SdfOp Op, int Index)? unsupported = null;

        for (var index = 0; (index < instructions.Length); index++) {
            var instruction = instructions[index];

            switch (SdfFieldOps.ReliefPointFactorOf(value: instruction.Op)) {
                case SdfReliefPointFactorAction.ResetPoint:
                    factor = 1f;
                    unsupported = null;
                    break;
                case SdfReliefPointFactorAction.Scale:
                    factor /= SdfDerivedLanes.ScaleCorrection(scale: new Vector3(x: instruction.Data0.X, y: instruction.Data0.Y, z: instruction.Data0.Z));
                    break;
                case SdfReliefPointFactorAction.DomainWarp:
                    factor *= DisplaceWarpLipschitz(instruction: instruction);
                    break;
                case SdfReliefPointFactorAction.GaussianPush:
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
                case SdfReliefPointFactorAction.Identity:
                    break;
                case SdfReliefPointFactorAction.NoGlobalBound:
                    unsupported ??= (instruction.Op, index);
                    break;
                case SdfReliefPointFactorAction.SampleRelief:
                    if (unsupported is { } op) {
                        throw new ArgumentException(message: $"{instruction.Op} at instruction {index} samples the point {op.Op} at instruction {op.Index} moved, which has no global continuous derivative bound, so no step bound covers the relief. ResetPoint and restore the shape's rigid frame before the relief.");
                    }
                    if (!float.IsFinite(f: factor)) {
                        throw new ArgumentException(message: $"{instruction.Op} at instruction {index} has a nonfinite point derivative bound.");
                    }
                    factors[index] = factor;
                    break;
                case SdfReliefPointFactorAction.Unsupported:
                    throw new ArgumentException(message: $"Instruction {index} is {instruction.Op}, which the relief point-factor analysis does not classify.");
                default:
                    throw new InvalidOperationException(message: "Invalid ReliefPointFactor action.");
            }
        }
        return factors;
    }
}
