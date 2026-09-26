using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Prepares what one frame of a <see cref="RenderGraphRuntimeNode"/> shows, before the runtime schedules it: the
/// host sets <see cref="RenderGraphRuntimeNode.Roots"/> and <see cref="RenderGraphRuntimeNode.Footprints"/> and each
/// shown instance's frame values.</summary>
/// <param name="context">The host's frame context.</param>
public delegate void RenderGraphFramePreparer(in FrameContext context);

/// <summary>A host's render root over a <see cref="RenderGraphRuntime"/>: each produced frame describes one display of a
/// fixed extent showing the runtime's root over the whole display, beside the other roots and the reads the host shows
/// that frame, and returns the root's latest completed image. It owns the runtime. A frame whose schedule and extents
/// repeat an earlier one allocates nothing.</summary>
public sealed class RenderGraphRuntimeNode : IRenderNode, ICaptureRequestTarget {
    private readonly NodeDescriptor m_descriptor;
    private readonly int m_displayHeight;
    private readonly int m_displayWidth;
    private readonly List<RenderGraphRoot> m_roots = [];

    private long m_frame;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphRuntimeNode"/> class.</summary>
    /// <param name="runtime">The runtime, which the node owns and disposes.</param>
    /// <param name="width">The display's width, in pixels, the extent the root renders at.</param>
    /// <param name="height">The display's height, in pixels.</param>
    /// <param name="footprints">The reads the display shows inside its rendering instances, until the host sets
    /// <see cref="Footprints"/>.</param>
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
            Name: "render-graph",
            SurfaceId: SurfaceId.New()
        );
        m_displayHeight = ((int)height);
        m_displayWidth = ((int)width);
        Footprints = [.. footprints];
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <summary>Gets or sets the reads the display shows inside its rendering instances this frame.</summary>
    public IReadOnlyList<RenderGraphFootprint> Footprints { get; set; }
    /// <inheritdoc/>
    public string? PendingCapturePath => Runtime.PendingCapturePath;
    /// <summary>Gets or sets the callback that prepares each frame before the runtime schedules it, or
    /// <see langword="null"/> for a display whose roots and footprints never change.</summary>
    public RenderGraphFramePreparer? Prepare { get; set; }
    /// <summary>Gets or sets the instances the display shows this frame beside the runtime's root, which it always shows
    /// over its whole extent.</summary>
    public IReadOnlyList<RenderGraphRoot> Roots { get; set; } = [];
    /// <summary>Gets the runtime the node produces frames from.</summary>
    public RenderGraphRuntime Runtime { get; }

    /// <inheritdoc/>
    public void Dispose() => Runtime.Dispose();
    /// <inheritdoc/>
    public void OnDeviceLost() => Runtime.OnDeviceLost();
    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        Prepare?.Invoke(context: in context);
        m_roots.Clear();
        m_roots.Add(item: new RenderGraphRoot(
            Height: 1.0,
            Instance: Runtime.Root,
            Width: 1.0
        ));

        var roots = Roots;

        for (var index = 0; (index < roots.Count); index++) {
            if (!string.Equals(
                a: roots[index].Instance,
                b: Runtime.Root,
                comparisonType: StringComparison.Ordinal
            )) {
                m_roots.Add(item: roots[index]);
            }
        }

        return Runtime.ProduceFrame(
            context: in context,
            frame: new RenderGraphFrame(
                DisplayHeight: m_displayHeight,
                DisplayHertz: 0,
                DisplayWidth: m_displayWidth,
                Footprints: Footprints,
                Index: m_frame++,
                Roots: m_roots,
                Tick: ((context.StepTicks == 0UL)
                    ? 0L
                    : ((long)(context.ElapsedTicks / context.StepTicks)))
            )
        );
    }
    /// <inheritdoc/>
    public void RequestCapture(FrameCaptureRequest request) => Runtime.RequestCapture(request: request);
}
