using Puck.Abstractions;

namespace Puck.Vulkan.Presentation;

/// <summary>
/// The Vulkan <see cref="ISurfacePresenter"/>: a thin facade over the <see cref="VulkanRenderer"/> (the
/// window and swapchain owner plus the per-frame GPU gate) and its <see cref="SurfaceCompositor"/> (the
/// fullscreen surface blit), so the host loop drives Vulkan presentation through the backend-neutral seam
/// without referencing either concrete type.
/// </summary>
public sealed class VulkanSurfacePresenter : ISurfacePresenter {
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
    public void Deactivate() {
        m_compositor.Dispose();
        m_renderer.Dispose();
    }
    /// <inheritdoc/>
    public void BeginFrame(uint width, uint height) {
        m_renderer.BeginFrame(
            height: height,
            width: width
        );
        // The frame-boundary gate: drain the previous frame's GPU work before the node tree reuses its
        // per-frame resources. Folded behind the seam so the host loop stays backend-agnostic.
        m_renderer.WaitForGpuIdle();
    }
    /// <inheritdoc/>
    public void Present(Surface surface) {
        m_compositor.Blit(surface: surface);
    }
    /// <inheritdoc/>
    public void Dispose() {
        Deactivate();
    }
}
