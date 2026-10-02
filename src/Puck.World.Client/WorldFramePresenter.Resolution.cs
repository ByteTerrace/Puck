using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    private FrameContext m_resolutionContext;

    private readonly List<(string Name, WorldDynamicResolutionController Controller)> m_resolutionViews = [];

    // A controller belongs to the same view slot as its history. Names are captured once, including numbered views,
    // so a steady frame allocates neither controller state nor a producer name. Frozen captures reuse the dressed frame.
    private float ResolveDynamicScale(int view, NormalizedRect region, uint width, uint height, float ceiling) {
        if (!m_settings.DynamicResolution) {
            if (view < m_resolutionViews.Count) { m_resolutionViews[view].Controller.Reset(); }
            return 0;
        }
        while (m_resolutionViews.Count <= view) {
            m_resolutionViews.Add(item: (WorldRootGraph.ProducerOf(view: m_resolutionViews.Count), new WorldDynamicResolutionController()));
        }
        var entry = m_resolutionViews[view];
        var work = m_graphs?.WorkOf(instance: entry.Name);
        // The shared graph quantizer supplies the first output grid before its node is attached. The controller
        // subsequently reads that node's installed output extent and quantizes only the active render scale.
        return entry.Controller.UpdateFrame(context: in m_resolutionContext, work: work,
            width: ((uint)RenderGraphExtent.Pixels(fraction: RenderGraphExtent.Quantize(fraction: region.Width), display: checked((int)width))),
            height: ((uint)RenderGraphExtent.Pixels(fraction: RenderGraphExtent.Quantize(fraction: region.Height), display: checked((int)height))),
            floor: WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Quarter), ceiling: ceiling);
    }
}
