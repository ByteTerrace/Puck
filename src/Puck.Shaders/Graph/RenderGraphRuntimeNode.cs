using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Prepares what one frame of a <see cref="RenderGraphRuntimeNode"/> shows, before the runtime schedules it: the
/// host sets <see cref="RenderGraphRuntimeNode.Roots"/> and <see cref="RenderGraphRuntimeNode.Footprints"/> and each
/// shown instance's frame values.</summary>
/// <param name="context">The host's frame context.</param>
public delegate void RenderGraphFramePreparer(in FrameContext context);

/// <summary>A host's render root over a <see cref="RenderGraphRuntime"/>: each produced frame describes one display, whose
/// extent the host may change between frames (<see cref="Resize"/>), showing the runtime's root over the whole
/// display, beside the other roots and the reads the host shows
/// that frame, and returns the root's latest completed image. It owns the runtime. A frame whose schedule and extents
/// repeat an earlier one allocates nothing.</summary>
public sealed class RenderGraphRuntimeNode : IRenderRoot, ICaptureRequestTarget {
    private int m_displayHeight;
    private int m_displayWidth;

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
        m_displayHeight = ((int)height);
        m_displayWidth = ((int)width);
        Footprints = [.. footprints];
    }

    /// <summary>Gets or sets the reads the display shows inside its rendering instances this frame.</summary>
    public IReadOnlyList<RenderGraphFootprint> Footprints { get; set; }
    /// <summary>Gets how many frames the node has produced, the next frame's <see cref="RenderGraphFrame.Index"/>.</summary>
    public long FramesProduced => m_frame;
    /// <summary>Gets the services the host ties to the root's teardown, disposed in order after the runtime. A host
    /// disposes its root while the device is still alive, so a service holding GPU objects that its container would
    /// dispose only after the device context is released here instead; its later disposal by the container is a
    /// no-op.</summary>
    public IReadOnlyList<IDisposable> Holdings { get; init; } = [];
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
    public void Dispose() {
        Runtime.Dispose();

        foreach (var holding in Holdings) {
            holding.Dispose();
        }
    }
    /// <inheritdoc/>
    public void OnDeviceLost() => Runtime.OnDeviceLost();
    /// <inheritdoc/>
    /// <remarks>The frame's completion is the runtime's (<see cref="RenderGraphRuntime.Render"/>): rendered only when
    /// the root's image shows this frame.</remarks>
    public RootFrame ProduceFrame(in FrameContext context) {
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

        var surface = Runtime.ProduceFrame(
            context: in context,
            frame: new RenderGraphFrame(
                DisplayHeight: m_displayHeight,
                DisplayHertz: context.DisplayHertz,
                DisplayWidth: m_displayWidth,
                Footprints: Footprints,
                Index: m_frame++,
                Roots: m_roots,
                Tick: ((context.StepTicks == 0UL)
                    ? 0L
                    : ((long)(context.ElapsedTicks / context.StepTicks)))
            )
        );

        return new RootFrame(
            Render: Runtime.Render,
            Surface: surface
        );
    }
    /// <inheritdoc/>
    public void RequestCapture(FrameCaptureRequest request) => Runtime.RequestCapture(request: request);
    /// <summary>Changes the display extent from the next produced frame on: the runtime schedules every instance's
    /// footprint against it, so the root and every view it places render at the new extent. The root, shown at its own
    /// extent, presents its last image until its graph installs the new one
    /// (<see cref="ShaderPipelineRenderNode.ShownAtItsExtent"/>), and a capture waits for that frame.</summary>
    /// <param name="width">The display's width, in pixels.</param>
    /// <param name="height">The display's height, in pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is zero or
    /// past <see cref="int.MaxValue"/>.</exception>
    public void Resize(uint width, uint height) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: ((uint)int.MaxValue),
            value: width
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: ((uint)int.MaxValue),
            value: height
        );

        m_displayWidth = ((int)width);
        m_displayHeight = ((int)height);
    }
}
