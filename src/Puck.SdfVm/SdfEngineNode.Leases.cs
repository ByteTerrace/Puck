using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.SdfVm;

// The node as the external producer behind sdf.world: the graph runtime produces it at the scheduled extent through
// the engine's own ring, and each consumer acquires the engine's latest completed output under a lease. Every
// acquisition is counted against the engine that wrote the image, so an engine replaced at a new extent is disposed
// only once the submissions sampling its output have released it; its Dispose still drains the device, as the backstop.
public sealed partial class SdfEngineNode : IRenderGraphExternalProducer {
    // Replaced engines whose output a consumer still holds, each disposed when its last acquisition is released.
    private readonly List<RetiringEngine> m_retiringEngines = [];

    // The token of the current engine's acquisitions: raised for every engine the node creates (never for a refused
    // build), so an acquisition of an engine a device loss released never matches a later one.
    private int m_engineToken;
    // The current engine's acquisitions not yet released.
    private int m_outputAcquisitions;
    // Created on the first acquisition, so a lease allocates nothing per frame.
    private Action<int>? m_releaseOutput;

    /// <summary>Gets the acquisitions of the node's output not yet released, over its current engine and every replaced
    /// engine still held.</summary>
    public int OutputLeases {
        get {
            var leases = m_outputAcquisitions;

            foreach (var retiring in m_retiringEngines) {
                leases += retiring.Acquisitions;
            }

            return leases;
        }
    }
    /// <summary>Gets the replaced engines not yet disposed because a consumer still holds their output.</summary>
    public int RetiringEngines => m_retiringEngines.Count;

    SurfaceFormat IRenderGraphExternalProducer.Format => SurfaceFormat.R8G8B8A8Unorm;

    // Disposes every replaced engine, whatever it still has leased: after a drain, or once the device is lost.
    private void DisposeRetiringEngines() {
        foreach (var retiring in m_retiringEngines) {
            retiring.Dispose();
        }

        m_retiringEngines.Clear();
        m_outputAcquisitions = 0;
    }
    private void ReleaseOutput(int token) {
        if (
            (token == m_engineToken) &&
            (m_engine is not null)
        ) {
            m_outputAcquisitions--;

            return;
        }

        for (var index = 0; (index < m_retiringEngines.Count); index++) {
            var retiring = m_retiringEngines[index];

            if (retiring.Token != token) {
                continue;
            }
            if (--retiring.Acquisitions == 0) {
                m_retiringEngines.RemoveAt(index: index);
                retiring.Dispose();
            }

            return;
        }
    }
    // Replaces the engine at the next produced frame. The replaced engine is disposed now when nothing holds its output,
    // and otherwise when the last acquisition is released; the screen-source leases its submissions sampled go with it.
    private void RetireEngine() {
        if (m_engine is not { } engine) {
            return;
        }

        var retiring = new RetiringEngine(
            acquisitions: m_outputAcquisitions,
            engine: engine,
            token: m_engineToken
        );

        foreach (var retained in m_retainedScreenSourceFrames) {
            retained.MoveTo(destination: retiring.ScreenSources);
        }

        m_engine = null;
        m_engineProduced = false;
        m_outputAcquisitions = 0;
        m_glyphAtlasInitialized = false;
        m_uploadedGlyphAtlas = null;

        if (retiring.Acquisitions == 0) {
            retiring.Dispose();
        } else {
            m_retiringEngines.Add(item: retiring);
        }
    }

    /// <summary>Renders one frame at an extent through the engine's own ring. A new extent replaces the engine first: the
    /// replacement is built at once from the installed pipeline set, and the replaced engine is disposed once every
    /// acquisition of its output is released.</summary>
    /// <param name="context">The host's frame context, which resolves the device.</param>
    /// <param name="width">The extent width, in pixels.</param>
    /// <param name="height">The extent height, in pixels.</param>
    /// <returns><see langword="true"/> when the frame was submitted; <see langword="false"/> while the engine's
    /// pipelines build or the context resolves no device.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is
    /// zero.</exception>
    public bool Produce(in FrameContext context, uint width, uint height) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);

        if (
            (width != m_width) ||
            (height != m_height)
        ) {
            RetireEngine();
            m_width = width;
            m_height = height;
        }

        return !ProduceFrame(context: in context).IsEmpty;
    }
    /// <summary>Acquires the engine's latest completed output: its storage image, in the layout the engine leaves it in
    /// between its submissions (<see cref="SdfWorldEngine.OutputLayout"/>), which a consumer's planned barriers start from
    /// and hand it back in. The acquisition is counted until its lease is retired.</summary>
    /// <param name="output">The output, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> before the current engine has produced a frame.</returns>
    public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
        if (
            !m_engineProduced ||
            (m_engine is not { } engine)
        ) {
            output = default;

            return false;
        }

        m_outputAcquisitions++;
        output = new RenderGraphExternalOutput(
            Image: Surface.SameDeviceImage(
                format: SurfaceFormat.R8G8B8A8Unorm,
                height: m_height,
                imageHandle: engine.OutputImageHandle,
                imageViewHandle: engine.OutputImageViewHandle,
                width: m_width
            ),
            Layout: engine.OutputLayout,

            Lease: new GpuImageLease(
                ImageViewHandle: engine.OutputImageViewHandle,
                Release: (m_releaseOutput ??= ReleaseOutput),
                ReleaseToken: m_engineToken
            )
        );

        return true;
    }

    // A replaced engine, the acquisitions of its output still held, and the screen-source leases its submissions sampled.
    private sealed class RetiringEngine(SdfWorldEngine engine, int token, int acquisitions) {
        public int Acquisitions = acquisitions;
        public LeaseRetireList ScreenSources { get; } = new();
        public int Token { get; } = token;

        // The engine's disposal drains the device, so its screen-source leases retire after it.
        public void Dispose() {
            engine.Dispose();
            ScreenSources.RetireAll();
        }
    }
}
