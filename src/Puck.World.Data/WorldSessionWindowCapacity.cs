namespace Puck.World;

/// <summary>The document-level ceiling on simultaneously authored <see cref="WorldScreenProjection.Window"/> session
/// faces — refused by name at validation (see <see cref="WorldDefinitionValidator"/>), never a silent quality
/// degrade. A window never enters <c>Puck.SdfVm.Views.ViewStack</c>'s round-robin refresh share the way an ordinary
/// <see cref="WorldScreenProjection.Camera"/> session does — it renders every produced frame unconditionally, because
/// a stale image would break the parallax the projection exists for — so each one is a full extra offscreen SDF
/// engine submit, paid on every frame, for the life of the bind.</summary>
/// <remarks><see cref="MaxSimultaneousWindows"/> mirrors <c>Puck.SdfVm.Views.ViewStack.RefreshBudget</c>'s value by
/// hand: <c>Puck.World.Data</c> sits below <c>Puck.SdfVm</c> in the project layering (see docs/project-map.md) and
/// may not reference it, the same reason <c>Puck.Overlays.OverlayChannelLeases.MaxSeats</c> hand-mirrors
/// <c>WorldPopulation.LocalSeatCount</c>'s value instead of importing it. <c>RefreshBudget</c> is the existing,
/// already-declared ceiling on how many full extra render passes one produced frame already budgets for among
/// budgeted views; reusing its value for the unbudgeted window count keeps the worst-case number of full extra
/// engine submits any one produced frame can be asked to pay bounded by the same figure the engine already accepts
/// elsewhere, rather than a fresh, unjustified number. A future <c>RefreshBudget</c> change owes this constant the
/// same move, by hand, in the same commit.</remarks>
public static class WorldSessionWindowCapacity {
    /// <summary>The most <see cref="WorldScreenProjection.Window"/> session faces one document may author at once.</summary>
    public const int MaxSimultaneousWindows = 4;
}
