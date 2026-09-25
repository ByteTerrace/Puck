using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX.Apis;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.Hosting;

namespace Puck.DirectX.Presentation;

/// <summary>
/// Composes the Direct3D 12 backend: the device context, all <c>IGpu*</c> adapters, and their
/// <see cref="HostCapabilityContribution"/>s. Pair it with a host that aggregates contributions and drives the
/// run loop. The device is created lazily — for cross-backend resource sharing, register your own
/// <see cref="DirectXDeviceContext"/> with a LUID provider before calling this.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static class DirectXPresenterServiceRegistration {
    // The key the backend's GpuPipelineCacheWork is registered under, and the name its counts report.
    private const string PipelineCacheBackend = "directx";

    /// <summary>
    /// Registers the Direct3D 12 backend: native APIs, the vertex-buffer factory chain, all
    /// <c>IGpu*</c> adapters, and the <see cref="IDirectXDeviceContext"/> /
    /// <see cref="IGpuDeviceContext"/> capability contributions.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddDirectXPresenter(this IServiceCollection services) {
        services.AddDirectXComputeApis();

        services.AddGpuPipelineCacheWork(backend: PipelineCacheBackend);
        // The device is created lazily on the default adapter; its pipeline library lives under the host's
        // GpuPipelineCacheStore when one is registered, and in memory otherwise. The debug layer follows the host's
        // GpuDeviceOptions, and is off when none is registered.
        services.TryAddSingleton(implementationFactory: static sp => new DirectXDeviceContext(
            adapterLuid: 0,
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110,
            pipelineCacheStore: sp.GetService<GpuPipelineCacheStore>(),
            pipelineCacheWork: sp.GetRequiredKeyedService<GpuPipelineCacheWork>(serviceKey: PipelineCacheBackend)
        ) {
            EnableDebugLayer = (sp.GetService<GpuDeviceOptions>()?.DebugLayers ?? false),
        });
        services.TryAddSingleton<IDirectXDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<DirectXDeviceContext>());
        services.TryAddSingleton<IGpuDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<DirectXDeviceContext>());


        services.TryAddSingleton<IGpuRecorder>(implementationFactory: static sp => new DirectXGpuRecorder(deviceContext: sp.GetRequiredService<DirectXDeviceContext>()));
        services.TryAddSingleton<IGpuDescriptorAllocator>(implementationFactory: static _ => new DirectXGpuDescriptorAllocator());
        services.TryAddSingleton<IGpuPipelineFactory>(implementationFactory: static _ => new DirectXGpuPipelineFactory());
        services.TryAddSingleton<IGpuQueueSubmitter>(implementationFactory: static _ => new DirectXGpuQueueSubmitter());
        services.TryAddSingleton<IGpuRenderPassFactory>(implementationFactory: static _ => new DirectXGpuRenderPassFactory());
        services.TryAddSingleton<IGpuShaderModuleFactory>(implementationFactory: static _ => new DirectXGpuShaderModuleFactory());
        services.TryAddSingleton<IGpuStorageBufferFactory>(implementationFactory: static _ => new DirectXGpuStorageBufferFactory());
        services.TryAddSingleton<IGpuSurfaceTransferFactory>(implementationFactory: static _ => new DirectXGpuSurfaceTransferFactory());
        // Optional capability: Direct3D 12 can export a shared texture for another backend on the same adapter to
        // import zero-copy. A host resolves this when present and falls back to the CPU-pixel transport otherwise.
        services.TryAddSingleton<IGpuSurfaceExportFactory>(implementationFactory: static _ => new DirectXGpuSurfaceExportFactory());
        services.TryAddSingleton<IGpuGeometryBufferFactory>(implementationFactory: static _ => new DirectXGpuGeometryBufferFactory());

        // Neutral presentation preferences (present mode + surface format); a consumer may register its own
        // before calling this to override the defaults (Vsync + R8G8B8A8).
        services.TryAddSingleton(instance: new PresentationOptions());
        services.TryAddSingleton<IDirectXCommandListRecorder>(implementationFactory: static _ => new DirectXCommandListRecorder());
        services.TryAddSingleton<DirectXSurfaceCompositor>(implementationFactory: static sp => new DirectXSurfaceCompositor(
            commandListRecorder: sp.GetRequiredService<IDirectXCommandListRecorder>(),
            presentationOptions: sp.GetRequiredService<PresentationOptions>(),
            shaderDirectory: Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: "Assets",
                path3: "Shaders"
            ),
            surfaceTransferFactory: sp.GetRequiredService<IGpuSurfaceTransferFactory>()
        ));
        services.TryAddSingleton(implementationFactory: static sp => new DirectXSurfacePresenter(
            compositor: sp.GetRequiredService<DirectXSurfaceCompositor>(),
            deviceContext: sp.GetRequiredService<DirectXDeviceContext>(),
            surfaceTransferFactory: sp.GetRequiredService<IGpuSurfaceTransferFactory>()
        ));
        services.TryAddSingleton<ISurfacePresenter>(implementationFactory: static sp => sp.GetRequiredService<DirectXSurfacePresenter>());

        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(IDirectXDeviceContext),
            Instance: sp.GetRequiredService<DirectXDeviceContext>(),
            IsHeld: false
        ));
        services.AddSingleton(implementationFactory: static sp => new HostCapabilityContribution(
            CapabilityType: typeof(IGpuDeviceContext),
            Instance: sp.GetRequiredService<DirectXDeviceContext>(),
            IsHeld: false
        ));

        return services;
    }
}
