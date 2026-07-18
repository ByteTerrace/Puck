using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
namespace Puck.Vulkan.Presentation;

/// <summary>
/// The Vulkan <see cref="ISurfacePresenter"/>: a thin facade over the <see cref="VulkanRenderer"/> (the
/// window and swapchain owner plus the per-frame GPU gate) and its <see cref="SurfaceCompositor"/> (the
/// fullscreen surface blit), so the host loop drives Vulkan presentation through the backend-neutral seam
/// without referencing either concrete type.
/// </summary>
public sealed class VulkanSurfacePresenter : ISurfacePresenter, IPresentTimingFeedback, IDeviceLostRecoverable, IPresentationSkipFeedback {
    private readonly SurfaceCompositor m_compositor;
    private readonly VulkanRenderer m_renderer;

    /// <summary>Initializes a new instance of the <see cref="VulkanSurfacePresenter"/> class.</summary>
    /// <param name="renderer">The window and swapchain owner.</param>
    /// <param name="compositor">The fullscreen surface-blit compositor.</param>
    /// <exception cref="ArgumentNullException"><paramref name="renderer"/> or <paramref name="compositor"/> is <see langword="null"/>.</exception>
    public VulkanSurfacePresenter(VulkanRenderer renderer, SurfaceCompositor compositor) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(renderer);

        m_compositor = compositor;
        m_renderer = renderer;
    }

    /// <inheritdoc/>
    public void Activate(NativeSurfaceBinding binding, uint width, uint height) {
        // The contract is "safe to call repeatedly — each call replaces any previously acquired resources",
        // so release any prior activation before re-acquiring.
        Deactivate();
        m_renderer.Initialize(
            binding: binding,
            height: height,
            width: width
        );
        m_compositor.Initialize();
    }
    /// <inheritdoc/>
    /// <remarks>Releases the presentation stack (compositor blit resources, swapchain chain, window surface) but
    /// KEEPS the device alive: the renderer is the published device-context capability, and node resources are
    /// children of its device — a backend switch away from Vulkan must not destroy it under them. The device itself
    /// is torn down once, by the renderer's own container-owned disposal at host shutdown (mirroring the Direct3D 12
    /// presenter, whose Deactivate has always left its device-context singleton alive).</remarks>
    public void Deactivate() {
        m_compositor.Dispose();
        m_renderer.ReleasePresentation();
    }
    /// <inheritdoc/>
    public void BeginFrame(uint width, uint height) {
        m_renderer.BeginFrame(
            height: height,
            width: width
        );
        // The frame-boundary gate, PIPELINED: wait only on the presentation ring slot's fence — the present two
        // frames back — instead of draining the whole device, so this frame's CPU production overlaps the previous
        // frame's GPU execution (wall interval → ~max(GPU, produce) when GPU-bound). Per-frame resource reuse is
        // guarded by each SdfWorldEngine's own frame ring; this wait bounds host latency to the ring depth. The
        // FULL drain remains the resize/device-loss/shutdown path (renderer BeginFrame recreation, RecoverFromDeviceLoss,
        // the host's teardown WaitIdle). Folded behind the seam so the host loop stays backend-agnostic — the
        // [frame-timing] "gpu-drain" bucket now measures this bounded wait.
        m_renderer.WaitForFrameSlot();
    }
    /// <inheritdoc/>
    public void Present(Surface surface) {
        m_compositor.Blit(surface: surface);
    }
    /// <inheritdoc/>
    public void RecoverFromDeviceLoss(NativeSurfaceBinding binding, uint width, uint height) {
        // Release the compositor's device-level blit resources on the OLD device BEFORE it is destroyed — they are not
        // swapchain resources, so RecreateDevice would otherwise leave them dangling on the device it destroys (a
        // validation error + crash). The compositor stays subscribed and rebuilds them on the new device at the next
        // BeginFrame's PresentationResourcesRecreated.
        m_compositor.ReleaseForDeviceLoss();

        // Recreate the lost device IN PLACE on the renderer (keeping object identity so the device-context capability and
        // node references stay valid; nodes + compositor rebuild against the new handle).
        m_renderer.RecreateDevice(
            binding: binding,
            height: height,
            width: width
        );
    }
    /// <inheritdoc/>
    public PresentTimingSample LastPresentTiming =>
        (m_renderer.TryGetPresentTiming(presentCount: out var presentCount, presentTimestampTicks: out var presentTimestampTicks)
            ? new PresentTimingSample(PresentCount: presentCount, PresentTimestampTicks: presentTimestampTicks)
            : PresentTimingSample.Unavailable);
    /// <inheritdoc/>
    public ulong SkippedPresentCount => m_renderer.SkippedPresentCount;
    /// <inheritdoc/>
    public void Dispose() {
        Deactivate();
    }
}
