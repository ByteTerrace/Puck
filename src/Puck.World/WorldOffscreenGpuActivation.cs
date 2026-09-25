using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Windowing;
using Puck.DirectX.Interop;
using Puck.Launcher;

namespace Puck.World;

/// <summary>
/// Brings the offscreen shape's (<c>host.presentation: offscreen</c>) GPU device up BEFORE the render root is first
/// resolved — resolved eagerly by <c>Program.cs</c>, right after <c>IHost.Build()</c>, exactly where
/// <see cref="WorldPostBuildWiring.Install"/> also runs — and rebuilds it after a loss, as the offscreen host's
/// <see cref="IDeviceRebuild"/>.
/// <para>
/// Direct3D 12 is genuinely surfaceless here: <see cref="DirectXDeviceContext"/> creates its device and
/// command queue lazily on an adapter LUID, with no window or surface of any kind — see
/// <c>experimental/Puck.Post/PostDirectXDevice.cs</c> for the prior art this mirrors. Nothing in this class touches a
/// window on that backend; the device activates on the render root's first GPU call, and calling
/// <see cref="ISurfacePresenter.Activate"/> against it would build a DXGI swap chain eagerly (the Direct3D 12
/// compositor's own <c>Initialize</c> creates one immediately, unlike the Vulkan renderer below) — exactly the
/// swapchain this shape must never create — so this class never calls it on that backend, and a rebuild recreates the
/// device context in place (<see cref="DirectXDeviceContext.Recreate"/>) with no swap chain either.
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
/// calls, so no swap chain is ever created on this backend either. A rebuild hands the presenter
/// (<see cref="IDeviceLostRecoverable"/>) the same hidden window's surface binding, and it too defers the swap chain.
/// The window's own lifetime becomes this instance's.
/// </para>
/// </summary>
internal sealed class WorldOffscreenGpuActivation : IDeviceRebuild, IDisposable {
    private readonly NativeSurfaceBinding m_binding;
    private readonly IGpuDeviceContext? m_directX;
    private readonly uint m_height;
    private readonly INativeWindow? m_hiddenWindow;
    private readonly IDeviceLostRecoverable? m_presenter;
    private readonly uint m_width;

    public WorldOffscreenGpuActivation(WorldHostSettings hostSettings, IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(hostSettings);
        ArgumentNullException.ThrowIfNull(services);

        m_height = ((uint)hostSettings.Height);
        m_width = ((uint)hostSettings.Width);

        if (hostSettings.HostsOnDirectX) {
            m_directX = services.GetRequiredService<IGpuDeviceContext>();
            // The device is otherwise created by the render root's first GPU call, inside the frame loop; bringing it
            // up here raises an absent adapter's GpuDeviceUnavailableException while the host starts, where
            // LauncherHostRun reports it as the unsupported-environment outcome. Reading the adapter LUID creates
            // the Direct3D 12 device.
            _ = m_directX.AdapterLuid;

            return;
        }

        var window = services.GetRequiredService<INativeWindowFactory>().Create();
        var presenter = services.GetRequiredService<ISurfacePresenter>();

        try {
            m_binding = window.CreateSurfaceBinding();
            presenter.Activate(
                binding: m_binding,
                height: m_height,
                width: m_width
            );
        } catch {
            window.Dispose();

            throw;
        }

        m_hiddenWindow = window;
        m_presenter = (presenter as IDeviceLostRecoverable);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The composed device context or presenter cannot rebuild its
    /// device.</exception>
    public void Rebuild() {
        if (m_directX is not null) {
            if (
                !OperatingSystem.IsWindowsVersionAtLeast(
                    build: 10240,
                    major: 10,
                    minor: 0
                ) ||
                (m_directX is not DirectXDeviceContext directX)
            ) {
                throw new InvalidOperationException(message: "The offscreen Direct3D 12 device context cannot rebuild its device.");
            }

            directX.Recreate();

            return;
        }

        (m_presenter ?? throw new InvalidOperationException(message: "The offscreen Vulkan presenter cannot rebuild its device.")).RecoverFromDeviceLoss(
            binding: m_binding,
            height: m_height,
            width: m_width
        );
    }
    public void Dispose() => m_hiddenWindow?.Dispose();
}
