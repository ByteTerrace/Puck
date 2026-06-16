using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Factories;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Recursive.Nodes;
using Puck.Recursive.Scene;
using Puck.SdfVm;
using Puck.SdfVm.Nodes;
using Puck.SdfVm.Rendering;
using Puck.Shaders;
using Puck.Vulkan.Interfaces;

namespace Puck.Recursive;

/// <summary>Registers the recursive showcase's <em>primary engine</em> — the cross-backend node tree and
/// the Direct3D 12 device it publishes — over the Vulkan stack the <see cref="Puck.Launcher"/> terminal
/// provides. It owns no window/swapchain/compositor/pump; those come from
/// <see cref="Puck.Launcher.LauncherServiceRegistration.AddLauncherTerminal"/>, composed by
/// <see cref="RecursiveShowcaseHost.AddRecursiveShowcase"/>.</summary>
internal static class RecursiveShowcaseRegistration {
    /// <summary>The recursive node tree (selected by <paramref name="tree"/>) and the root
    /// <see cref="IHostContext"/> it resolves its backends through. <c>default</c> is a Vulkan SDF engine
    /// hosting a DirectX textured child; <c>vdv</c> nests Vulkan → DirectX host → Vulkan; <c>dvd</c> nests
    /// DirectX host → Vulkan → DirectX. The host context publishes the Vulkan device (from the terminal) and,
    /// on Windows, the Direct3D 12 device, plus the terminal-control baton — so every node resolves whichever
    /// backend it was built against, and the root engine holds the baton.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="tree">The tree to build: <c>default</c>, <c>vdv</c>, or <c>dvd</c>.</param>
    public static IServiceCollection AddRecursiveNodeTree(this IServiceCollection services, string tree) {
        services.AddSingleton<ColorFieldNode>();
        services.AddSingleton<SdfScene>();

        if (OperatingSystem.IsWindowsVersionAtLeast(
            10,
            0,
            10240
        )) {
            AddRecursiveDirectX(services: services);
        }

        services.AddSingleton<IRenderNode>(implementationFactory: sp => {
            if (OperatingSystem.IsWindowsVersionAtLeast(
                10,
                0,
                10240
            )) {
                return tree switch {
                    "dvd" => BuildDirectXVulkanDirectX(sp: sp),
                    "vdv" => BuildVulkanDirectXVulkan(sp: sp),
                    _ => BuildDefaultTree(sp: sp),
                };
            }

            return BuildDefaultTree(sp: sp);
        });

        // The root host context overrides the terminal's default (Vulkan device + the held baton/input-focus)
        // to ALSO publish the Direct3D 12 device for the DirectX subtree. The held capabilities — the baton
        // and input focus — come from the terminal and are re-published so the root engine holds them.
        services.AddSingleton<IHostContext>(implementationFactory: static sp => {
            var capabilities = new Dictionary<Type, object> {
                [typeof(IVulkanDeviceContext)] = sp.GetRequiredService<IVulkanDeviceContext>(),
            };

            if (OperatingSystem.IsWindowsVersionAtLeast(
                10,
                0,
                10240
            )) {
                capabilities[typeof(IDirectXDeviceContext)] = sp.GetRequiredService<DirectXDeviceContext>();
            }

            return new HostContext(
                capabilities: capabilities,
                heldCapabilities: new Dictionary<Type, object> {
                    [typeof(IInputFocus)] = sp.GetRequiredService<IInputFocus>(),
                    [typeof(ITerminalControl)] = sp.GetRequiredService<ITerminalControl>(),
                }
            );
        });

        return services;
    }

