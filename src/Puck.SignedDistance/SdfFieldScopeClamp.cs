namespace Puck.SignedDistance;

/// <summary>A non-unit candidate-distance scale baked into a field scope's pop. This bound applies to every
/// shape in the scope even when the program's global <see cref="SdfProgram.StepScale"/> is one. It is a
/// conservative field bound, not a measured multiplier of GPU time or march iterations.</summary>
/// <param name="PushInstructionIndex">The scope's opening instruction in <see cref="SdfProgram.Instructions"/>.</param>
/// <param name="PopInstructionIndex">The closing instruction carrying the scale in <c>Data1.y</c>.</param>
/// <param name="InstanceIndex">The owning instance, or -1 for the world stream.</param>
/// <param name="ShapeCount">The number of shape instructions sharing this clamp, including detail shapes.</param>
/// <param name="StepScale">The exact positive scale below one stored in the pop instruction.</param>
public readonly record struct SdfFieldScopeClamp(
    int PushInstructionIndex,
    int PopInstructionIndex,
    int InstanceIndex,
    int ShapeCount,
    float StepScale
);
