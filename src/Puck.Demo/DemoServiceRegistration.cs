using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Assets;
using Puck.Platform;
using Puck.Platform.Windows;
using Puck.Shaders;
using Puck.Vulkan;
using Puck.Vulkan.Apis;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;

namespace Puck.Demo;

/// <summary>Explicit, composable service registration for the demo: every dependency is wired by hand
/// against the new Puck.* libraries — no engine-wide bring-up helper. Grouped by concern so the
/// composition root stays readable, and trimmed to exactly what the showcase touches.</summary>
internal static class DemoServiceRegistration {
    /// <summary>Platform: the native window + surface stack (à la carte; no engine host).</summary>
    public static IServiceCollection AddDemoPlatformWindowing(this IServiceCollection services) {
        services.TryAddSingleton<INativeDisplayEnvironment, NativeDisplayEnvironment>();
        services.TryAddSingleton<INativeWindowPlatformSupport, NativeWindowPlatformSupport>();
        services.TryAddSingleton<IClipboardService>(static sp =>
            ((sp.GetRequiredService<INativeWindowPlatformSupport>().CurrentDisplayKind == NativeDisplayKind.Win32)
                ? new Win32ClipboardService()
                : new NullClipboardService()));
        services.TryAddSingleton<INativeImageCaptureService>(static sp =>
            ((sp.GetRequiredService<INativeWindowPlatformSupport>().CurrentDisplayKind == NativeDisplayKind.Win32)
                ? new Win32NativeImageCaptureService()
                : new NullNativeImageCaptureService()));
        services.TryAddSingleton<INativeSurfaceFactory, ConfiguredNativeSurfaceFactory>();
        services.TryAddSingleton<INativeWindowFactory, NativeWindowFactory>();

        return services;
    }

    /// <summary>Puck.Vulkan + Puck.Shaders: the native APIs, factories, and shader loader the renderer
    /// and the showcase scene compose over. Split into focused steps so each stays loosely coupled.</summary>
    public static IServiceCollection AddDemoVulkan(this IServiceCollection services) {
        return services
            .AddDemoVulkanNativeApis()
            .AddDemoVulkanFactories();
    }

    /// <summary>Puck.Vulkan: one native API per Vulkan capability the renderer and scene use.</summary>
    public static IServiceCollection AddDemoVulkanNativeApis(this IServiceCollection services) {
        services.TryAddSingleton<IVulkanCommandBufferRecordingApi>(static _ => new VulkanNativeCommandBufferRecordingApi());
        services.TryAddSingleton<IVulkanCommandResourcesApi>(static _ => new VulkanNativeCommandResourcesApi());
        services.TryAddSingleton<IVulkanDescriptorApi>(static _ => new VulkanNativeDescriptorApi());
        services.TryAddSingleton<IVulkanFramebufferSetApi>(static _ => new VulkanNativeFramebufferSetApi());
        services.TryAddSingleton<IVulkanFramePresentationApi>(static _ => new VulkanNativeFramePresentationApi());
        services.TryAddSingleton<IVulkanFrameSynchronizationApi>(static _ => new VulkanNativeFrameSynchronizationApi());
        services.TryAddSingleton<IVulkanGraphicsPipelineApi>(static _ => new VulkanNativeGraphicsPipelineApi());
        services.TryAddSingleton<IVulkanInstanceApi>(static _ => new VulkanNativeInstanceApi());
        services.TryAddSingleton<IVulkanLogicalDeviceApi>(static _ => new VulkanNativeLogicalDeviceApi());
        services.TryAddSingleton<IVulkanOffscreenImageApi>(static _ => new VulkanNativeOffscreenImageApi());
        services.TryAddSingleton<IVulkanPhysicalDeviceApi>(static _ => new VulkanNativePhysicalDeviceApi());
        services.TryAddSingleton<IVulkanRenderPassApi>(static _ => new VulkanNativeRenderPassApi());
        services.TryAddSingleton<IVulkanShaderModuleApi>(static _ => new VulkanNativeShaderModuleApi());
        services.TryAddSingleton<IVulkanStorageBufferApi>(static _ => new VulkanNativeStorageBufferApi());
        services.TryAddSingleton<IVulkanSurfaceApi>(static _ => new VulkanNativeSurfaceApi());
        services.TryAddSingleton<IVulkanSwapchainApi>(static _ => new VulkanNativeSwapchainApi());
        services.TryAddSingleton<IVulkanVertexBufferApi>(static _ => new VulkanNativeVertexBufferApi());

        return services;
    }

