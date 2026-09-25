using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.DirectX.Interop;
using Puck.Hosting;

namespace Puck.DirectX.Presentation;

/// <summary>
/// Registers the backend-neutral image and command-pool adapters (<see cref="IGpuImageFactory"/> and
/// <see cref="IGpuComputeCommandPoolFactory"/>) that wrap the Direct3D 12 compute path. The Direct3D 12 peer of
/// <c>VulkanComputeServiceRegistration</c>; kept apart from
/// <see cref="DirectXPresenterServiceRegistration"/> so the composition root's type coupling stays bounded.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static class DirectXComputeServiceRegistration {
    /// <summary>Registers the neutral image and command pool adapters, each bound to the backend's device context.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDirectXComputeApis(this IServiceCollection services) {
        services.TryAddSingleton<IGpuComputeCommandPoolFactory>(implementationFactory: static sp => new DirectXGpuComputeCommandPoolFactory(deviceContext: sp.GetRequiredService<DirectXDeviceContext>()));
        services.TryAddSingleton<IGpuImageFactory>(implementationFactory: static sp => new DirectXGpuImageFactory(deviceContext: sp.GetRequiredService<DirectXDeviceContext>()));
        // The compute-services bundle composes the nine granular services a compute node drives (the two above plus the
        // recorder, bindings, pipeline, queue submitter, shader-module, buffer and surface-transfer factories the
        // presenter registers). Resolved lazily, so order with those is immaterial.
        services.TryAddSingleton<IGpuComputeServices, GpuComputeServices>();

        return services;
    }
}
