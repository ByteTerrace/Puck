using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX.Apis;
using Puck.DirectX.Factories;
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
    /// <summary>
    /// Registers the Direct3D 12 backend: native APIs, the vertex-buffer factory chain, all
    /// <c>IGpu*</c> adapters, and the <see cref="IDirectXDeviceContext"/> /
    /// <see cref="IGpuDeviceContext"/> capability contributions.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddDirectXPresenter(this IServiceCollection services) {
        services.AddDirectXComputeApis();

        services.TryAddSingleton<DirectXDeviceContext>();
        services.TryAddSingleton<IDirectXDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<DirectXDeviceContext>());
        services.TryAddSingleton<IGpuDeviceContext>(implementationFactory: static sp => sp.GetRequiredService<DirectXDeviceContext>());

        services.TryAddSingleton<IDirectXVertexBufferApi>(implementationFactory: static _ => new DirectXNativeVertexBufferApi());
        services.TryAddSingleton<IDirectXVertexBufferFactory>(implementationFactory: static sp => new DirectXVertexBufferFactory(
            vertexBufferApi: sp.GetRequiredService<IDirectXVertexBufferApi>()
        ));
        services.TryAddSingleton<IDirectXShaderCompilerApi>(implementationFactory: static _ => new DirectXNativeShaderCompilerApi());

        services.TryAddSingleton<IGpuCommandRecorder>(implementationFactory: static _ => new DirectXGpuCommandRecorder());
        services.TryAddSingleton<IGpuDescriptorAllocator>(implementationFactory: static _ => new DirectXGpuDescriptorAllocator());
        services.TryAddSingleton<IGpuPipelineFactory>(implementationFactory: static _ => new DirectXGpuPipelineFactory());
        services.TryAddSingleton<IGpuQueueSubmitter>(implementationFactory: static _ => new DirectXGpuQueueSubmitter());
        services.TryAddSingleton<IGpuRenderTargetFactory>(implementationFactory: static _ => new DirectXGpuRenderTargetFactory());
        services.TryAddSingleton<IGpuShaderModuleFactory>(implementationFactory: static _ => new DirectXGpuShaderModuleFactory());
        services.TryAddSingleton<IGpuStorageBufferFactory>(implementationFactory: static _ => new DirectXGpuStorageBufferFactory());
        services.TryAddSingleton<IGpuSurfaceTransferFactory>(implementationFactory: static _ => new DirectXGpuSurfaceTransferFactory());
        // Optional capability: Direct3D 12 can export a shared texture for another backend on the same adapter to
        // import zero-copy. A host resolves this when present and falls back to the CPU-pixel transport otherwise.
        services.TryAddSingleton<IGpuSurfaceExportFactory>(implementationFactory: static _ => new DirectXGpuSurfaceExportFactory());
        services.TryAddSingleton<IGpuVertexBufferFactory>(implementationFactory: static sp => new DirectXGpuVertexBufferFactory(
            vertexBufferFactory: sp.GetRequiredService<IDirectXVertexBufferFactory>()
        ));

        // Neutral presentation preferences (present mode + surface format); a consumer may register its own
        // before calling this to override the defaults (Vsync + R8G8B8A8).
        services.TryAddSingleton(instance: new PresentationOptions());
        services.TryAddSingleton<IDirectXCommandListRecorder>(implementationFactory: static _ => new DirectXCommandListRecorder());
        services.TryAddSingleton<DirectXSurfaceCompositor>(implementationFactory: static sp => new DirectXSurfaceCompositor(
            commandListRecorder: sp.GetRequiredService<IDirectXCommandListRecorder>(),
            presentationOptions: sp.GetRequiredService<PresentationOptions>(),
            shaderCompiler: sp.GetRequiredService<IDirectXShaderCompilerApi>()
        ));
        services.TryAddSingleton(implementationFactory: static sp => new DirectXSurfacePresenter(
            compositor: sp.GetRequiredService<DirectXSurfaceCompositor>(),
            deviceContext: sp.GetRequiredService<DirectXDeviceContext>()
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
