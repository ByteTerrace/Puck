using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Represents one decoded SDF VM instruction before it is packed for the GPU.</summary>
/// <param name="Op">The instruction operation.</param>
/// <param name="Shape">The shape identifier for shape instructions, or operation-specific data otherwise.</param>
/// <param name="Blend">The blend identifier for shape instructions, or operation-specific data otherwise.</param>
/// <param name="Material">The material identifier or operation-specific material lane.</param>
/// <param name="Data0">The first operation-specific data vector.</param>
/// <param name="Data1">The second operation-specific data vector.</param>
/// <param name="Detail">Whether a <see cref="SdfOp.ShapeBlend"/> instruction is SHADING-ONLY: skipped by every
/// march/step-bound consumer (the beam cone march, the fine march, shadow/AO, and the rigid-leaf fast path) and included
/// only in the hit-only shade re-evaluation the world renderer runs at an already-found hit (KEEP IN SYNC with
/// <c>SDF_SHAPE_DETAIL_FLAG</c>/<c>sdfDetailShadingActive</c> in Assets/Shaders/Sdf/sdf-vm.hlsli). Packs into the
/// otherwise-unused high bit of the instruction's Shape lane (see <see cref="SdfProgram"/>'s packing), so it is
/// meaningful only when <see cref="Op"/> is <see cref="SdfOp.ShapeBlend"/>; false elsewhere.</param>
/// <param name="Secondary">Whether a <see cref="SdfOp.ShapeBlend"/> instruction participates in a SECONDARY-RAY
/// march: true (the default) marches for the camera/beam/fine march, the hit-only shade re-evaluations, AND the
/// soft-shadow/ambient-occlusion field walks, exactly like an ordinary shape. False drops it ONLY from the
/// soft-shadow and ambient-occlusion walks — it still marches for the camera, still carves the silhouette and the
/// collider — the opposite exclusion set from <see cref="Detail"/> (KEEP IN SYNC with
/// <c>SDF_SHAPE_NO_SECONDARY_FLAG</c>/<c>sdfSecondaryMarchActive</c> in Assets/Shaders/Sdf/sdf-vm.hlsli). Packs into
/// the instruction's Shape lane's next-highest bit, so it is meaningful only when <see cref="Op"/> is
/// <see cref="SdfOp.ShapeBlend"/>; true elsewhere.</param>
public readonly record struct SdfInstruction(
    SdfOp Op,
    uint Shape,
    uint Blend,
    uint Material,
    Vector4 Data0,
    Vector4 Data1,
    bool Detail = false,
    bool Secondary = true
);
