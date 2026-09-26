using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.SdfVm;

// The node as the external producer behind sdf.world: the graph runtime produces it through the engine's own ring, and
// each consumer acquires a view's latest completed output under a lease. Every acquisition holds the image the engine
// rendered, so an output the engine replaced at a new view extent, and an engine replaced at a larger extent, are
// disposed only once the submissions sampling them have released them; an engine's Dispose still drains the device, as
// the backstop.
public sealed partial class SdfEngineNode : IRenderGraphExternalProducer {
    // Replaced engines whose outputs a consumer still holds, each disposed when its last acquisition is released.
    private readonly List<RetiringEngine> m_retiringEngines = [];
    // Per view slot: the extent the graph last scheduled the view at, or zero before one.
    private readonly (uint Width, uint Height)[] m_scheduledViewExtents = new (uint Width, uint Height)[SdfWorldEngine.MaxViewports];
    // Per view slot past view 0: the producer that stands for the view in a render graph, created on first request.
    private readonly SdfViewProducer?[] m_viewProducers = new SdfViewProducer?[SdfWorldEngine.MaxViewports];

    // Created on the first acquisition, so a lease allocates nothing per frame.
    private Action<int>? m_releaseOutput;

    /// <summary>Gets the acquisitions of the node's view outputs not yet released, over its current engine and every
    /// replaced engine still held.</summary>
    public int OutputLeases {
        get {
            var leases = (m_engine?.ViewOutputHolds ?? 0);

            foreach (var retiring in m_retiringEngines) {
                leases += retiring.Engine.ViewOutputHolds;
            }

            return leases;
        }
    }
    /// <summary>Gets the replaced engines not yet disposed because a consumer still holds one of their outputs.</summary>
    public int RetiringEngines => m_retiringEngines.Count;

    SurfaceFormat IRenderGraphExternalProducer.Format => SurfaceFormat.R8G8B8A8Unorm;

