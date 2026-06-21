namespace Puck.Abstractions;

/// <summary>
/// The backend-neutral seam between the host loop and a graphics backend: given a native surface binding and
/// the current frame size, it owns the presentation lifecycle (swapchain and back buffers, recreated on
/// resize) and presents the one <see cref="Surface"/> the root render node produces each frame. It is
/// deliberately decoupled from windowing and input — the host owns the window and feeds the size — so it can
/// live in the dependency-free abstractions leaf and be implemented by any backend (Vulkan, DirectX, …).
/// </summary>
public interface ISurfacePresenter : IDisposable {
    /// <summary>Acquires GPU presentation resources (swap chain, back buffers, render passes) for a native
    /// surface binding. Safe to call repeatedly — each call replaces any previously acquired resources.</summary>
    /// <param name="binding">The native surface binding identifying the surface to present into.</param>
    /// <param name="width">The initial render-target width in pixels.</param>
    /// <param name="height">The initial render-target height in pixels.</param>
    void Activate(NativeSurfaceBinding binding, uint width, uint height);
    /// <summary>Releases all GPU presentation resources acquired by <see cref="Activate"/>. Idempotent — safe
    /// to call when not active or more than once.</summary>
    void Deactivate();
    /// <summary>Prepares the next frame: recreates presentation resources when the target size changed, then
    /// waits for the previous frame's submitted work so its per-frame resources can be safely reused.</summary>
    /// <param name="width">The current render-target width in pixels.</param>
    /// <param name="height">The current render-target height in pixels.</param>
    void BeginFrame(uint width, uint height);
    /// <summary>Presents one surface, fullscreen. A no-op when the surface is empty (a skipped frame).</summary>
    /// <param name="surface">The surface the root render node produced this frame.</param>
    void Present(Surface surface);
}
