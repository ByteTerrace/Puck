using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Pacing;
using Puck.DirectX.Presentation;
using Puck.Launcher;
using Puck.Memory;
using Puck.Platform;
using Puck.Platform.Windows;
using Puck.Vulkan.Presentation;

namespace Puck.World;

/// <summary>
/// The GPU-host registration for Puck.World: the launcher terminal, platform windowing, the unmanaged allocator, and
/// the launch-selected Vulkan or Direct3D 12 presenter. Registers the GPU-host block (windowing, allocator, one
/// launch-selected backend) directly, and omits the camera-capture concern this game has no use for.
/// </summary>
internal static class WorldHost {
    /// <summary>Registers the shared GPU-host block: launcher terminal, windowing, allocator, and the selected
    /// presenter. Only the selected backend enters this service provider so its neutral compute services, device,
    /// presenter, and shader format cannot disagree.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="hostsOnDirectX">Whether Direct3D 12 is the selected host backend.</param>
    public static void AddWorldGpuHost(IServiceCollection services, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddLauncherTerminal();
        // The concrete native windowing (window factory, clipboard, display probe) the platform-agnostic launcher
        // resolves at runtime.
        services.AddPlatformWindowing();
        // The concrete unmanaged allocator behind the Vulkan backend's IAllocator dependency. Harmless on DirectX,
        // and keeping it in the common block preserves the composition's one backend-selection branch.
        services.AddPuckAllocator();

        if (hostsOnDirectX) {
            if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)) {
                throw new PlatformNotSupportedException(message: "The Direct3D 12 Puck.World host requires Windows 10 or newer.");
            }

            services.AddDirectXPresenter();
            services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
                Name: "directx",
                Presenter: (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)
                    ? sp.GetRequiredService<DirectXSurfacePresenter>()
                    : throw new PlatformNotSupportedException(message: "The Direct3D 12 Puck.World host requires Windows 10 or newer."))
            ));
        } else {
            services.AddVulkanPresenter();
            // The Vulkan block publishes its device context in DI as IVulkanDeviceContext only (the neutral
            // interface rides a HostCapabilityContribution instead). Alias the neutral seam here so backend-neutral
            // consumers (the unified overlay's OverlayServices) resolve the SAME device either backend registers —
            // the DirectX block already registers IGpuDeviceContext itself.
            services.TryAddSingleton<IGpuDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<VulkanRenderer>());
            services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
                Name: "vulkan",
                Presenter: sp.GetRequiredService<VulkanSurfacePresenter>()
            ));
        }

        // The generic switch still fronts the one launch-selected descriptor, preserving the launcher's neutral
        // presenter seam and backend query command. Switching is a no-op because changing compute APIs requires a
        // render-graph rebuild; Puck.World exposes that categorical choice at launch via --backend instead.
        services.AddBackendSwitcher(preferredBackend: (hostsOnDirectX ? "directx" : "vulkan"));
    }

    /// <summary>Registers the headless boot shape's host block: the launcher terminal's headless twin (command pump +
    /// <see cref="HeadlessTickHostedService"/>) and, on Windows, a standalone high-resolution precision waiter for its
    /// pacing loop. NO window, NO GPU device, NO swapchain, NO allocator, NO backend presenter — the direct headless
    /// counterpart to <see cref="AddWorldGpuHost"/>, and the two are never called together.</summary>
    /// <param name="services">The service collection.</param>
    public static void AddWorldHeadlessHost(IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddLauncherHeadlessTerminal();

        // A standalone Win32 high-resolution waitable timer (the same mechanism a native window exposes for the
        // windowed pacer) — optional: absent on non-Windows or an unsupported OS version, where
        // HeadlessTickHostedService falls back to a coarse Thread.Sleep(1) wait, exactly like the windowed pacer's
        // own fallback.
        if (Win32PrecisionWaiter.TryCreate() is { } waiter) {
            services.AddSingleton<IPrecisionWaiter>(implementationInstance: waiter);
        }
    }
}
