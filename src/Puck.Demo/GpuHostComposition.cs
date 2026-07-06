using Microsoft.Extensions.DependencyInjection;
using Puck.DirectX.Presentation;
using Puck.Launcher;
using Puck.Memory;
using Puck.Platform;
using Puck.Vulkan.Presentation;

namespace Puck.Demo;

/// <summary>
/// The one trimmed-GPU-host recipe every demo composition root shares: launcher terminal, platform windowing, camera
/// capture, the unmanaged allocator, both presenters, and the named backend switch. <see cref="DemoHost"/> (the live
/// demo) and <c>ForgeHost</c> (the one-shot forge harness) both build on this instead of re-declaring the block.
/// <c>Puck.Post</c> keeps its own deliberately VALUE-COPIED equivalent (the POST and the demo cross-check each other;
/// never fold the two together).
/// </summary>
internal static class GpuHostComposition {
    /// <summary>Registers the shared GPU-host block. ORDER MATTERS inside: <c>AddVulkanPresenter</c> MUST precede
    /// <c>AddDirectXPresenter</c>. Both chain in the neutral compute seam (AddVulkanComputeApis / AddDirectXComputeApis),
    /// which TryAdd the SAME IGpuCompute*/IGpuTiming*/IGpuComputeServices types into this one container — so whichever
    /// registers FIRST wins. The Vulkan-hosted producers resolve those from THIS provider and need the Vulkan device's
    /// adapters, so Vulkan must win. (The off-host Direct3D 12 nodes build their own isolated collection and are
    /// unaffected.)</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="preferredBackend">The backend the launcher's switch fronts first ("vulkan" or "directx").</param>
    /// <param name="registerDirectXBackend">Whether Direct3D 12 is contributed as a NAMED presenter the backend switch
    /// can select (the live demo); the forge harness always hosts on Vulkan and skips the named registration while
    /// still chaining the DirectX compute seam.</param>
    public static void AddTrimmedGpuHost(IServiceCollection services, string preferredBackend, bool registerDirectXBackend) {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLauncherTerminal();
        // The launcher run loop is platform-agnostic; the composition root supplies the concrete native windowing (the
        // window factory + clipboard + display probe) it resolves from the container at runtime.
        services.AddPlatformWindowing();
        services.AddCameraCapture();
        // Bind the concrete unmanaged allocator here, at the composition root — the Vulkan backend depends only on the
        // IAllocator abstraction and resolves it from the container.
        services.AddPuckAllocator();
        services.AddVulkanPresenter();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            services.AddDirectXPresenter();
        }
        // Contribute the concrete backends as NAMED presenters; the launcher's backend switch (AddBackendSwitcher) picks
        // the preferred one and fronts the rest behind ISurfacePresenter. The launcher never names a backend — only the
        // composition roots do, here.
        services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(Name: "vulkan", Presenter: sp.GetRequiredService<VulkanSurfacePresenter>()));
        if (registerDirectXBackend && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            services.AddSingleton(implementationFactory: static sp => new SurfacePresenterDescriptor(
                Name: "directx",
                Presenter: (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
                    ? sp.GetRequiredService<DirectXSurfacePresenter>()
                    : throw new PlatformNotSupportedException())));
        }
        services.AddBackendSwitcher(preferredBackend: preferredBackend);
    }
}
