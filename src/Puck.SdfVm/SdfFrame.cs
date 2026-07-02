using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;

namespace Puck.SdfVm;

public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region);

/// <summary>One moving entity's rigid transform for a frame: a world position and an orientation. The renderer uploads
/// these into the per-frame dynamic-transform buffer the <c>SdfOp.TransformDynamic</c> opcode indexes by slot, so an
/// entity moves without rebuilding the scene program. The slot is the entity's index in <see cref="SdfFrame.DynamicTransforms"/>.</summary>
public readonly record struct DynamicTransform(Vector3 Position, Quaternion Orientation);
public sealed record SdfFrame(
    SdfProgram Program,
    bool ProgramChanged,
    IReadOnlyList<SdfViewSnapshot> Views,
    float Time,
    float WarpAmount
) {
    /// <summary>Per-frame transforms for the scene's moving entities, indexed by dynamic-transform slot. Empty for a
    /// static scene (the renderer then binds a single identity slot the program never references). Updating this list
    /// is how entities move — the program (binding 1) is uploaded once and left untouched.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms { get; init; } = [];
}
