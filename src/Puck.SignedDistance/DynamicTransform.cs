using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One moving entity's rigid transform for a frame: a world position and an orientation. The renderer uploads
/// these into the per-frame dynamic-transform buffer the <c>SdfOp.TransformDynamic</c> opcode indexes by slot, so an
/// entity moves without rebuilding the scene program. The slot is the entity's index in
/// <c>Puck.SdfVm.SdfFrame.DynamicTransforms</c>.
/// <para><paramref name="CastsSoftShadow"/> (default <see langword="true"/> = casts) rides the packed position row's spare
/// <c>.w</c> lane (see <c>SdfWorldEngine.PackDynamicTransforms</c>): <see langword="false"/> means this dynamic instance is
/// skipped by the soft-shadow march only — the camera/AO marches are unaffected, so a suppressed avatar still renders and
/// self-occludes, it just stops casting/receiving through the sun-shadow enumeration. Per-frame data (avatars move every
/// frame); flipping it never rebuilds the program. Default casts is byte-identical to every prior frame's zero-pad upload.</para></summary>
public readonly record struct DynamicTransform(Vector3 Position, Quaternion Orientation, bool CastsSoftShadow = true);
