namespace Puck.Shaders;

// The allocation ceiling is captured with a build; changing only the current render grid changes no GPU object.
public sealed partial class ShaderPipelineRenderNode {
    private IShaderPipelineRenderExtent? m_installedRenderExtent;
    private long m_installedRenderRevision;
    private long m_requestedRenderRevision;
    // This frame's render grid, resolved once by UpdateRenderExtents for every pass and recording.
    private uint m_renderWidth;
    private uint m_renderHeight;
    private double m_renderGrid = 1d;
    // The grid each recent rendered submission recorded its passes at, at the submission's index modulo the length:
    // twice the frames in flight, so a submission is still here when its completion or timing is read back.
    private (long Submission, double Grid)[] m_renderGrids = [];
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

    // Whether the installed graph renders at an extent other than the one last requested: a resize is building, or its
    // candidate was refused.
    private bool RendersAnotherExtent => ((m_width != m_requestedWidth) || (m_height != m_requestedHeight));

    /// <summary>Reads the render grid one of this node's recent submissions recorded its passes at, as a fraction of the
    /// output on each axis: the grid its package's render extent resolved (<see cref="IShaderPipelineRenderExtent.Grid"/>),
    /// or one for a graph without one. A submission is recorded once it renders the installed graph and kept while it
    /// is among the latest twice the frames in flight, which covers every submission whose completion
    /// (<see cref="TryReadCompleted"/>) or timing (<see cref="LatestTimingSubmission"/>) is read back.</summary>
    /// <param name="submission">The submission's identity, as the node's work ledger numbers it.</param>
    /// <param name="grid">The grid the submission rendered at; zero when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the submission rendered the installed graph and is still recorded.</returns>
    public bool TryGetRenderGrid(long submission, out double grid) {
        grid = 0d;

        if ((submission <= 0L) || (m_renderGrids.Length == 0)) {
            return false;
        }

        var entry = m_renderGrids[((int)(submission % m_renderGrids.Length))];

        if (entry.Submission != submission) {
            return false;
        }

        grid = entry.Grid;

        return true;
    }

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
        m_renderGrid = (m_installedRenderExtent?.Grid ?? 1d);
        foreach (var pass in m_passes) {
            var extent = (pass.Dimensions?.Resolve(frameHeight: m_height, frameWidth: m_width, renderHeight: render.Height, renderWidth: render.Width) ??
                ((pass.Package is null) ? (m_width, m_height) : render));

            pass.Width = extent.Item1;
            pass.Height = extent.Item2;
        }
    }
    // Records the grid of the submission just sealed (m_submissions), which rendered the installed graph.
    private void NoteRenderGrid() {
        if (m_renderGrids.Length == 0) {
            m_renderGrids = new (long, double)[checked((((int)m_inFlight) * 2))];
        }

        m_renderGrids[((int)(m_submissions % m_renderGrids.Length))] = (m_submissions, m_renderGrid);
    }
}