    /// <summary>Puck.Vulkan + Puck.Shaders: the factories, command-buffer recorder, and shader loader the
    /// renderer and scene compose over the native APIs.</summary>
    public static IServiceCollection AddDemoVulkanFactories(this IServiceCollection services) {
        services.TryAddSingleton<IVulkanInstanceFactory>(static sp => new VulkanInstanceFactory(sp.GetRequiredService<IVulkanInstanceApi>()));
        services.TryAddSingleton<IVulkanSurfaceFactory>(static sp => new VulkanSurfaceFactory(sp.GetRequiredService<IVulkanSurfaceApi>()));
        services.TryAddSingleton<IVulkanPhysicalDeviceSelector>(static sp => new VulkanPhysicalDeviceSelector(sp.GetRequiredService<IVulkanPhysicalDeviceApi>()));
        services.TryAddSingleton<IVulkanLogicalDeviceFactory>(static sp =>
            new VulkanLogicalDeviceFactory(
                sp.GetRequiredService<IVulkanLogicalDeviceApi>(),
                sp.GetRequiredService<IVulkanPhysicalDeviceApi>()
            ));
        services.TryAddSingleton<IVulkanSwapchainSupportApi>(static sp => new VulkanSwapchainSupportApi(sp.GetRequiredService<IVulkanPhysicalDeviceApi>()));
        services.TryAddSingleton<IVulkanSwapchainFactory>(static sp => new VulkanSwapchainFactory(sp.GetRequiredService<IVulkanSwapchainApi>()));
        services.TryAddSingleton<IVulkanRenderPassFactory>(static sp => new VulkanRenderPassFactory(sp.GetRequiredService<IVulkanRenderPassApi>()));
        services.TryAddSingleton<IVulkanFramebufferSetFactory>(static sp => new VulkanFramebufferSetFactory(sp.GetRequiredService<IVulkanFramebufferSetApi>()));
        services.TryAddSingleton<IVulkanCommandResourcesFactory>(static sp => new VulkanCommandResourcesFactory(sp.GetRequiredService<IVulkanCommandResourcesApi>()));
        services.TryAddSingleton<IVulkanFrameSynchronizationFactory>(static sp => new VulkanFrameSynchronizationFactory(sp.GetRequiredService<IVulkanFrameSynchronizationApi>()));
        services.TryAddSingleton<IVulkanFramePresenter>(static sp =>
            new VulkanFramePresenter(
                sp.GetRequiredService<IVulkanFramePresentationApi>(),
                sp.GetRequiredService<IVulkanFrameSynchronizationApi>()
            ));
        services.TryAddSingleton<IVulkanGraphicsPipelineFactory>(static sp => new VulkanGraphicsPipelineFactory(sp.GetRequiredService<IVulkanGraphicsPipelineApi>()));
        services.TryAddSingleton<IVulkanShaderModuleFactory>(static sp => new VulkanShaderModuleFactory(sp.GetRequiredService<IVulkanShaderModuleApi>()));
        services.TryAddSingleton<IVulkanStorageBufferFactory>(static sp => new VulkanStorageBufferFactory(sp.GetRequiredService<IVulkanStorageBufferApi>()));
        services.TryAddSingleton<IVulkanVertexBufferFactory>(static sp => new VulkanVertexBufferFactory(sp.GetRequiredService<IVulkanVertexBufferApi>()));

        // The renderer's command-buffer recorder, the content-addressed asset source, and the shader loader.
        services.TryAddSingleton<IVulkanCommandBufferRecorder>(static sp => new VulkanCommandBufferRecorder(sp.GetRequiredService<IVulkanCommandBufferRecordingApi>()));
        services.TryAddSingleton<IAssetSource, FileSystemAssetSource>();
        services.TryAddSingleton<IShaderModuleLoader, ShaderModuleLoader>();

        return services;
    }
}
