namespace Puck.SignedDistance;

/// <summary>What binds a program's global <see cref="SdfProgram.StepScale"/> below 1: the unscoped shape chain with the
/// largest Lipschitz factor (outside every <see cref="SdfOp.PushField"/>/<see cref="SdfOp.PopField"/> scope, whose
/// factor a scope clamps to 1 at its pop), or an unscoped relief whose charge is larger. The read-back a cost sheet names
/// so a silent global bound has an author: which instance, which shape or relief, how much. Scoped clamps are reported
/// separately by <see cref="SdfProgram.FieldScopeClamps"/>; an absent binder does not mean the program has no
/// clamps.</summary>
/// <param name="InstructionIndex">The binding shape's or relief's instruction index in <see cref="SdfProgram.Instructions"/>.</param>
/// <param name="InstanceIndex">The index in <see cref="SdfProgram.Instances"/> of the instance owning that instruction,
/// or -1 for the world stream.</param>
/// <param name="Op">The binding instruction's op: <see cref="SdfOp.ShapeBlend"/> for a chain, or the relief's.</param>
/// <param name="Shape">The chain's shape; <see langword="null"/> for a relief.</param>
/// <param name="Factor">The chain's own Lipschitz factor (a warp bound, or a product of warp bounds), or one plus the
/// relief's charge (its slope, masks included, times its point factor). The program's step scale is at most
/// <c>1 / Factor</c>; a chamfer composition or a second unscoped relief can push it lower still.</param>
public readonly record struct SdfStepScaleBinder(
    int InstructionIndex,
    int InstanceIndex,
    SdfOp Op,
    SdfShapeType? Shape,
    float Factor
);
