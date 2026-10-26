using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One moving entity's rigid transform for a frame: a world position and an orientation. The renderer uploads
/// these into the per-frame dynamic-transform buffer the <c>SdfOp.TransformDynamic</c> opcode indexes by slot, so an
/// entity moves without rebuilding the scene program. The slot is the entity's index in
/// <c>Puck.SdfVm.SdfFrame.DynamicTransforms</c>.
/// <para><paramref name="Lanes"/> is an anonymous four-component carrier, indexed 0 through 3. Every component
/// must be finite. Shape operations and material layers select their component explicitly; the engine assigns
/// no meaning to a component. A chain without a dynamic transform reads four zeros.</para></summary>
public readonly record struct DynamicTransform(Vector3 Position, Quaternion Orientation, Vector4 Lanes = default) {
    /// <summary>Gets the uniform scale of a shared local asset's material coordinates. Field transforms remain rigid.</summary>
    public float MaterialScale { get; init; } = 1f;
}
