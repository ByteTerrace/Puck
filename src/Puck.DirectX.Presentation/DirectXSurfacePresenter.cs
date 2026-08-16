using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Presentation;

/// <summary>
/// The Direct3D 12 <see cref="ISurfacePresenter"/>: a thin facade over <see cref="DirectXSurfaceCompositor"/>
/// (the DXGI swap chain and fullscreen blit), routed through the shared <see cref="DirectXDeviceContext"/>. The
/// host loop drives <see cref="Present(Surface)"/> through the backend-neutral seam, and that is the whole of
/// what this presenter records: one fullscreen blit of the surface handed to it. Compositing happens BEFORE the
/// surface arrives — the SDF compute kernel composites its views into a single image — so the presenter never
/// sees more than one draw. The compositor's multi-draw overload exists for that case and nothing drives it.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXSurfacePresenter : ISurfacePresenter, IPresentSurfaceReadback, IPresentTimingFeedback, IDeviceLostRecoverable {
    private readonly DirectXSurfaceCompositor m_compositor;
    private readonly DirectXDeviceContext m_deviceContext;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;

    private IGpuSurfaceImport? m_captureImport;
    private IGpuSurfaceReadback? m_captureReadback;

    /// <summary>Initializes a new instance of the <see cref="DirectXSurfacePresenter"/> class.</summary>
    /// <param name="deviceContext">The shared device and command queue.</param>
    /// <param name="compositor">The DXGI swap chain and blit pipeline.</param>
    /// <param name="surfaceTransferFactory">Creates the lazily armed capture readback and shared-surface importer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/>, <paramref name="compositor"/>, or
    /// <paramref name="surfaceTransferFactory"/> is <see langword="null"/>.</exception>
    public DirectXSurfacePresenter(DirectXDeviceContext deviceContext, DirectXSurfaceCompositor compositor, IGpuSurfaceTransferFactory surfaceTransferFactory) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(surfaceTransferFactory);

        m_compositor = compositor;
        m_deviceContext = deviceContext;
        m_surfaceTransferFactory = surfaceTransferFactory;
    }

    private static GpuPixelFormat ToGpuFormat(SurfaceFormat format) => format switch {
        SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
        SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
        _ => throw new InvalidOperationException(message: "The surface format has no DirectX readback mapping."),
    };
    private void ReleaseCaptureResources() {
        m_captureReadback?.Dispose();
        m_captureReadback = null;
        m_captureImport?.Dispose();
        m_captureImport = null;
    }

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

        var format = ToGpuFormat(format: surface.Format);
        var imageHandle = surface.ImageHandle;

        if (surface.IsSharedHandle) {
            m_captureImport ??= m_surfaceTransferFactory.CreateImport(deviceContext: m_deviceContext);
            imageHandle = m_captureImport.Import(
                deviceContext: m_deviceContext,
                format: format,
                height: surface.Height,
                sharedHandle: surface.SharedHandle,
                width: surface.Width
            ).ImageHandle;
        }

        m_captureReadback ??= m_surfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);
        var pixels = m_captureReadback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: format,
            height: surface.Height,
            sourceImageHandle: imageHandle,
            width: surface.Width
        );

        return Surface.CpuPixels(
            format: surface.Format,
            height: surface.Height,
            pixels: pixels,
            width: surface.Width
        );
    }
    /// <inheritdoc/>
    public void RecoverFromDeviceLoss(NativeSurfaceBinding binding, uint width, uint height) {
        // Release the compositor's swap chain / heaps / blit resources on the OLD (removed) device — COM Release is safe
        // on a removed device's objects, and these are not recreated by the device context. Then recreate the device IN
        // PLACE (preserving the shared capability's identity so the compute node resolving it stays valid), and
        // re-initialize the compositor against the new device. The node tree rebuilds its own resources next frame.
        ReleaseCaptureResources();
        m_compositor.Dispose();

        try {
            m_deviceContext.Recreate();
            m_compositor.Initialize(
                binding: binding,
                deviceContext: m_deviceContext,
                height: height,
                width: width
            );
        } catch (DeviceLostException) {
            throw;
        } catch (DirectXException exception) {
            // The device could not be recreated — almost always because the adapter has not come back yet (a real
            // removal leaves no capable device for seconds; D3D12CreateDevice fails until it returns). Surface it as the
            // neutral recoverable signal so the host pump waits and retries rather than aborting the run.
            throw new DeviceLostException(message: "The Direct3D 12 device could not be recreated yet (the adapter is unavailable).", reasonCode: exception.Result, innerException: exception);
        }
    }

    /// <inheritdoc/>
    public PresentTimingSample LastPresentTiming =>
        (m_compositor.TryGetPresentTiming(presentCount: out var presentCount, presentQpcTicks: out var presentQpcTicks)
            ? new PresentTimingSample(PresentCount: presentCount, PresentTimestampTicks: presentQpcTicks)
            : PresentTimingSample.Unavailable);

    /// <inheritdoc/>
    public void Dispose() {
        Deactivate();
    }
}
