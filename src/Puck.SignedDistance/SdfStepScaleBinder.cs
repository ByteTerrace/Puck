namespace Puck.SignedDistance;

/// <summary>The one unscoped shape chain that binds a program's global <see cref="SdfProgram.StepScale"/> below 1:
/// the depth-0 chain (outside every <see cref="SdfOp.PushField"/>/<see cref="SdfOp.PopField"/> scope, whose factor a
/// scope clamps to 1 at its pop) with the largest Lipschitz factor. The read-back a cost sheet names so a silent
/// frame-wide march tax has an author: which instance, which shape, how much.</summary>
/// <param name="InstructionIndex">The chain's shape instruction index in <see cref="SdfProgram.Instructions"/>.</param>
/// <param name="InstanceIndex">The index in <see cref="SdfProgram.Instances"/> of the instance owning that instruction,
/// or -1 for the world stream.</param>
/// <param name="Shape">The chain's shape.</param>
/// <param name="Factor">The chain's own Lipschitz factor (an eccentricity, a warp bound, or their product). The
/// program's step scale is at most <c>1 / Factor</c>; a chamfer composition can push it lower still.</param>
public readonly record struct SdfStepScaleBinder(
    int InstructionIndex,
    int InstanceIndex,
    SdfShapeType Shape,
    float Factor
);
