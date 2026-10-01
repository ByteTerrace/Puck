using Puck.Hosting;

namespace Puck.Shaders;

// A package with separate render and output grids prices every planned pass at the grid it records. Native and
// ordinary graph instances keep their declared uniform-extent policy; the output extent remains the consumer's grid.
public sealed partial class RenderGraphRuntime : IRenderGraphPassCosts {
    RenderGraphPassCost? IRenderGraphPassCosts.CostOf(string instance, int width, int height) {
        var index = m_set.IndexOf(name: instance);

        if ((index < 0) || (width <= 0) || (height <= 0) ||
            !RunsPackage(instance: m_set.Instances[index], packages: m_packages) ||
            !m_packages.TryGetFactory(package: m_set.Instances[index].ExternalPackage!, factory: out var factory) ||
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
