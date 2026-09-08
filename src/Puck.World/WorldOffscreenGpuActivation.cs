using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;

namespace Puck.World;

/// <summary>
/// Brings the offscreen shape's (<c>host.presentation: offscreen</c>) GPU device up BEFORE the render root is first
/// resolved — resolved eagerly by <c>Program.cs</c>, right after <c>IHost.Build()</c>, exactly where
/// <see cref="WorldPostBuildWiring.Install"/> also runs.
/// <para>
/// Direct3D 12 is genuinely surfaceless here: <c>Puck.DirectX.Interop.DirectXDeviceContext</c> creates its device and
/// command queue lazily on an adapter LUID, with no window or surface of any kind — see
/// <c>experimental/Puck.Post/PostDirectXDevice.cs</c> for the prior art this mirrors. Nothing in this class touches a
/// window on that backend; the device activates on the render root's first GPU call, and calling
/// <see cref="ISurfacePresenter.Activate"/> against it would build a DXGI swap chain eagerly (the Direct3D 12
/// compositor's own <c>Initialize</c> creates one immediately, unlike the Vulkan renderer below) — exactly the
/// swapchain this shape must never create — so this class never calls it on that backend.
/// </para>
/// <para>
/// Vulkan is not surfaceless: its <c>VulkanRenderer.Initialize</c> — the only device-bring-up path this backend has —
/// hard-requires a real <see cref="NativeSurfaceBinding"/> with a payload (it throws otherwise), and physical-device
/// selection queries present support against that same surface, so there is no surfaceless compute-only Vulkan device
/// in this codebase today. The fallback: a real native window, created through the SAME
/// <see cref="INativeWindowFactory"/>/<c>NativeWindowMode.PlatformWindow</c> path the windowed shape uses, but
/// <see cref="INativeWindow.Show"/> is NEVER called on it — Win32 (and the Wayland/Xcb backends) create a window
/// hidden by default, so an unshown window carries no on-screen presence at all. <see cref="ISurfacePresenter.Activate"/>
/// builds the Vulkan instance/surface/device (and the compositor's unused blit-pipeline objects) but — unlike Direct3D
/// 12 — defers the swap chain itself to the first <see cref="ISurfacePresenter.BeginFrame"/>, which this class never
/// calls, so no swap chain is ever created on this backend either. The window's own lifetime becomes this instance's.
/// </para>
/// </summary>
internal sealed class WorldOffscreenGpuActivation : IDisposable {
    private readonly INativeWindow? m_hiddenWindow;

    public WorldOffscreenGpuActivation(WorldHostSettings hostSettings, IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(hostSettings);
        ArgumentNullException.ThrowIfNull(services);

        if (hostSettings.HostsOnDirectX) {
            return;
        }

        var windowFactory = services.GetRequiredService<INativeWindowFactory>();
        var window = windowFactory.Create();

        try {
            services.GetRequiredService<ISurfacePresenter>().Activate(
                binding: window.CreateSurfaceBinding(),
                height: ((uint)hostSettings.Height),
                width: ((uint)hostSettings.Width)
            );
        } catch {
            window.Dispose();

            throw;
        }

        m_hiddenWindow = window;
    }

    public void Dispose() => m_hiddenWindow?.Dispose();
}
