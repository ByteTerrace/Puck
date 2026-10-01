namespace Puck.Shaders;

// The allocation ceiling is captured with a build; changing only the current render grid changes no GPU object.
public sealed partial class ShaderPipelineRenderNode {
    private IShaderPipelineRenderExtent? m_installedRenderExtent;
    private long m_installedRenderRevision;
    private long m_requestedRenderRevision;
    // This frame's render grid, resolved once by UpdateRenderExtents for every pass and recording.
    private uint m_renderWidth;
    private uint m_renderHeight;
    // Whether the published image was rendered at the extent last requested when it rendered; only such an image is
    // captured.
    private bool m_publishedAtRequestedExtent = true;

    /// <summary>Gets or sets whether the node's published image is shown at its own extent, as a render graph's root is
    /// presented as the display, rather than resampled into a rect its reader sizes. The host composes what a frame
    /// projects (a camera's aspect) for the extent it requests (<see cref="RequestedExtent"/>), so a frame the installed
    /// graph renders at another extent, while a resize builds or after its candidate was refused, projects for an extent
    /// it is not shown at. A node shown at its own extent presents its last image instead until the requested extent
    /// installs. A node whose reader places it renders meanwhile, since its reader stretches the image into the rect the
    /// projection was composed for. Either way a capture reads only an image rendered at the extent requested of the node
    /// when it rendered, so a capture armed during a resize waits for the new extent.</summary>
    public bool ShownAtItsExtent { get; set; }
    /// <summary>Gets or sets the frames its render graph's schedules have left the node's instance unread, nothing showing
    /// or reading it, which every package recording of the node's next frame carries
    /// (<see cref="RenderGraphPackageRecording.UnreadFrames"/>). It moves only while the instance is parked.</summary>
    public long UnreadFrames { get; set; }

    // Whether the installed graph renders at an extent other than the one last requested: a resize is building, or its
    // candidate was refused.
    private bool RendersAnotherExtent => ((m_width != m_requestedWidth) || (m_height != m_requestedHeight));

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
    // A frame renders only while the provider's revision is the one the installed graph was built against
    // (CountsChanged holds the last image otherwise), so the current grid always belongs to the installed graph.
    private void UpdateRenderExtents() {
        var ceiling = RenderCeiling(counts: m_installedCounts);
        var render = (m_installedRenderExtent?.FrameAt(height: m_height, width: m_width) ?? (m_width, m_height));

        ValidateRenderExtent(ceiling: ceiling, render: render);
        (m_renderWidth, m_renderHeight) = render;
        foreach (var pass in m_passes) {
            var extent = (pass.Dimensions?.Resolve(frameHeight: m_height, frameWidth: m_width, renderHeight: render.Height, renderWidth: render.Width) ??
                ((pass.Package is null) ? (m_width, m_height) : render));

            pass.Width = extent.Item1;
            pass.Height = extent.Item2;
        }
    }
}
