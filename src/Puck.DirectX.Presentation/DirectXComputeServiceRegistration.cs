using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
namespace Puck.DirectX.Presentation;

/// <summary>
/// Registers the backend-neutral compute adapters (<see cref="IGpuComputePipelineFactory"/>,
/// <see cref="IGpuStorageImageFactory"/>, <see cref="IGpuComputeCommandPoolFactory"/>, and
/// <see cref="IGpuComputeRecorder"/>) that wrap the Direct3D 12 compute path. The Direct3D 12 peer of
/// <c>VulkanComputeServiceRegistration</c>; kept apart from
/// <see cref="DirectXPresenterServiceRegistration"/> so the composition root's type coupling stays bounded.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static class DirectXComputeServiceRegistration {
    /// <summary>Registers the neutral compute pipeline, storage image, command pool, and recorder adapters.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDirectXComputeApis(this IServiceCollection services) {
        services.TryAddSingleton<IGpuComputeCommandPoolFactory>(static _ => new DirectXGpuComputeCommandPoolFactory());
        services.TryAddSingleton<IGpuComputePipelineFactory>(static _ => new DirectXGpuComputePipelineFactory());
        services.TryAddSingleton<IGpuComputeRecorder>(static _ => new DirectXGpuComputeRecorder());
        services.TryAddSingleton<IGpuStorageImageFactory>(static _ => new DirectXGpuStorageImageFactory());
        // GPU performance counters: the timestamp query-pool factory + recorder (the neutral timing seam, D3D12 peer).
        services.TryAddSingleton<IGpuTimingPoolFactory>(static _ => new DirectXGpuTimingPoolFactory());
        services.TryAddSingleton<IGpuTimingRecorder>(static _ => new DirectXGpuTimingRecorder());
        // The compute-services bundle composes the nine granular compute factories/services a compute node drives
        // (the four above plus the descriptor allocator, queue submitter, shader-module, storage-buffer, and
        // surface-transfer factories the presenter registers). Resolved lazily, so order with those is immaterial.
        services.TryAddSingleton<IGpuComputeServices, GpuComputeServices>();

        return services;
    }
}
