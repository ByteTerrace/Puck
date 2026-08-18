using System.Numerics;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm.Views;

/// <summary>
/// A nested world — its own <see cref="ISdfFrameSource"/> (typically a small <see cref="SdfCompositionFrameSource"/>
/// with its own emitters, unrelated to the host world's), rendered offscreen through its own
/// <see cref="SdfWorldEngine"/> exactly as <see cref="SdfCameraView"/> films the host world. A screen surface wired
/// to this view shows an entirely separate SDF program — a world inside the world — and if that world's own
/// emitters include a screen surface wired to yet another nested view, the chain composes (one frame of lag per hop,
/// the same self-reference-safe TV-in-TV rule <see cref="ViewStack"/> enforces for every content kind).
/// </summary>
/// <remarks>
/// No construction sites exist in the buildable tree; the type is currently unconsumed. Its destination is a world
/// document field or console verb that lets a screen show another live, simulated world, tracked as an open item in
/// <c>docs/vision.md</c>'s recursion note; no phase currently owns building that seam. See
/// <see cref="Puck.SignedDistance.Queries.IWorldQuery"/> for the same posture applied to its own destination.
/// </remarks>
public sealed class NestedWorldView : IViewContent, IDisposable {
    /// <summary>The view's fixed render height.</summary>
    public const uint DefaultHeight = 144;
    /// <summary>The view's fixed render width — the native brick panel size (matches <see cref="SdfCameraView.DefaultWidth"/>).</summary>
    public const uint DefaultWidth = 160;

    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_height;
    private readonly bool m_hostsOnDirectX;
    private readonly SdfViewGpuServices m_services;
    private readonly uint m_width;

    private SdfWorldEngine? m_engine;
    private SdfWorldKernels? m_kernels;

    /// <summary>Wraps a nested world's own frame source as view content.</summary>
    /// <param name="services">The concrete GPU-services closure (<see cref="SdfViewGpuServices"/>) this view forwards
    /// to its offscreen engine — resolved once at the composition root and stashed unchanged.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the kernel bytecode).</param>
    /// <param name="frameSource">The nested world's own frame source — captured fresh every <see cref="Resolve"/>,
    /// entirely independent of the host world's program/anchors/emitters.</param>
    /// <param name="width">The render width (default the native panel size).</param>
    /// <param name="height">The render height (default the native panel size).</param>
    public NestedWorldView(SdfViewGpuServices services, bool hostsOnDirectX, ISdfFrameSource frameSource, uint width = DefaultWidth, uint height = DefaultHeight) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(frameSource);

        m_services = services;
        m_hostsOnDirectX = hostsOnDirectX;
        m_frameSource = frameSource;
        m_width = width;
        m_height = height;
    }

    /// <inheritdoc/>
    /// <remarks>Always <see langword="true"/> — a nested-world resolve is a real offscreen render pass (capturing its
    /// own frame source and submitting it).</remarks>
    public bool IsBudgeted => true;
    /// <inheritdoc/>
    /// <remarks>Always zero — a nested world films its own lit content; it contributes no light to the host room
    /// beyond whatever the host's own screen-surface glow accounting already does for a bound image.</remarks>
    public Vector3 RoomGlow => Vector3.Zero;

    // A nested-world filming view has no probe discipline of its own beyond SdfFilmingViewEngine's composed-vs-bare
    // capacity fallback — it must never grow past its first frame's shape when its frame source is not a composition
    // (documented risk, same as any non-composed ISdfFrameSource used directly against SdfWorldEngine).
    private void EnsureEngine(IGpuDeviceContext device, IGpuComputeServices gpu, SdfFrame frame) =>
        SdfFilmingViewEngine.EnsureEngine(
            device: device,
            engine: ref m_engine,
            frame: frame,
            frameSource: m_frameSource,
            gpu: gpu,
            height: m_height,
            hostsOnDirectX: m_hostsOnDirectX,
            kernels: ref m_kernels,
            services: m_services,
            viewLabel: "nested-world view",
            width: m_width
        );

    /// <inheritdoc/>
    public void Dispose() {
        m_engine?.Dispose();
        m_engine = null;
    }
    /// <inheritdoc/>
    public void NotifyDeviceLost() {
        m_frameSource.NotifyDeviceLost();
        m_engine?.Dispose();
        m_engine = null;
    }
    /// <inheritdoc/>
    public nint Resolve(in ViewRenderContext context) {
        if (!context.Host.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
            return 0;
        }

        var frame = m_frameSource.CaptureFrame(
            width: m_width,
            height: m_height,
            deltaSeconds: ((float)context.Host.FrameDeltaSeconds),
            interpolationAlpha: ((float)context.Host.InterpolationAlpha)
        );

        EnsureEngine(
            device: device,
            gpu: m_services.Gpu,
            frame: frame
        );
        m_engine!.SubmitFrame(frame: frame);

        return m_engine.OutputImageViewHandle;
    }
}
