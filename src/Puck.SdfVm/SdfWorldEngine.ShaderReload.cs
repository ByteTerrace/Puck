using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    private readonly List<ReloadablePipeline> m_reloadablePipelines = [];
    private SdfWorldKernels m_loadedKernels;

    private static ReadOnlyMemory<byte> KernelBytes(in SdfWorldKernels kernels, string name) => name switch {
        "sdf-beam" => kernels.Beam,
        "sdf-instance-cull" => kernels.InstanceCull,
        "sdf-cull-args" => kernels.CullArgs,
        "sdf-world-views" => kernels.Views,
        "sdf-world-views-core" => kernels.ViewsCore,
        "sdf-world-views-folds" => kernels.ViewsFolds,
        "sdf-sky" => kernels.Sky,
        "sdf-world-composite" => kernels.Composite,
        "sdf-brick-bake" => kernels.BrickBake,
        "sdf-brick-upload" => kernels.BrickUpload,
        "sdf-frame-upload" => kernels.FrameUpload,
        _ => throw new ArgumentException(message: $"Unknown SDF kernel '{name}'.", paramName: nameof(name)),
    };

    private IGpuComputePipeline CreateReloadablePipeline(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, IGpuDeviceContext deviceContext) {
        var pipeline = new ReloadablePipeline(
            description: description,
            current: new PipelineVersion(
                native: m_gpu.ComputePipelineFactory.Create(deviceContext: deviceContext, description: description, computeShaderModule: computeShaderModule),
                shader: null // Initial shader modules remain owned by the engine's existing fields.
            )
        );
        m_reloadablePipelines.Add(item: pipeline);
        return pipeline;
    }

    /// <summary>Replaces changed compute pipelines at a render-thread boundary, preserving buffers, images, baked
    /// bricks, descriptors and scene state. Unchanged bytecode creates no pipelines and causes no GPU wait.</summary>
    /// <param name="kernels">A complete compiled set for this engine's current backend and unchanged host binding ABI.</param>
    /// <returns>The number of changed, active pipelines installed.</returns>
    /// <remarks>Call serially with submission, never from a concurrent command thread. Candidates are created before
    /// the frame ring drains. The beam/views ISA handshake runs before commit; failures restore the previous set.
    /// A C# binding or buffer-layout change still requires rebuilding the host. This does not reload child engines
    /// or postprocessing decorators owned by other nodes.</remarks>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A preview readback is outstanding, pipeline creation fails, or the ISA handshake fails.</exception>
    /// <exception cref="ArgumentException">Candidate bytecode is malformed or unsupported by the backend.</exception>
    public int ReloadKernels(in SdfWorldKernels kernels) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ThrowIfPipelinedFrameInFlight();
        var replacements = new List<(ReloadablePipeline Slot, PipelineVersion Candidate)>();
        var previous = new List<PipelineVersion>();
        var installed = false;
        try {
            foreach (var slot in m_reloadablePipelines) {
                var bytes = KernelBytes(kernels: kernels, name: slot.Description.Name);
                if (bytes.Span.SequenceEqual(other: KernelBytes(kernels: m_loadedKernels, name: slot.Description.Name).Span)) {
                    continue;
                }

                var shader = m_gpu.ShaderModuleFactory.Create(deviceContext: m_deviceContext, stage: GpuShaderStage.Compute, bytecode: bytes);
                try {
                    var native = m_gpu.ComputePipelineFactory.Create(deviceContext: m_deviceContext, description: slot.Description, computeShaderModule: shader);
                    replacements.Add(item: (slot, new PipelineVersion(native: native, shader: shader)));
                } catch {
                    shader.Dispose();
                    throw;
                }
            }

            if (replacements.Count == 0) {
                m_loadedKernels = kernels;
                return 0;
            }

            WaitForFrameRing();
            foreach (var (slot, candidate) in replacements) {
                previous.Add(item: slot.Exchange(replacement: candidate));
            }
            installed = true;
            try {
                SdfShaderSetVerification.VerifyShaderSet(device: m_deviceContext, kernels: kernels, verify: VerifyIsaVersion);
            } finally {
                // The ISA probe temporarily borrows slot zero's source descriptors and tile buffer. Force their
                // normal rebinding and a fresh render even on rollback; retained world/image ownership is unchanged.
                Array.Clear(array: m_boundSourceViews[0]);
                Array.Clear(array: m_boundScreenSourceViews[0]);
                Array.Clear(array: m_boundGlyphAtlasViews);
                m_hasPreviousFrameSignature = false;
                m_shadowAccumulationResetFrames = ShadowAccumulationResetFrames;
            }

            m_loadedKernels = kernels;
        } catch {
            if (installed) {
                for (var index = 0; index < replacements.Count; index++) {
                    _ = replacements[index].Slot.Exchange(replacement: previous[index]);
                }
            }
            foreach (var (_, candidate) in replacements) {
                candidate.Dispose();
            }
            throw;
        }

        foreach (var retired in previous) {
            retired.Dispose();
        }
        return replacements.Count;
    }

    private sealed class PipelineVersion(IGpuComputePipeline native, IGpuShaderModule? shader) : IDisposable {
        public IGpuComputePipeline Native { get; } = native;
        public void Dispose() {
            Native.Dispose();
            shader?.Dispose();
        }
    }

    // The engine's record paths retain these stable slots. Native handles change together between frames, with
    // identically defined layouts, so both backends can reuse the already-allocated descriptor sets.
    private sealed class ReloadablePipeline(GpuComputePipelineDescription description, PipelineVersion current) : IGpuComputePipeline {
        private PipelineVersion m_current = current;
        public GpuComputePipelineDescription Description { get; } = description;
        public nint Handle => m_current.Native.Handle;
        public nint LayoutHandle => m_current.Native.LayoutHandle;
        public nint DescriptorSetLayoutHandle => m_current.Native.DescriptorSetLayoutHandle;
        public PipelineVersion Exchange(PipelineVersion replacement) {
            var previous = m_current;
            m_current = replacement;
            return previous;
        }
        public void Dispose() => m_current.Dispose();
    }
}
