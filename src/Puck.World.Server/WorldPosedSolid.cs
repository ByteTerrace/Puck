using Puck.Maths;
using Puck.World.Authoring;

namespace Puck.World.Server;

/// <summary>An attached solid row at one sampled carrier pose: the creation it shows and the fixed-point origin and
/// rotation its shapes are placed at, in place of the row's inert authored transform.</summary>
/// <param name="Placement">The attached solid row.</param>
/// <param name="Creation">The creation the row shows.</param>
/// <param name="Origin">The resolved world origin, in metres.</param>
/// <param name="Rotation">The resolved rotation.</param>
public readonly record struct WorldPosedSolid(WorldPlacement Placement, WorldPrototype Creation, FixedVector3 Origin, FixedQuaternion Rotation) {
    /// <summary>Gets the stamp transform the row's shapes are emitted through at this pose, for colliders and the
    /// authored field alike.</summary>
    public FixedCreationStampTransform Transform => new(Origin: Origin, Rotation: Rotation, Scale: FixedQ4816.FromDouble(value: Placement.Scale), ReflectionNormal: null);
}
/// <summary>An attached solid row at the pose its colliders were last sampled, with those colliders and their authored
/// sources.</summary>
/// <param name="Pose">The row and the pose.</param>
/// <param name="Colliders">The colliders compiled at that pose, exactly as the solver holds them.</param>
public sealed record WorldAttachedDiagnostic(WorldPosedSolid Pose, IReadOnlyList<WorldDiagnosticCollider> Colliders);
