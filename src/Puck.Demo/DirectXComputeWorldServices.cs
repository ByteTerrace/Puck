using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.DirectX.Presentation;

namespace Puck.Demo;

/// <summary>
/// Registers the Direct3D 12 neutral compute services a bespoke-device (off-host) compute world node drives: the nine
/// granular <c>IGpuCompute*</c>/factory adapters plus the <see cref="IGpuComputeServices"/> bundle that composes them.
/// Extracted from the three nodes that each built a fresh <see cref="ServiceCollection"/> for a standalone Direct3D 12
/// device — the compute-world host, its LUID-matched parity device, and the ray-query host — which were verbatim copies
/// differing only in what they ADD on top (timing counters / acceleration structures).
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal static class DirectXComputeWorldServices {
    /// <summary>Registers the shared standalone Direct3D 12 compute service bundle.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining (so a caller can add timing or acceleration services).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddDirectXComputeWorld(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        // Reuse the CANONICAL Direct3D 12 compute seam (compute pool/pipeline/recorder, storage image, timing counters,
        // and the IGpuComputeServices bundle) instead of re-listing it, so that bundle has exactly one definition.
        services.AddDirectXComputeApis();

        // Plus the factories the host PRESENTER would otherwise contribute — this is an off-host, presenter-less
        // collection, so they have no other source here.
        services.TryAddSingleton<IGpuDescriptorAllocator>(static _ => new DirectXGpuDescriptorAllocator());
        services.TryAddSingleton<IGpuQueueSubmitter>(static _ => new DirectXGpuQueueSubmitter());
        services.TryAddSingleton<IGpuShaderModuleFactory>(static _ => new DirectXGpuShaderModuleFactory());
        services.TryAddSingleton<IGpuStorageBufferFactory>(static _ => new DirectXGpuStorageBufferFactory());
        services.TryAddSingleton<IGpuSurfaceTransferFactory>(static _ => new DirectXGpuSurfaceTransferFactory());

        return services;
    }
}
