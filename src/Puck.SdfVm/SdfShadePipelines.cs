using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

// One immutable, fully built generation. Only the residency owns its native leases. Recordings pin the generation
// through SdfShadeSlots until completion; the residency retires an old generation only when Pins reaches zero.
internal sealed class SdfShadePipelines : IDisposable {
    private readonly Dictionary<SdfKernel, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>> m_leases;
    private int m_pins;

    private SdfShadePipelines(SdfShadeKernels request, Dictionary<SdfKernel, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>> leases) {
        Request = request;
        m_leases = leases;
    }
    internal SdfShadeKernels Request { get; }
    internal int Pins => Volatile.Read(ref m_pins);
    internal void Pin() => Interlocked.Increment(ref m_pins);
    internal void Unpin() => Interlocked.Decrement(ref m_pins);
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) => m_leases[kernel].Current!.Compute!;

    internal static async Task<SdfShadePipelines> BuildAsync(SdfShadeKernels request, ShaderCompiler compiler,
        ShaderTarget target, string tree, GpuPassPipelineCache cache, IGpuDeviceContext device, CancellationToken cancellationToken) {
        var bytecodes = await request.CompileAsync(compiler, target, tree, cancellationToken).ConfigureAwait(false);
        var leases = new Dictionary<SdfKernel, GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>>();
        try {
            using var reflector = new ShaderBytecodeReflector(compiler.Toolchain);
            foreach (var (kernel, bytecode) in bytecodes) {
                cancellationToken.ThrowIfCancellationRequested();
                if (SdfKernelSet.InterfaceMismatch(kernel, reflector.Read(bytecode.Span)) is { } mismatch) {
                    throw new InvalidDataException(mismatch);
                }
                leases.Add(kernel, cache.Acquire(device, GpuPassPipelineKey.OfCompute(bytecode,
                    SdfWorldTables.PipelineLayouts.Specs[(int)kernel].Description)));
            }
            await Task.WhenAll(leases.Values.Select(lease => lease.WaitAsync(cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new(request, leases);
        } catch {
            GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(leases.Values.ToArray());
            throw;
        }
    }

    public void Dispose() => GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline>.Release(m_leases.Values.ToArray());
}
