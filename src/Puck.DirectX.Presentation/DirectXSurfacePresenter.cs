using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.DirectX.Presentation;

/// <summary>
/// The Direct3D 12 <see cref="ISurfacePresenter"/>: a thin facade over <see cref="DirectXSurfaceCompositor"/>
/// (the DXGI swap chain and fullscreen display encode), routed through the shared <see cref="DirectXDeviceContext"/>.
/// The host loop drives <see cref="Present(Surface)"/> through the backend-neutral seam, and that is the whole of what
/// this presenter records: one fullscreen encode of the surface handed to it. Compositing happens BEFORE the surface
/// arrives — the render graph's root places every view and pane into one image — so the presenter never sees more than
/// one draw. The compositor's multi-draw overload exists for that case and nothing drives it.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXSurfacePresenter : ISurfacePresenter, IPresentSurfaceReadback, IPresentTimingFeedback, IDeviceLostRecoverable {
    private readonly DirectXSurfaceCompositor m_compositor;
    private readonly DirectXDeviceContext m_deviceContext;
    private readonly GpuPassPipelineCache m_pipelines;

    private SurfaceEncoder? m_captureEncoder;
    private IGpuSurfaceImport? m_captureImport;
    private IGpuSurfaceReadback? m_captureReadback;

    /// <summary>Initializes a new instance of the <see cref="DirectXSurfacePresenter"/> class.</summary>
    /// <param name="deviceContext">The shared device and command queue, whose services create the lazily armed capture
    /// readback, encoder and shared-surface importer.</param>
    /// <param name="compositor">The DXGI swap chain and display-encode pipeline.</param>
    /// <param name="pipelines">The composition's pass pipelines, which a capture's display encode is an entry of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/>, <paramref name="compositor"/> or
    /// <paramref name="pipelines"/> is <see langword="null"/>.</exception>
    public DirectXSurfacePresenter(DirectXDeviceContext deviceContext, DirectXSurfaceCompositor compositor, GpuPassPipelineCache pipelines) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(pipelines);

        m_compositor = compositor;
        m_deviceContext = deviceContext;
        m_pipelines = pipelines;
    }

    private void ReleaseCaptureResources() {
        m_captureEncoder?.Dispose();
        m_captureEncoder = null;
        m_captureReadback?.Dispose();
        m_captureReadback = null;
        m_captureImport?.Dispose();
        m_captureImport = null;
    }

    /// <inheritdoc/>
    public DisplayOutput? Output => m_compositor.Output;

    /// <inheritdoc/>
    public void Activate(NativeSurfaceBinding binding, uint width, uint height) {
        // The contract is "safe to call repeatedly — each call replaces any previously acquired resources",
        // so release any prior activation before re-acquiring.
        Deactivate();
        m_compositor.Initialize(
            binding: binding,
            deviceContext: m_deviceContext,
            height: height,
            width: width
        );
    }
    /// <inheritdoc/>
    public void Deactivate() {
        if (m_deviceContext.IsInitialized) {
            m_deviceContext.WaitIdle();
        }

        ReleaseCaptureResources();
        m_compositor.Dispose();
    }
    /// <inheritdoc/>
    public void BeginFrame(uint width, uint height) {
        m_compositor.BeginFrame(
            deviceContext: m_deviceContext,
            height: height,
            width: width
        );
    }
    /// <inheritdoc/>
    public void Present(Surface surface) {
        m_compositor.Blit(
            deviceContext: m_deviceContext,
            surface: surface
        );
    }
    /// <inheritdoc/>
    public Surface ReadSurface(Surface surface) {
        if (
            surface.IsEmpty ||
            surface.IsCpuPixels
        ) {
            return surface;
        }
        // A working image no capture sink reads is captured through the display encode's SDR.
        if (
            surface.IsSameDeviceImage &&
            !Surface.IsSurfaceFormat(format: surface.Format)
        ) {
            return (m_captureEncoder ??= new SurfaceEncoder(
                device: m_deviceContext,
                directX: true,
                owner: "presentation-capture",
                pipelines: m_pipelines
            )).ReadSurface(surface: surface);
        }

        return SurfaceReadbackCapture.ReadSurface(
            captureImport: ref m_captureImport,
            captureReadback: ref m_captureReadback,
            deviceContext: m_deviceContext,
            surface: surface
        );
    }
    /// <inheritdoc/>
    public void RecoverFromDeviceLoss(NativeSurfaceBinding binding, uint width, uint height) {
        // Release the compositor's swap chain / heaps / encode resources on the OLD (removed) device — COM Release is safe
        // on a removed device's objects, and these are not recreated by the device context. Then recreate the device IN
        // PLACE (preserving the shared capability's identity so the compute node resolving it stays valid), and
        // re-initialize the compositor against the new device. The render root rebuilds its own resources next frame.
        ReleaseCaptureResources();
        m_compositor.Dispose();

        // A swap chain the returning adapter cannot back yet is the same wait as an absent adapter: Recreate's one retry
        // rule, which the offscreen host's rebuild follows too.
        m_deviceContext.Recreate(reinitialize: () => m_compositor.Initialize(
            binding: binding,
            deviceContext: m_deviceContext,
            height: height,
            width: width
        ));
    }

    /// <inheritdoc/>
    public PresentTimingSample LastPresentTiming =>
        (m_compositor.TryGetPresentTiming(
            presentCount: out var presentCount,
            presentQpcTicks: out var presentQpcTicks
        )
            ? new PresentTimingSample(
                PresentCount: presentCount,
                PresentTimestampTicks: presentQpcTicks
            )
            : PresentTimingSample.Unavailable
        );

    /// <inheritdoc/>
    public void Dispose() {
        Deactivate();
    }
}
