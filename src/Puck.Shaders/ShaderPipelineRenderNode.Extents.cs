namespace Puck.Shaders;

// The allocation ceiling is captured with a build; changing only the current render grid changes no GPU object.
public sealed partial class ShaderPipelineRenderNode {
    private IShaderPipelineRenderExtent? m_installedRenderExtent;
    private long m_installedRenderRevision;
    private long m_requestedRenderRevision;

    private IShaderPipelineRenderExtent? RenderExtentOf(ShaderPipelinePlan plan) {
        foreach (var pass in plan.Passes) {
            if ((pass.Package is { } package) && m_packages.TryGetFactory(package: package.Package, factory: out var factory) &&
                (factory.RenderExtentOf(instance: m_name) is { } extent)) {
                return extent;
            }
        }
        return null;
    }
    private static void ValidateRenderExtent((uint Width, uint Height) render, (uint Width, uint Height) ceiling) {
        if ((render.Width == 0) || (render.Height == 0) || (render.Width > ceiling.Width) || (render.Height > ceiling.Height)) {
            throw new InvalidDataException(message: $"Render extent {render.Width}x{render.Height} is outside its {ceiling.Width}x{ceiling.Height} ceiling.");
        }
    }
    private static (uint Width, uint Height) RenderCeiling(ShaderPipelineStorageCounts counts) =>
        (((counts.RenderWidth == 0) ? counts.Width : counts.RenderWidth), ((counts.RenderHeight == 0) ? counts.Height : counts.RenderHeight));
    private static (uint Width, uint Height) PassExtent(ShaderPipelinePlannedPass pass, ShaderPipelineStorageCounts counts) =>
        (pass.Extent?.Resolve(frameWidth: counts.Width, frameHeight: counts.Height, renderWidth: counts.RenderWidth, renderHeight: counts.RenderHeight) ??
        ((pass.Package is null) ? (counts.Width, counts.Height) : RenderCeiling(counts: counts)));
    private void UpdateRenderExtents() {
        var ceiling = RenderCeiling(counts: m_installedCounts);
        var render = (m_installedRenderExtent?.FrameAt(height: m_height, width: m_width) ?? (m_width, m_height));

        ValidateRenderExtent(ceiling: ceiling, render: render);
        foreach (var pass in m_passes) {
            var extent = (pass.Dimensions?.Resolve(frameHeight: m_height, frameWidth: m_width, renderHeight: render.Height, renderWidth: render.Width) ??
                ((pass.Package is null) ? (m_width, m_height) : render));

            pass.Width = extent.Item1;
            pass.Height = extent.Item2;
        }
    }
}
