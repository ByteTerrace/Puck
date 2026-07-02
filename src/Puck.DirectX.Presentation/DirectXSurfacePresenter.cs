using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Presentation;

/// <summary>
/// The Direct3D 12 <see cref="ISurfacePresenter"/>: a thin facade over <see cref="DirectXSurfaceCompositor"/>
/// (the DXGI swap chain and fullscreen blit), routed through the shared <see cref="DirectXDeviceContext"/>. The
/// host loop drives <see cref="Present(Surface)"/> through the backend-neutral seam; multi-draw compositing is
/// done backend-neutrally through <c>Puck.Compositing.GpuCompositor</c>, which composes into an offscreen
/// render target that this presenter then blits.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXSurfacePresenter : ISurfacePresenter, IPresentTimingFeedback, IDeviceLostRecoverable {
    private readonly DirectXSurfaceCompositor m_compositor;
    private readonly DirectXDeviceContext m_deviceContext;

    /// <summary>Initializes a new instance of the <see cref="DirectXSurfacePresenter"/> class.</summary>
    /// <param name="deviceContext">The shared device and command queue.</param>
    /// <param name="compositor">The DXGI swap chain and multi-draw compositor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="deviceContext"/> or <paramref name="compositor"/> is <see langword="null"/>.</exception>
    public DirectXSurfacePresenter(DirectXDeviceContext deviceContext, DirectXSurfaceCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(deviceContext);

        m_compositor = compositor;
        m_deviceContext = deviceContext;
    }

    /// <inheritdoc/>
    public void Activate(NativeSurfaceBinding binding, uint width, uint height) {
        // The contract is "safe to call repeatedly — each call replaces any previously acquired resources",
        // so release any prior activation before re-acquiring.
        Deactivate();
        m_compositor.Initialize(
            deviceContext: m_deviceContext,
            binding: binding,
            height: height,
            width: width
        );
    }
    /// <inheritdoc/>
    public void Deactivate() {
        if (m_deviceContext.IsInitialized) {
            m_deviceContext.WaitIdle();
        }

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
    public void RecoverFromDeviceLoss(NativeSurfaceBinding binding, uint width, uint height) {
        // Release the compositor's swap chain / heaps / blit resources on the OLD (removed) device — COM Release is safe
        // on a removed device's objects, and these are not recreated by the device context. Then recreate the device IN
        // PLACE (preserving the shared capability's identity so the compute node resolving it stays valid), and
        // re-initialize the compositor against the new device. The node tree rebuilds its own resources next frame.
        m_compositor.Dispose();

        try {
            m_deviceContext.Recreate();
            m_compositor.Initialize(
                deviceContext: m_deviceContext,
                binding: binding,
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
        (m_compositor.TryGetPresentTiming(out var presentCount, out var presentQpcTicks)
            ? new PresentTimingSample(PresentCount: presentCount, PresentTimestampTicks: presentQpcTicks)
            : PresentTimingSample.Unavailable);
    /// <inheritdoc/>
    public void Dispose() {
        Deactivate();
    }
}
