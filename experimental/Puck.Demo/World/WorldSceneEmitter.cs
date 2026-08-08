using Puck.Assets;
using Puck.SdfVm;
using Puck.Text;

namespace Puck.Demo.World;

/// <summary>Adapts <see cref="WorldSceneRenderer"/> onto the <see cref="ISdfSceneEmitter"/> contract — the sculpted
/// world scene as one composable emitter. <see cref="OwnsMaterialScope"/> is <see langword="true"/>: a placement's
/// authored <c>Pattern</c> reaches its wallpaper-fold recolor rows POSITIONALLY through
/// <see cref="SdfProgramBuilder.WallpaperFold"/>'s <c>materialStride</c> (see <see cref="WorldSceneRenderer.EmitWorld"/>'s
/// <c>AppendFoldOps</c>), so this is the scope mechanism's first real SECOND consumer alongside
/// <see cref="Puck.SdfVm.Debug.SdfDriftMonolith"/> — proof the mechanism generalizes past its one hand-authored
/// exhibit.
/// <para>
/// The inner <see cref="WorldSceneRenderer"/> is built lazily on this emitter's first
/// <see cref="Emit"/> or <see cref="PackDynamicTransforms"/> call, at whatever <see cref="SdfEmitContext.SlotBase"/>
/// the composing host assigns — the composition host's construction-time worst-case probe (see
/// <see cref="SdfCompositionFrameSource"/>) always calls <see cref="Emit"/> at least once before anything else touches
/// this emitter, so the renderer exists (at the correct slot base) by the time a real frame needs it. The base is
/// therefore never threaded through this type's own constructor — a caller composes this emitter without knowing or
/// caring where the composition host will land its dynamic-transform range.
/// </para></summary>
public sealed class WorldSceneEmitter : ISdfSceneEmitter {
    private readonly WorldScene m_scene;
    private readonly ContentAddressedStore m_store;
    private WorldSceneRenderer? m_renderer;
    private FontAtlas? m_pendingFont;

    /// <summary>Wraps a world scene (see the type remarks on slot-base deferral).</summary>
    /// <param name="scene">The authored scene to emit.</param>
    /// <param name="store">The content-addressed store placements resolve creations against.</param>
    public WorldSceneEmitter(WorldScene scene, ContentAddressedStore store) {
        m_scene = scene;
        m_store = store;
    }

    private WorldSceneRenderer EnsureRenderer(int slotBase) {
        if (m_renderer is not { } renderer) {
            renderer = new WorldSceneRenderer(scene: m_scene, store: m_store, slotBase: slotBase);
            renderer.SetGlyphAtlas(font: m_pendingFont);
            m_renderer = renderer;
        }

        return renderer;
    }

    /// <summary>Binds the shared world-glyph atlas (forwards to <see cref="WorldSceneRenderer.SetGlyphAtlas"/> once
    /// the renderer exists; stashed for the eventual first <see cref="EnsureRenderer"/> call otherwise).</summary>
    /// <param name="font">The shared font atlas (or null when none was built).</param>
    public void SetGlyphAtlas(FontAtlas? font) {
        m_pendingFont = font;
        m_renderer?.SetGlyphAtlas(font: font);
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) =>
        EnsureRenderer(slotBase: context.SlotBase).EmitWorld(builder: builder, probeWorstCase: context.Probe);

    /// <inheritdoc/>
    public int DynamicSlotCount => WorldSceneRenderer.DynamicSlotCount;

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) =>
        EnsureRenderer(slotBase: context.SlotBase).PackTransforms(transforms: slots, hiddenPosition: context.ParkPosition, timeSeconds: context.Time);

    /// <inheritdoc/>
    public bool OwnsMaterialScope => true;

    /// <inheritdoc/>
    public int Revision => m_scene.ProgramRevision;
}
