using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.DirectX.Interop;

namespace Puck.DirectX.Presentation;

/// <summary>
/// Registers the backend-neutral compute adapters (<see cref="IGpuComputePipelineFactory"/>,
/// <see cref="IGpuImageFactory"/>, <see cref="IGpuComputeCommandPoolFactory"/>, and
/// <see cref="IGpuComputeRecorder"/>) that wrap the Direct3D 12 compute path. The Direct3D 12 peer of
/// <c>VulkanComputeServiceRegistration</c>; kept apart from
/// <see cref="DirectXPresenterServiceRegistration"/> so the composition root's type coupling stays bounded.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static class DirectXComputeServiceRegistration {
    /// <summary>Registers the neutral compute pipeline, image, command pool, and recorder adapters.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDirectXComputeApis(this IServiceCollection services) {
        services.TryAddSingleton<IGpuComputeCommandPoolFactory>(implementationFactory: static _ => new DirectXGpuComputeCommandPoolFactory());
        services.TryAddSingleton<IGpuComputePipelineFactory>(implementationFactory: static _ => new DirectXGpuComputePipelineFactory());
        services.TryAddSingleton<IGpuComputeRecorder>(implementationFactory: static sp => new DirectXGpuComputeRecorder(deviceContext: sp.GetRequiredService<DirectXDeviceContext>()));
        services.TryAddSingleton<IGpuImageFactory>(implementationFactory: static _ => new DirectXGpuImageFactory());
        // The compute-services bundle composes the nine granular compute factories/services a compute node drives
        // (the four above plus the descriptor allocator, queue submitter, shader-module, storage-buffer, and
        // surface-transfer factories the presenter registers). Resolved lazily, so order with those is immaterial.
        services.TryAddSingleton<IGpuComputeServices, GpuComputeServices>();

        return services;
    }
}
