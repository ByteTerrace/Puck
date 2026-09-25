using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>The <c>post.&lt;id&gt;</c> package of one shipped post-process shader set: a fullscreen draw of the set's
/// vertex and fragment stages that samples the pass's one input image and writes its one output, with the pass's frame
/// block (the frame members, then the set's config) pushed as its push constants. One type serves every
/// <c>post.&lt;id&gt;</c>, registered once per set under <see cref="Id"/>.
/// <para>
/// Its build creates the two shader modules, the render pass and the graphics pipeline on the thread pool. Its recorder
/// takes them when the graph installs, creates the fullscreen triangle's vertex buffer and one sampler per frame slot,
/// and allocates one descriptor set per frame slot from the instance's pool. Each frame it writes the input into the
/// slot's set and records the render pass and the draw over a framebuffer on the output image, created the first time
/// that image is drawn into and kept for the recorder's life. Its ports are a fragment-sampled input and a
/// color-attachment output, so the input arrives shader-readable and the output in render-target layout, which its
/// render pass leaves it in; it records no barrier.
/// </para>
/// </summary>
public sealed class PostProcessPackage : IRenderGraphPackageFactory {
    private readonly GpuComputeBinding[] m_bindings;

    /// <summary>Initializes a new instance of the <see cref="PostProcessPackage"/> class for one loaded shader set.</summary>
    /// <param name="manifest">The loaded graphics set, whose bytecode the build reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException"><paramref name="manifest"/> is a compute set, or declares anything but one
    /// sampled image.</exception>
    public PostProcessPackage(ShaderSetManifest manifest) {
        ArgumentNullException.ThrowIfNull(argument: manifest);

        if (!manifest.IsGraphics) {
            throw new InvalidDataException(message: $"'{manifest.Name}' is a compute set; a post-process package needs vertex and fragment stages.");
        }
        if (
            (manifest.Bindings.Count != 1) ||
            (manifest.Bindings[0].Kind != ShaderSetManifestBindingKind.SampledImage) ||
            (manifest.Bindings[0].Count != 1)
        ) {
            throw new InvalidDataException(message: $"'{manifest.Name}' must declare exactly one sampledImage binding (the input image) and nothing else to run as a post-process package.");
        }

        Manifest = manifest;
        m_bindings = [new GpuComputeBinding(
            manifest.Bindings[0].VulkanBinding,
            GpuComputeBindingKind.SampledImage
        )];
    }

    /// <summary>Gets the package id the set is registered under: <c>post.&lt;set id&gt;</c>.</summary>
    public string Id => (RenderGraphPackageCatalog.PostProcessPrefix + Manifest.Name);
    /// <summary>Gets the shader set the package draws.</summary>
    public ShaderSetManifest Manifest { get; }
    /// <inheritdoc/>
    /// <remarks>One sampled image, the pass's input, at the set's binding.</remarks>
    public IReadOnlyList<GpuComputeBinding> SetBindings => m_bindings;

