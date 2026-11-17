using Puck.Maths;
using Puck.World.Authoring;

namespace Puck.World.Server;

/// <summary>A moving solid row at one authority-sampled pose and size: the transform its contact and diagnostic read.</summary>
/// <param name="Placement">The attached solid row.</param>
/// <param name="Creation">The creation the row shows.</param>
/// <param name="Origin">The resolved world origin, in metres.</param>
/// <param name="Rotation">The resolved rotation.</param>
/// <param name="Scale">The sampled uniform scale, including the placement's authored scale.</param>
/// <param name="ReflectionNormal">The occurrence's reflection plane, if any.</param>
public readonly record struct WorldPosedSolid(WorldPlacement Placement, WorldPrototype Creation, FixedVector3 Origin, FixedQuaternion Rotation, FixedQ4816 Scale, FixedVector3? ReflectionNormal = null) {
    /// <summary>Gets the stamp transform the row's shapes are emitted through at this pose, for colliders and the
    /// authored field alike.</summary>
    public FixedCreationStampTransform Transform => new(Origin: Origin, Rotation: Rotation, Scale: Scale, ReflectionNormal: ReflectionNormal);
}
/// <summary>An attached solid row at the pose its colliders were last sampled, with those colliders and their authored
/// sources.</summary>
/// <param name="Pose">The row and the pose.</param>
/// <param name="Colliders">The colliders compiled at that pose, exactly as the solver holds them.</param>
public sealed record WorldAttachedDiagnostic(WorldPosedSolid Pose, IReadOnlyList<WorldDiagnosticCollider> Colliders);
