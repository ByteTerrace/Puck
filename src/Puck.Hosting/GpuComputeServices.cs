using Puck.Abstractions.Gpu;

namespace Puck.Hosting;

/// <summary>
/// The default backend-neutral <see cref="IGpuComputeServices"/>: a record that composes the nine granular compute
/// factories/services a backend registers. It is backend-agnostic — it only holds the neutral interfaces — so a
/// single registration (<c>AddSingleton&lt;IGpuComputeServices, GpuComputeServices&gt;()</c>) works for any backend
/// whose granular services are registered, and the dependency-injection container fills the constructor.
/// </summary>
public sealed record GpuComputeServices(
    IGpuCommandPoolFactory CommandPoolFactory,
    IGpuPipelineFactory PipelineFactory,
    IGpuRecorder Recorder,
    IGpuBindings Bindings,
    IGpuQueueSubmitter QueueSubmitter,
    IGpuShaderModuleFactory ShaderModuleFactory,
    IGpuBufferFactory BufferFactory,
    IGpuImageFactory ImageFactory,
    IGpuSurfaceTransferFactory SurfaceTransferFactory
) : IGpuComputeServices;