    // The stage bytecode of the backend the instance records on, read from the manifest's validated bytes.
    private ReadOnlyMemory<byte> Bytecode(string stem, bool directX) {
        var key = (stem + (directX
            ? ".dxil"
            : ".spv"));

        return (Manifest.Bytecode.TryGetValue(
            key: key,
            value: out var bytecode
        )
            ? bytecode
            : throw new FileNotFoundException(message: $"'{Manifest.Name}' manifest carries no validated bytecode for '{key}'."));
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The pass does not read one image and write one, its frame block is not the
    /// set's, or the set lacks a stage's bytecode for the backend.</exception>
    public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (
            (context.Inputs.Count != 1) ||
            (context.Outputs.Count != 1) ||
            (context.Inputs[0].Kind != ShaderPipelineResourceKind.Image) ||
            (context.Outputs[0].Kind != ShaderPipelineResourceKind.Image)
        ) {
            throw new InvalidDataException(message: $"Pass '{context.Pass}' of package '{Id}' must read one image and write one.");
        }

        var services = context.Services;
        var built = new Built();

        try {
            cancellationToken.ThrowIfCancellationRequested();
            built.Vertex = services.ShaderModuleFactory.Create(
                bytecode: Bytecode(
                    directX: context.HostsOnDirectX,
                    stem: Manifest.Stages.Vertex!
                ),
                stage: GpuShaderStage.Vertex
            );
            cancellationToken.ThrowIfCancellationRequested();
            built.Fragment = services.ShaderModuleFactory.Create(
                bytecode: Bytecode(
                    directX: context.HostsOnDirectX,
                    stem: Manifest.Stages.Fragment!
                ),
                stage: GpuShaderStage.Fragment
            );
            built.RenderPass = services.RenderPassFactory.Create(description: new GpuRenderPassDescription(Colors: [new GpuColorAttachment(
                FinalLayout: GpuImageLayout.RenderTarget,
                Format: ShaderPipelineRenderNode.ParseFormat(format: context.Outputs[0].Format),
                Load: GpuAttachmentLoad.Clear,
                Store: GpuAttachmentStore.Store
            )]), name: new GpuObjectName(
                owner: context.Instance,
                part: context.Pass
            ));

            var description = new GpuGraphicsPipelineDescription(
                Manifest.Name,
                new GpuVertexInputLayout(
                    FullscreenTriangle.StrideBytes,
                    [new GpuVertexAttribute(
                        Format: GpuVertexFormat.R32G32Float,
                        Location: 0,
                        OffsetBytes: 0
                    )]
                ),
                1,
                false,
                new GpuPushConstantBinding(
                    data: new byte[context.Parameters.SizeBytes],
                    offset: 0,
                    stageFlags: ShaderPipelineRenderNode.FrameBlockStages
                )
            );

            Manifest.ValidateBindings(description: description);
            cancellationToken.ThrowIfCancellationRequested();
            built.Pipeline = services.PipelineFactory.Create(
                description: description,
                fragmentShaderModule: built.Fragment,
                name: new GpuObjectName(
                    owner: context.Instance,
                    part: context.Pass
                ),
                renderPass: built.RenderPass,
                vertexShaderModule: built.Vertex
            );
        } catch {
            built.Dispose();

            throw;
        }

        return built;
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="built"/> is not this package's build, or
    /// <paramref name="descriptorPool"/> is zero.</exception>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, nint descriptorPool) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (built is not Built objects) {
            built?.Dispose();

            throw new ArgumentException(
                message: $"Package '{Id}' was handed another package's build.",
                paramName: nameof(built)
            );
        }
        if (descriptorPool == 0) {
            objects.Dispose();

            throw new ArgumentException(
                message: $"Package '{Id}' allocates its sets from the instance's pool, and none was created.",
                paramName: nameof(descriptorPool)
            );
        }

