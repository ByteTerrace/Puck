using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>Adapts <see cref="CompanionRenderer"/> onto the <see cref="ISdfSceneEmitter"/> contract — the room's live
/// companion roster as one composable emitter, built fresh onto <see cref="SdfEmitContext.SlotBase"/> from the start
/// (unlike <see cref="Puck.Demo.World.WorldSceneEmitter"/>/<see cref="CreatorSceneEmitter"/>, which still carry a
/// fixed-slotBase constructor; this type uses the current frame-relative slot allocation and does not have fixed-slotBase debt to
/// pay down).</summary>
public sealed class CompanionEmitter : ISdfSceneEmitter {
    private readonly CompanionRoster m_roster;
    private CompanionRenderer? m_renderer;
    private Func<CompanionState, int>? m_faceSlotResolver;

    /// <summary>Wraps a companion roster.</summary>
    /// <param name="roster">The live companion roster.</param>
    public CompanionEmitter(CompanionRoster roster) {
        ArgumentNullException.ThrowIfNull(roster);

        m_roster = roster;
    }

    /// <summary>Resolves a screen-faced companion's ledger-granted screen-surface slot this pass (see
    /// <see cref="CompanionRenderer.EmitCompanions"/>'s <c>faceSlotResolver</c>) — set once by the composing frame
    /// source; read fresh at every live <see cref="Emit"/>.</summary>
    public void SetFaceSlotResolver(Func<CompanionState, int> resolver) {
        ArgumentNullException.ThrowIfNull(resolver);

        m_faceSlotResolver = resolver;
    }

    private CompanionRenderer EnsureRenderer(int slotBase) =>
        (m_renderer ??= new CompanionRenderer(roster: m_roster, slotBase: slotBase));

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) =>
        EnsureRenderer(slotBase: context.SlotBase).EmitCompanions(builder: builder, probeWorstCase: context.Probe, faceSlotResolver: (context.Probe ? null : m_faceSlotResolver));

    /// <inheritdoc/>
    public int DynamicSlotCount => CompanionRenderer.DynamicSlotCount;

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) =>
        EnsureRenderer(slotBase: context.SlotBase).PackTransforms(transforms: slots, hiddenPosition: context.ParkPosition);

    /// <summary>The live-packed root transform for companion <paramref name="companionIndex"/>'s shape
    /// <paramref name="shapeIndex"/>, as of the last <see cref="PackDynamicTransforms"/> call (the SAME one-frame lag
    /// every other diegetic-view anchor read already carries — see <c>OverworldFrameSource.PublishCompanionShapeAnchors</c>).
    /// Returns <see langword="false"/> before the renderer's first pack (no composition source has run yet).</summary>
    public bool TryGetShapeTransform(int companionIndex, int shapeIndex, out DynamicTransform transform) {
        if ((m_renderer is not { } renderer) || !renderer.TryGetLastShapeTransform(companionIndex: companionIndex, shapeIndex: shapeIndex, transform: out transform)) {
            transform = default;

            return false;
        }

        return true;
    }
}