    /// <summary>Returns the producer that stands for one view past view 0 in a render graph: its <c>Produce</c> records
    /// the extent the graph scheduled the view at, which the engine renders the view at from the next frame on, and it
    /// hands out the view's latest output. The node renders every view in its own <see cref="Produce"/>, so a graph
    /// schedules the node before these. The node owns each producer; disposing one releases nothing.</summary>
    /// <param name="view">The view slot, from 1 to <see cref="SdfWorldEngine.MaxViewports"/> less one.</param>
    /// <returns>The view's producer, the same object on every call.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="view"/> is 0 or past the last view slot.</exception>
    public IRenderGraphExternalProducer ViewProducer(int view) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: view
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: SdfWorldEngine.MaxViewports,
            value: view
        );

        return (m_viewProducers[view] ??= new SdfViewProducer(
            node: this,
            view: view
        ));
    }

    // Disposes every replaced engine, whatever it still has leased: after a drain, or once the device is lost.
    private void DisposeRetiringEngines() {
        foreach (var retiring in m_retiringEngines) {
            retiring.Dispose();
        }

        m_retiringEngines.Clear();
    }
    private void ReleaseOutput(int identity) {
        if (m_engine?.ReleaseViewOutput(identity: identity) ?? false) {
            return;
        }

        for (var index = 0; (index < m_retiringEngines.Count); index++) {
            var retiring = m_retiringEngines[index];

            if (!retiring.Engine.ReleaseViewOutput(identity: identity)) {
                continue;
            }
            if (retiring.Engine.ViewOutputHolds == 0) {
                m_retiringEngines.RemoveAt(index: index);
                retiring.Dispose();
            }

            return;
        }
    }
    // Replaces the engine at the next produced frame. The replaced engine is disposed now when nothing holds its outputs,
    // and otherwise when the last acquisition is released; the screen-source leases its submissions sampled go with it.
    private void RetireEngine() {
        if (m_engine is not { } engine) {
            return;
        }

        var retiring = new RetiringEngine(engine: engine);

        foreach (var retained in m_retainedScreenSourceFrames) {
            retained.MoveTo(destination: retiring.ScreenSources);
        }

        m_engine = null;
        m_engineProduced = false;
        m_glyphAtlasInitialized = false;
        m_uploadedGlyphAtlas = null;

        if (engine.ViewOutputHolds == 0) {
            retiring.Dispose();
        } else {
            m_retiringEngines.Add(item: retiring);
        }
    }
    // Hands the engine every view extent a graph has scheduled, before the frame that renders at them.
    private void ApplyScheduledViewExtents(SdfWorldEngine engine, int viewCount) {
        for (var view = 0; (view < viewCount); view++) {
            var (width, height) = m_scheduledViewExtents[view];

            if ((width != 0) && (height != 0)) {
                engine.RequestViewExtent(
                    height: height,
                    view: view,
                    width: width
                );
            }
        }
    }

    /// <summary>Gets whether the current engine has rendered a view into its output, which a consumer can then acquire.</summary>
    /// <param name="view">The 0-based view slot.</param>
    /// <returns><see langword="true"/> once a submitted frame of the current engine rendered the view.</returns>
    public bool HasViewOutput(int view) => (m_engine?.HasViewOutput(view: view) ?? false);

    private bool TryAcquireViewOutput(int view, out RenderGraphExternalOutput output) {
        if (
            !m_engineProduced ||
            (m_engine is not { } engine) ||
            !engine.TryAcquireViewOutput(
                output: out var acquired,
                view: view
            )
        ) {
            output = default;

            return false;
        }

        output = new RenderGraphExternalOutput(
            Image: Surface.SameDeviceImage(
                format: SurfaceFormat.R8G8B8A8Unorm,
                height: acquired.Height,
                imageHandle: acquired.ImageHandle,
                imageViewHandle: acquired.ImageViewHandle,
                width: acquired.Width
            ),
            Layout: engine.OutputLayout,
            Lease: new GpuImageLease(
                ImageViewHandle: acquired.ImageViewHandle,
                Release: (m_releaseOutput ??= ReleaseOutput),
                ReleaseToken: acquired.Identity
            )
        );

        return true;
    }

    /// <summary>Renders one frame through the engine's own ring, every view of the frame included, with view 0 at the
    /// scheduled extent. The engine's own extent, the largest any view renders at, only grows: an extent past it replaces
    /// the engine first, built at once from the installed pipeline set, and the replaced engine is disposed once every
    /// acquisition of its outputs is released.</summary>
    /// <param name="context">The host's frame context, which resolves the device.</param>
    /// <param name="width">View 0's extent width, in pixels.</param>
    /// <param name="height">View 0's extent height, in pixels.</param>
    /// <param name="reads">The latest completed image of each source instance the world's instance reads: each screen
    /// that reads one binds its image, and the node takes its lease once however many screens show it, holding it until
    /// the frame-ring slot that samples it has passed its fence.</param>
    /// <returns><see langword="true"/> when the frame was submitted; <see langword="false"/> while the engine's
    /// pipelines build or the context resolves no device.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is
    /// zero.</exception>
    public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        if (
            (width > m_width) ||
            (height > m_height)
        ) {
            RetireEngine();
            m_width = Math.Max(val1: m_width, val2: width);
            m_height = Math.Max(val1: m_height, val2: height);
        }

        m_scheduledViewExtents[0] = (width, height);
        m_reads = reads;

        try {
            return !ProduceFrame(context: in context).IsEmpty;
        } finally {
            m_reads = null;
        }
    }
    /// <summary>Acquires view 0's latest completed output: its image, in the layout the engine leaves it in between its
    /// submissions (<see cref="SdfWorldEngine.OutputLayout"/>), which a consumer's planned barriers start from and hand
    /// it back in. The acquisition holds the image until its lease is retired.</summary>
    /// <param name="output">The output, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> before the current engine has produced a frame.</returns>
    public bool TryAcquireOutput(out RenderGraphExternalOutput output) => TryAcquireViewOutput(
        output: out output,
        view: 0
    );

    // A replaced engine, and the screen-source leases its submissions sampled.
    private sealed class RetiringEngine(SdfWorldEngine engine) {
        public SdfWorldEngine Engine { get; } = engine;
        public LeaseRetireList ScreenSources { get; } = new();

        // The engine's disposal drains the device, so its screen-source leases retire after it.
        public void Dispose() {
            Engine.Dispose();
            ScreenSources.RetireAll();
        }
    }
    // One view past view 0 as an external producer: the node renders it, and this records its scheduled extent and hands
    // out its output. The node owns it, so its disposal and device loss release nothing.
    private sealed class SdfViewProducer(SdfEngineNode node, int view) : IRenderGraphExternalProducer {
        public SurfaceFormat Format => SurfaceFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => (node.HasViewOutput(view: view)
            ? null
            : (node.NotReadyReason ?? $"view {view} of the world has not rendered"));
        public string? PendingCapturePath => null;
        public IGpuWorkSource Work => node.Work;

        public void Dispose() { }
        public void OnDeviceLost() { }
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            ArgumentOutOfRangeException.ThrowIfZero(value: width);
            ArgumentOutOfRangeException.ThrowIfZero(value: height);

            node.m_scheduledViewExtents[view] = (width, height);

            return node.HasViewOutput(view: view);
        }
        public void RequestCapture(FrameCaptureRequest request) {
            ArgumentNullException.ThrowIfNull(argument: request);

            _ = request.TryFail(error: new NotSupportedException(message: $"View {view} of the world is captured through the render graph's root, not on its own."));
        }
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) => node.TryAcquireViewOutput(
            output: out output,
            view: view
        );
    }
}
