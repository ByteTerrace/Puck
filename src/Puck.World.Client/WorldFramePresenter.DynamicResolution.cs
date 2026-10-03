using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    private readonly Dictionary<string, IWorldFrameLoadSource?> m_viewLoads = new(comparer: StringComparer.Ordinal);

    private FrameContext m_resolutionContext;

    /// <summary>The primary view's load.</summary>
    public IWorldFrameLoadSource? FrameLoad { get; set; }
    /// <summary>Creates a named view's load once, reading only that view's submissions.</summary>
    public Func<string, IWorldFrameLoadSource?>? FrameLoadForView { get; set; }

    private void AdvanceDynamicResolution(in FrameContext context) {
        m_resolutionContext = context;
        m_settings.ReadQuality(definition: m_client.Definition);
        FrameLoad?.RequireGpuTiming(required: (m_settings.AnyResolutionEnabled && (context.DisplayHertz > 0)));
        FrameLoad?.RequireCompletions(required: m_settings.AnyResolutionEnabled);
    }

    /// <summary>Dresses one view's allocation ceiling and grid using its saved quality and live pin.
    /// Repeated frames and pin sweeps reuse the view's controller and load.</summary>
    public SdfViewSnapshot DressResolution(SdfViewSnapshot view, string name, uint width, uint height) {
        var state = m_settings.Resolution(view: name);
        var ceiling = m_settings.Ceiling(view: name);
        var scale = ceiling;

        if (m_settings.Enabled(view: name)) {
            if (!m_viewLoads.TryGetValue(key: name, value: out var load)) {
                load = ((name == WorldViewGraphs.WorldInstance) ? FrameLoad : FrameLoadForView?.Invoke(name));
                m_viewLoads.Add(key: name, value: load);
            }
            scale = state.Controller.Advance(load, m_resolutionContext.DisplayHertz, ceiling,
                m_settings.Floor(view: name), (((long)width) * height),
                ((state.Pin > 0f) ? Math.Clamp(state.Pin, m_settings.Floor(view: name), ceiling) : 0f));
        } else {
            state.Controller.Reset();
        }
        return view with { RenderScale = ceiling, ResolvedRenderScale = scale, UpscaleSharpness = m_settings.UpscaleSharpness };
    }
}
