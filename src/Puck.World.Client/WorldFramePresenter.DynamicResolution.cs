using Puck.Hosting;

namespace Puck.World.Client;

// Dynamic resolution (rendering plan P15-6): one controller chooses, once a presented frame, the render grid every one
// of the presentation's own views renders at, inside the ceiling the render settings allocate them at
// (WorldRenderSettings.RenderCeiling). The views carry the grid as SdfViewSnapshot.ResolvedRenderScale, composed with a
// layout transition's dip, so it moves no allocation, graph or history. Off, every view renders its ceiling exactly as
// before.
public sealed partial class WorldFramePresenter {
    private readonly WorldDynamicResolution m_dynamicResolution = new();

    private float m_dynamicGrid;

    /// <summary>Gets the dynamic-resolution controller, whose grid and signal the console reports.</summary>
    public WorldDynamicResolution DynamicResolution => m_dynamicResolution;
    /// <summary>Gets or sets the load the dynamic-resolution controller reads: the views' GPU frame time, the presenter's
    /// present timing, the views' counted march steps and their budget, or <see langword="null"/> for a host that reports
    /// none of them.</summary>
    public IWorldFrameLoadSource? FrameLoad { get; set; }

    // Advances the controller once a presented frame, before the frame's views are dressed.
    private void AdvanceDynamicResolution(in FrameContext context) {
        // The GPU's frame time is read only against a known display rate, so only then is it measured.
        FrameLoad?.RequireGpuTiming(required: (m_settings.DynamicResolution && (context.DisplayHertz > 0)));
        FrameLoad?.RequireCompletions(required: m_settings.DynamicResolution);
        m_dynamicGrid = (m_settings.DynamicResolution
            ? m_dynamicResolution.Advance(
                ceiling: m_settings.RenderCeiling,
                displayHertz: context.DisplayHertz,
                floor: m_settings.DynamicResolutionFloor,
                forced: m_settings.DynamicResolutionForced,
                load: FrameLoad,
                outputPixels: (((long)context.TargetWidth) * context.TargetHeight)
            )
            : 0f);
    }
    // The grid a view renders at before a layout transition's dip: the controller's while dynamic resolution is on,
    // bounded by the ceiling, and the ceiling otherwise.
    private float RenderGrid() {
        var ceiling = m_settings.RenderCeiling;

        return ((m_settings.DynamicResolution && (m_dynamicGrid > 0f))
            ? Math.Min(val1: m_dynamicGrid, val2: ceiling)
            : ceiling);
    }
}
