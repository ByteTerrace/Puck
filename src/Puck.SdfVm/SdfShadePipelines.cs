using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

// One program set's specialized views and glass pipelines, fully built before the residency can publish it, each a lease
// on the composition's pass-pipeline cache under its kernel's own description (so its sets bind as the fixed kernel's
// do) and a name of its own (sdf-world-views.shade-<set>) that captures and pipeline statistics tell apart. The residency
// owns the leases. A recording pins the set it records with (SdfShadeSlots.RecordPipeline) and the pin moves to the
// submission, which drops it only on a proof of completion, an abandonment before submission, or a device loss; the
// residency frees a retired set only once its pins reach zero, and frees every set at device teardown and after its
// final idle drain.
internal sealed class SdfShadePipelines : IDisposable {
    private readonly Dictionary<SdfKernel, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>> m_leases;
    private int m_pins;

    private SdfShadePipelines(SdfShadeKernels request, Dictionary<SdfKernel, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>> leases) {
        Request = request;
        m_leases = leases;
    }

    internal SdfShadeKernels Request { get; }
    internal int Pins => Volatile.Read(location: ref m_pins);

    internal void Pin() => Interlocked.Increment(location: ref m_pins);
    internal void Unpin() => Interlocked.Decrement(location: ref m_pins);
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) => m_leases[kernel].Current!.Compute!;

    // Compiles, reflects and creates off the frame thread. A kernel that reads anything but this host's interface (a tree
    // edited since the running kernels were compiled) refuses the whole set, which then stays interpreted.
    internal static async Task<SdfShadePipelines> BuildAsync(SdfShadeKernels request, ShaderCompiler compiler,
        ShaderTarget target, string tree, GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        var bytecodes = await request.CompileAsync(cancellationToken: cancellationToken, compiler: compiler, target: target, tree: tree).ConfigureAwait(continueOnCapturedContext: false);
        var leases = new Dictionary<SdfKernel, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>>();

        try {
            using var reflector = new ShaderBytecodeReflector(toolchain: compiler.Toolchain);

            foreach (var (kernel, bytecode) in bytecodes) {
                cancellationToken.ThrowIfCancellationRequested();
                if (SdfKernelSet.InterfaceMismatch(kernel: kernel, reflected: reflector.Read(bytecode: bytecode.Span)) is { } mismatch) {
                    throw new InvalidDataException(message: $"The specialized '{SdfKernelSet.StemOf(kernel: kernel)}' does not read this host's interface: {mismatch}");
                }
                var description = SdfWorldTables.PipelineLayouts.Specs[(int)kernel].Description;

                leases.Add(key: kernel, value: cache.Acquire(device: device, key: GpuPassPipelineKey.OfCompute(
                    bytecode: bytecode,
                    description: description with { Name = $"{description.Name}.shade-{request.Identity[..12]}" }
                )));
            }
            await Task.WhenAll(tasks: leases.Values.Select(selector: lease => lease.WaitAsync(cancellationToken: cancellationToken))).ConfigureAwait(continueOnCapturedContext: false);
            cancellationToken.ThrowIfCancellationRequested();
            return new SdfShadePipelines(leases: leases, request: request);
        } catch {
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases: [.. leases.Values]);
            throw;
        }
    }

    public void Dispose() => GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases: [.. m_leases.Values]);
}
