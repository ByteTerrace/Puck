using Puck.Hosting;

namespace Puck.Shaders;

// A package with separate render and output grids prices every planned pass at the grid it records. Native and
// ordinary graph instances keep their declared uniform-extent policy; the output extent remains the consumer's grid.
public sealed partial class RenderGraphRuntime : IRenderGraphPassCosts {
    RenderGraphPassCost? IRenderGraphPassCosts.CostOf(string instance, int width, int height) {
        var index = m_set.IndexOf(name: instance);

        return ((index >= 0)
            ? CostAt(height: height, index: index, instance: instance, width: width)
            : null);
    }
    RenderGraphPassCost? IRenderGraphPassCosts.CostAt(int index, string instance, int width, int height) =>
        CostAt(height: height, index: index, instance: instance, width: width);

    private RenderGraphPassCost? CostAt(int index, string instance, int width, int height) {
        if ((width <= 0) || (height <= 0) ||
            (m_packageFactory[index] is not { } factory) ||
            (factory.RenderExtentOf(instance: instance) is not { } extents) ||
            (m_graphs[index] is not { } graph)) {
            return null;
        }
        var render = extents.FrameAt(height: ((uint)height), width: ((uint)width));
        var pixels = 0L;
        var passes = graph.Pipeline.Plan.Passes;

        for (var passIndex = 0; (passIndex < passes.Count); passIndex++) {
            var pass = passes[passIndex];
            var extent = (pass.Extent?.Resolve(frameHeight: ((uint)height), frameWidth: ((uint)width),
                renderHeight: render.Height, renderWidth: render.Width) ?? (render.Width, render.Height));

            pixels = checked((pixels + (((long)extent.Item1) * extent.Item2)));
        }
        return new RenderGraphPassCost(Passes: graph.Pipeline.Plan.Passes.Count, Pixels: pixels);
    }
}