    // default: a Vulkan SDF engine (the blit-root) hosting one child in slot 3 — a DirectX textured node on
    // Windows, the Vulkan color-field node elsewhere.
    private static IRenderNode BuildDefaultTree(IServiceProvider sp) {
        var children = new Dictionary<int, IRenderNode>();

        if (OperatingSystem.IsWindowsVersionAtLeast(
            10,
            0,
            10240
        )) {
            children[3] = sp.GetRequiredService<DirectXColorNode>();
        } else {
            children[3] = sp.GetRequiredService<ColorFieldNode>();
        }

        return BuildSdfEngine(
            children: children,
            frameSource: sp.GetRequiredService<SdfScene>(),
            name: "sdf-engine",
            produceCpuPixels: false,
            sp: sp
        );
    }
    // vdv: a Vulkan SDF engine (blit-root) hosts a DirectX host in slot 3, which in turn hosts a Vulkan SDF
    // engine — Vulkan → DirectX → Vulkan, each boundary crossed by the CPU-pixel surface.
    [SupportedOSPlatform("windows10.0.10240")]
    private static IRenderNode BuildVulkanDirectXVulkan(IServiceProvider sp) {
        var leaf = BuildSdfEngine(
            children: null,
            frameSource: new SdfScene(),
            name: "sdf-leaf",
            produceCpuPixels: true,
            sp: sp
        );
        var directXHost = BuildDirectXEngine(
            child: leaf,
            name: "directx-host",
            sp: sp
        );

        return BuildSdfEngine(
            children: new Dictionary<int, IRenderNode> { [3] = directXHost, },
            frameSource: sp.GetRequiredService<SdfScene>(),
            name: "sdf-engine",
            produceCpuPixels: false,
            sp: sp
        );
    }
    // dvd: a DirectX host (root) hosts a Vulkan SDF engine, which in turn hosts a DirectX textured node in
    // slot 3 — DirectX → Vulkan → DirectX. The root's CPU-pixel output is uploaded by the surface compositor.
    [SupportedOSPlatform("windows10.0.10240")]
    private static IRenderNode BuildDirectXVulkanDirectX(IServiceProvider sp) {
        var middle = BuildSdfEngine(
            children: new Dictionary<int, IRenderNode> { [3] = sp.GetRequiredService<DirectXColorNode>(), },
            frameSource: sp.GetRequiredService<SdfScene>(),
            name: "sdf-middle",
            produceCpuPixels: true,
            sp: sp
        );

        return BuildDirectXEngine(
            child: middle,
            name: "directx-root",
            sp: sp
        );
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private static DirectXEngineNode BuildDirectXEngine(IServiceProvider sp, IRenderNode child, string name) {
        return new DirectXEngineNode(
            child: child,
            name: name,
            pipelineFactory: sp.GetRequiredService<IDirectXPipelineFactory>(),
            shaderCompiler: sp.GetRequiredService<IDirectXShaderCompilerApi>(),
            vertexBufferFactory: sp.GetRequiredService<IDirectXVertexBufferFactory>()
        );
    }
    private static SdfEngineNode BuildSdfEngine(IServiceProvider sp, ISdfFrameSource frameSource, IReadOnlyDictionary<int, IRenderNode>? children, bool produceCpuPixels, string name) {
        return SdfEngineNode.Create(
            children: children,
            commandBufferRecordingApi: sp.GetRequiredService<IVulkanCommandBufferRecordingApi>(),
            commandResourcesFactory: sp.GetRequiredService<IVulkanCommandResourcesFactory>(),
            descriptorApi: sp.GetRequiredService<IVulkanDescriptorApi>(),
            framebufferSetApi: sp.GetRequiredService<IVulkanFramebufferSetApi>(),
            frameReadbackApi: sp.GetRequiredService<IVulkanFrameReadbackApi>(),
            frameSource: frameSource,
            graphicsPipelineFactory: sp.GetRequiredService<IVulkanGraphicsPipelineFactory>(),
            name: name,
            offscreenImageApi: sp.GetRequiredService<IVulkanOffscreenImageApi>(),
            options: sp.GetRequiredService<SdfViewRendererOptions>(),
            produceCpuPixels: produceCpuPixels,
            renderPassApi: sp.GetRequiredService<IVulkanRenderPassApi>(),
            shaderModuleFactory: sp.GetRequiredService<IVulkanShaderModuleFactory>(),
            shaderModuleLoader: sp.GetRequiredService<IShaderModuleLoader>(),
            storageBufferFactory: sp.GetRequiredService<IVulkanStorageBufferFactory>(),
            vertexBufferFactory: sp.GetRequiredService<IVulkanVertexBufferFactory>()
        );
    }

    /// <summary>Puck.DirectX: the shared Direct3D 12 device + the shader-compiler/pipeline/vertex-buffer stack
    /// the producer nodes compose — the peer of the terminal's Vulkan stack. The device is created on the SAME
    /// physical adapter as the terminal's Vulkan device (matched by LUID) so the backends can share GPU
    /// resources; the LUID is resolved lazily from the Vulkan device context the terminal published.</summary>
    [SupportedOSPlatform("windows10.0.10240")]
    private static void AddRecursiveDirectX(IServiceCollection services) {
        services.AddSingleton<IDirectXDeviceApi, DirectXNativeDeviceApi>();
        services.AddSingleton(implementationFactory: static sp => new DirectXDeviceContext(
            adapterLuidProvider: () => {
                var deviceContext = sp.GetRequiredService<IVulkanDeviceContext>();
                var luid = sp.GetRequiredService<IVulkanPhysicalDeviceApi>().GetDeviceLuid(
                    instanceHandle: deviceContext.Instance.Handle,
                    physicalDeviceHandle: deviceContext.PhysicalDevice.Handle
                );

                sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(categoryName: "Puck.Demo.Recursive.DirectX")
                    .LogInformation(
                        "Creating the Direct3D 12 device on Vulkan adapter LUID 0x{AdapterLuid:X16}.",
                        luid
                    );

                return luid;
            },
            deviceApi: sp.GetRequiredService<IDirectXDeviceApi>(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        ));
        services.AddSingleton<IDirectXShaderCompilerApi, DirectXNativeShaderCompilerApi>();
        services.AddSingleton<IDirectXPipelineApi, DirectXNativePipelineApi>();
        services.AddSingleton<IDirectXPipelineFactory, DirectXPipelineFactory>();
        services.AddSingleton<IDirectXVertexBufferApi, DirectXNativeVertexBufferApi>();
        services.AddSingleton<IDirectXVertexBufferFactory, DirectXVertexBufferFactory>();
        services.AddSingleton<DirectXColorNode>();
    }
}
