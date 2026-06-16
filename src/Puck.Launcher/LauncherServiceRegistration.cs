using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Assets;
using Puck.Commands;
using Puck.Compositing;
using Puck.Hosting;
using Puck.Launcher.Commands;
using Puck.Launcher.Vulkan;
using Puck.Platform;
using Puck.Platform.Windows;
using Puck.Shaders;
using Puck.Vulkan;
using Puck.Vulkan.Apis;
using Puck.Vulkan.Factories;
using Puck.Vulkan.Interfaces;

namespace Puck.Launcher;

/// <summary>Explicit, composable service registration for the launcher: every dependency is wired by hand
/// against the Puck.* libraries — no engine-wide bring-up helper. Grouped by concern so the composition
/// root stays readable.</summary>
public static class LauncherServiceRegistration {
    /// <summary>The engine-agnostic terminal: the native window + Vulkan swapchain owner, its surface-blit
    /// compositor, the terminal-control <em>baton</em>, the command pump, and the run loop. It carries no
    /// engine type — it drives whichever root <see cref="IRenderNode"/> the developer registers and blits
    /// its one surface to the swapchain. The developer supplies (in their composition root): the root
    /// <see cref="IRenderNode"/>, a <see cref="BindingCommandSource"/> (key bindings) and an
    /// <c>InputPacket → InputSignal?</c> adapter for the pump, and any engine-specific
    /// <see cref="ICommandModule"/>s. The root <see cref="IHostContext"/> is registered with
    /// <c>TryAddSingleton</c> (publishing the Vulkan device + baton), so a developer whose root needs extra
    /// capabilities can register their own instead.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddLauncherTerminal(this IServiceCollection services) {
        var blitShaderDirectory = Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders"
        );

        services
            .AddLauncherPlatformWindowing()
            .AddLauncherVulkan();

        // The terminal's device/swapchain owner + its surface-blit compositor.
        services.TryAddSingleton(new VulkanRendererOptions {
            ApplicationName = "Puck.Launcher",
        });
        services.TryAddSingleton<VulkanRenderer>();
        // Publish the device context as a public interface so a consumer can compose its own root
        // IHostContext (e.g. adding a DirectX device) without referencing the internal renderer type.
        services.TryAddSingleton<IVulkanDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<VulkanRenderer>());
        services.TryAddSingleton<VulkanQueueSubmitter>();
        services.TryAddSingleton(implementationFactory: sp => new SurfaceCompositor(
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            descriptorApi: sp.GetRequiredService<IVulkanDescriptorApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            graphicsPipelineFactory: sp.GetRequiredService<IVulkanGraphicsPipelineFactory>(),
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>(),
            queueSubmitter: sp.GetRequiredService<VulkanQueueSubmitter>(),
            renderer: sp.GetRequiredService<VulkanRenderer>(),
            shaderDirectory: blitShaderDirectory,
            shaderModuleFactory: sp.GetRequiredService<IVulkanShaderModuleFactory>(),
            shaderModuleLoader: sp.GetRequiredService<IShaderModuleLoader>(),
            storageBufferFactory: sp.GetRequiredService<IVulkanStorageBufferFactory>(),
            vertexBufferFactory: sp.GetRequiredService<IVulkanVertexBufferFactory>()
        ));

        // The terminal's held capabilities, both backed by the one TerminalControl: the baton (terminal
        // ownership/lifecycle) and input focus (the right to receive input). They are published as HELD on
        // the root host context, so the root engine holds them via HoldsCapability and hosted children do
        // not — the capability-permission system. The window loop drains exit + routes input through them.
        services.TryAddSingleton<LauncherOptions>();
        services.TryAddSingleton<TerminalControl>();
        services.TryAddSingleton<ITerminalControl>(implementationFactory: static sp => sp.GetRequiredService<TerminalControl>());
        services.TryAddSingleton<IInputFocus>(implementationFactory: static sp => sp.GetRequiredService<TerminalControl>());
        services.TryAddSingleton<IHostContext>(implementationFactory: static sp => new HostContext(
            capabilities: new Dictionary<Type, object> {
                [typeof(IVulkanDeviceContext)] = sp.GetRequiredService<VulkanRenderer>(),
            },
            heldCapabilities: new Dictionary<Type, object> {
                [typeof(IInputFocus)] = sp.GetRequiredService<IInputFocus>(),
                [typeof(ITerminalControl)] = sp.GetRequiredService<ITerminalControl>(),
            }
        ));

        // Command pump: the registry, the stdin text source (results echoed to stdout so scripted runs are
        // assertable), and the per-frame shell. The keyboard binding source and the input adapter are
        // developer-supplied (they encode the engine's controls), so the pump stays engine-agnostic.
        services.TryAddSingleton<CommandRegistry>();
        services.TryAddSingleton(implementationFactory: static provider => new TextCommandSource(
            onResult: static (line, result) => {
                if (!string.IsNullOrEmpty(value: result.Output)) {
                    Console.Out.WriteLine(value: result.Output);
                }
            },
            registry: provider.GetRequiredService<CommandRegistry>()
        ));
        services.TryAddSingleton(implementationFactory: static sp => new CommandShell(
            inputAdapter: sp.GetRequiredService<Func<InputPacket, InputSignal?>>(),
            keyboardSource: sp.GetRequiredService<BindingCommandSource>(),
            registry: sp.GetRequiredService<CommandRegistry>(),
            standardInputSource: sp.GetRequiredService<TextCommandSource>()
        ));

        // The terminal's own command surface (just `quit`, which drives the baton) and the two hosted
        // services: the window/run loop and the stdin reader.
        services.AddSingleton<ICommandModule, TerminalCommandModule>();
        services.AddHostedService<LauncherWindowHostedService>();
        services.AddHostedService(implementationFactory: static sp => new StandardInputReaderService(
            source: sp.GetRequiredService<TextCommandSource>(),
            threadName: "Puck.Launcher Stdin Reader"
        ));

        return services;
    }

    /// <summary>Platform: the native window + surface stack (à la carte; no engine host).</summary>
    public static IServiceCollection AddLauncherPlatformWindowing(this IServiceCollection services) {
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

    /// <summary>Puck.Vulkan + Puck.Shaders: the native APIs, factories, and shader loader the terminal and
    /// the engine node compose over.</summary>
    public static IServiceCollection AddLauncherVulkan(this IServiceCollection services) {
        return services
            .AddLauncherVulkanNativeApis()
            .AddLauncherVulkanFactories();
    }

    /// <summary>Puck.Vulkan: one native API per Vulkan capability the terminal and engine use.</summary>
    public static IServiceCollection AddLauncherVulkanNativeApis(this IServiceCollection services) {
        services.TryAddSingleton<IVulkanCommandBufferRecordingApi>(static _ => new VulkanNativeCommandBufferRecordingApi());
        services.TryAddSingleton<IVulkanCommandResourcesApi>(static _ => new VulkanNativeCommandResourcesApi());
        services.TryAddSingleton<IVulkanDescriptorApi>(static _ => new VulkanNativeDescriptorApi());
        services.TryAddSingleton<IVulkanFramebufferSetApi>(static _ => new VulkanNativeFramebufferSetApi());
        services.TryAddSingleton<IVulkanFrameReadbackApi>(static _ => new VulkanNativeFrameReadbackApi());
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
    /// terminal and engine compose over the native APIs.</summary>
    public static IServiceCollection AddLauncherVulkanFactories(this IServiceCollection services) {
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