        return new Recorder(
            binding: m_bindings[0].Binding,
            built: objects,
            context: context,
            descriptorPool: descriptorPool
        );
    }

    // The modules, render pass and pipeline one pass's build creates, which its recorder owns once created.
    private sealed class Built : IDisposable {
        public IGpuShaderModule? Fragment;
        public IGpuPipeline? Pipeline;
        public IGpuRenderPass? RenderPass;
        public IGpuShaderModule? Vertex;

        public void Dispose() {
            Pipeline?.Dispose();
            Pipeline = null;
            RenderPass?.Dispose();
            RenderPass = null;
            Fragment?.Dispose();
            Fragment = null;
            Vertex?.Dispose();
            Vertex = null;
        }
    }
    // Records one post-process pass. Everything a frame slot binds is per slot, since the instance waits only that
    // slot's previous submission before recording into it.
    private sealed class Recorder : IRenderGraphPackageRecorder {
        private readonly uint m_binding;
        private readonly Built m_built;
        private readonly Dictionary<nint, IGpuFramebuffer> m_framebuffers;
        private readonly IGpuBuffer m_geometry;
        private readonly nint[] m_samplers;
        private readonly GpuDeviceServices m_services;
        private readonly nint[] m_sets;

        private bool m_disposed;

        public Recorder(RenderGraphPackageRecorderContext context, Built built, nint descriptorPool, uint binding) {
            var inFlight = context.InFlightFrames;

            m_binding = binding;
            m_built = built;
            m_framebuffers = new Dictionary<nint, IGpuFramebuffer>(capacity: inFlight);
            m_samplers = new nint[inFlight];
            m_services = context.Services;
            m_sets = new nint[inFlight];

            // The vertex buffer comes from the device's own factory, as a shader pass's geometry does, so it counts as no
            // created storage buffer.
            m_geometry = context.Device.Services.BufferFactory.CreateHostVisible(
                data: FullscreenTriangle.CreateVertexData(),
                name: new GpuObjectName(
                    detail: "geometry",
                    owner: context.Instance,
                    part: context.Pass
                ),
                usage: GpuBufferUsage.Vertex
            );

            try {
                for (var slot = 0; (slot < inFlight); slot++) {
                    m_sets[slot] = m_services.Bindings.AllocateSet(
                        descriptorPool,
                        built.Pipeline!.DescriptorSetLayoutHandle,
                        name: new GpuObjectName(
                            index: slot,
                            owner: context.Instance,
                            part: context.Pass
                        )
                    );
                    m_samplers[slot] = m_services.Bindings.CreateSampler();
                }
            } catch {
                Dispose();

                throw;
            }
        }

        public void Dispose() {
            if (m_disposed) {
                return;
            }

            m_disposed = true;

            foreach (var framebuffer in m_framebuffers.Values) {
                framebuffer.Dispose();
            }

            m_framebuffers.Clear();

            foreach (var sampler in m_samplers) {
                if (sampler != 0) {
                    m_services.Bindings.DestroySampler(samplerHandle: sampler);
                }
            }

            m_geometry.Dispose();
            m_built.Dispose();
        }
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            var input = recording.Inputs[0];
            var output = recording.Outputs[0];
            var target = (output.Owned ?? throw new InvalidDataException(message: $"Output '{output.Version}' is not an image the instance owns, so it cannot be drawn into."));
            var command = recording.CommandBuffer;
            var recorder = recording.Recorder;
            var pipeline = m_built.Pipeline!;
            var set = m_sets[recording.Slot];

            if (!m_framebuffers.TryGetValue(
                key: target.ImageHandle,
                value: out var framebuffer
            )) {
                framebuffer = m_services.RenderPassFactory.CreateFramebuffer(
                    colors: [target],
                    depth: null,
                    renderPass: m_built.RenderPass!
                );
                m_framebuffers.Add(
                    key: target.ImageHandle,
                    value: framebuffer
                );
            }

            m_services.Bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: m_binding,
                descriptorSetHandle: set,
                imageViewHandle: input.Image.ImageViewHandle,
                samplerHandle: m_samplers[recording.Slot]
            );
            recorder.BeginRenderPass(
                command,
                framebuffer
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                pipelineHandle: pipeline.Handle
            );
            recorder.BindVertexBuffer(
                command,
                m_geometry.BufferHandle,
                m_geometry.SizeBytes,
                FullscreenTriangle.StrideBytes
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                data: recording.FrameBlock,
                offset: 0,
                pipelineLayoutHandle: pipeline.LayoutHandle,
                stageFlags: ShaderPipelineRenderNode.FrameBlockStages
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                descriptorSetHandle: set,
                group: 0,
                pipelineLayoutHandle: pipeline.LayoutHandle
            );
            recorder.Draw(
                commandBufferHandle: command,
                parameters: new GpuDrawParameters(
                    FullscreenTriangle.VertexCount,
                    1
                )
            );
            recorder.EndRenderPass(commandBufferHandle: command);

            return RenderGraphPackageOutcome.Drew;
        }
    }
}
