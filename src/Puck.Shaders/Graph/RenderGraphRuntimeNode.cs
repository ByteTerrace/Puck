using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>A host's render root over a <see cref="RenderGraphRuntime"/>: each produced frame describes one display of a
/// fixed extent showing the runtime's root over the whole display, with every read the host shows at a fixed footprint,
/// and returns the root's latest completed image. It owns the runtime. A frame whose schedule and extents repeat an
/// earlier one allocates nothing.</summary>
public sealed class RenderGraphRuntimeNode : IRenderNode, ICaptureRequestTarget {
    private readonly NodeDescriptor m_descriptor;
    private readonly int m_displayHeight;
    private readonly int m_displayWidth;
    private readonly IReadOnlyList<RenderGraphFootprint> m_footprints;
    private readonly IReadOnlyList<RenderGraphRoot> m_roots;

    private long m_frame;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphRuntimeNode"/> class.</summary>
    /// <param name="runtime">The runtime, which the node owns and disposes.</param>
    /// <param name="width">The display's width, in pixels, the extent the root renders at.</param>
    /// <param name="height">The display's height, in pixels.</param>
    /// <param name="footprints">The reads the display shows inside its rendering instances, each on every frame.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> or <paramref name="footprints"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is
    /// zero.</exception>
    public RenderGraphRuntimeNode(RenderGraphRuntime runtime, uint width, uint height, IReadOnlyList<RenderGraphFootprint> footprints) {
        ArgumentNullException.ThrowIfNull(argument: runtime);
        ArgumentNullException.ThrowIfNull(argument: footprints);
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        Runtime = runtime;
        m_descriptor = new NodeDescriptor(
            Name: $"render-graph:{runtime.Root}",
            SurfaceId: SurfaceId.New()
        );
        m_displayHeight = ((int)height);
        m_displayWidth = ((int)width);
        m_footprints = [.. footprints];
        m_roots = [new RenderGraphRoot(
            Height: 1.0,
            Instance: runtime.Root,
            Width: 1.0
        )];
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <inheritdoc/>
    public string? PendingCapturePath => Runtime.PendingCapturePath;
    /// <summary>Gets the runtime the node produces frames from.</summary>
    public RenderGraphRuntime Runtime { get; }

    /// <inheritdoc/>
    public void Dispose() => Runtime.Dispose();
    /// <inheritdoc/>
    public void OnDeviceLost() => Runtime.OnDeviceLost();
    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) => Runtime.ProduceFrame(
        context: in context,
        frame: new RenderGraphFrame(
            DisplayHeight: m_displayHeight,
            DisplayHertz: 0,
            DisplayWidth: m_displayWidth,
            Footprints: m_footprints,
            Index: m_frame++,
            Roots: m_roots
        )
    );
    /// <inheritdoc/>
    public void RequestCapture(FrameCaptureRequest request) => Runtime.RequestCapture(request: request);
}
