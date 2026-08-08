using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>The emission-suppression policy <see cref="CreatorSceneEmitter"/> applies to every LIVE (non-probe) emit —
/// dropping authoring scaffolding a studio review shouldn't show, without mutating the underlying
/// <see cref="CreatorScene"/> (a suppressed placement's sticky ghost/selection state stays intact for interactive
/// use — see <see cref="CreatorSceneRenderer.EmitPool"/>'s remarks). The worst-case PROBE always emits everything
/// regardless of this policy (a suppressed program is a strict subset of the probed envelope).</summary>
/// <param name="SuppressEasel">Drop the preview easel (the post + bake-preview screen slab).</param>
/// <param name="SuppressAdornments">Drop the placement ghost, the rig's goal markers, and the selection highlight.</param>
public readonly record struct CreatorEmitOptions(bool SuppressEasel = false, bool SuppressAdornments = false);

/// <summary>Adapts <see cref="CreatorSceneRenderer"/> onto the <see cref="ISdfSceneEmitter"/> contract — the creator
/// authoring pool as one composable emitter. Unlike <see cref="Puck.Demo.World.WorldSceneEmitter"/> this does not use
/// a positional material stride, so <see cref="ISdfSceneEmitter.OwnsMaterialScope"/> stays the default <see langword="false"/>.
/// <para>
/// Like <see cref="Puck.Demo.World.WorldSceneEmitter"/>, the inner <see cref="CreatorSceneRenderer"/>
/// is built LAZILY on this emitter's first <see cref="Emit"/>/<see cref="PackDynamicTransforms"/> call, at whatever
/// <see cref="SdfEmitContext.SlotBase"/> the composing host assigns (see that type's remarks for why the
/// construction-time worst-case probe guarantees this happens before a real frame needs the renderer).
/// </para>
/// <para>
/// SUPPRESSION POLICY (see <see cref="SetOptions"/>): mutable, not constructor-fixed — a <c>--scenario</c> studio
/// review installs its backdrop AFTER this emitter (and the composition host that probes it) already exist, so a
/// fixed-at-construction policy could never observe a LATER studio activation. <see cref="Emit"/> reads the current
/// policy fresh every live call.
/// </para></summary>
public sealed class CreatorSceneEmitter : ISdfSceneEmitter {
    private readonly CreatorScene m_scene;
    private CreatorSceneRenderer? m_renderer;
    private CreatorEmitOptions m_options;

    /// <summary>Wraps a creator scene under a suppression policy (see the type remarks on slot-base deferral).</summary>
    /// <param name="scene">The authored pool to emit.</param>
    /// <param name="options">The live-emission suppression policy (see <see cref="CreatorEmitOptions"/>); the default
    /// suppresses nothing (the ordinary in-session authoring view) — see <see cref="SetOptions"/> to change it later.</param>
    public CreatorSceneEmitter(CreatorScene scene, CreatorEmitOptions options = default) {
        m_scene = scene;
        m_options = options;
    }

    /// <summary>Replaces the live-emission suppression policy (see <see cref="CreatorEmitOptions"/> and the type
    /// remarks) — read fresh by the next live <see cref="Emit"/>; the worst-case probe branch is unaffected (it
    /// always emits everything regardless of policy).</summary>
    /// <param name="options">The new policy.</param>
    public void SetOptions(CreatorEmitOptions options) => m_options = options;

    private CreatorSceneRenderer EnsureRenderer(int slotBase) =>
        (m_renderer ??= new CreatorSceneRenderer(scene: m_scene, slotBase: slotBase));

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) =>
        EnsureRenderer(slotBase: context.SlotBase).EmitPool(builder: builder, probeWorstCase: context.Probe, suppressEasel: m_options.SuppressEasel, suppressAdornments: m_options.SuppressAdornments);

    /// <inheritdoc/>
    public int DynamicSlotCount => CreatorSceneRenderer.DynamicSlotCount;

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) =>
        EnsureRenderer(slotBase: context.SlotBase).PackTransforms(transforms: slots, hiddenPosition: context.ParkPosition);

    /// <inheritdoc/>
    public int Revision => m_scene.ProgramRevision;
}
