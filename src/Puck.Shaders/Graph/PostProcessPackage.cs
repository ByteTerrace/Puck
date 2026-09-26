using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>The <c>post.&lt;id&gt;</c> package of one shipped post-process shader set: a fullscreen draw of the set's
/// vertex and fragment stages that samples the pass's one input image and writes its one output. It binds the set's
/// interface (<see cref="ShaderSetManifest.FrameLayout"/>), which the plan lays out for the pass: the frame group, and the
/// pass group holding the extent, the set's config, the input image and the set's samplers. One type serves every
/// <c>post.&lt;id&gt;</c>, registered once per set under <see cref="Id"/>.
/// <para>
/// Its build leases the graphics pipeline, its two shader modules and the render pass it draws in, through the pass's
/// pipeline layout, from the pass-pipeline cache on the thread pool, so every pass of one set into one format shares
/// them. Its recorder takes them when the graph installs, creates the fullscreen triangle's vertex
/// buffer and one sampler per frame slot, and allocates its frame and pass group sets per frame slot from the instance's
/// pool (<see cref="RenderGraphPackageSets"/>), writing the slot's sampler into every sampler the set declares. Each frame
/// it writes the input into the slot's pass set, binds both sets and records the render pass and the draw over a
/// framebuffer on the output image, created the first time that image is drawn into and kept for the recorder's life.
/// Its ports are a fragment-sampled input and a color-attachment output, so the input arrives shader-readable and the
/// output in render-target layout, which its render pass leaves it in; it records no barrier.
/// </para>
/// </summary>
public sealed class PostProcessPackage : IRenderGraphPackageFactory {
    private readonly string m_input;
    private readonly string[] m_samplers;

    /// <summary>Initializes a new instance of the <see cref="PostProcessPackage"/> class for one loaded shader set.</summary>
    /// <param name="manifest">The loaded graphics set, whose bytecode the build reads.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException"><paramref name="manifest"/> is a compute set, or declares anything but one
    /// sampled image (the input) and the samplers it is read through.</exception>
    public PostProcessPackage(ShaderSetManifest manifest) {
        ArgumentNullException.ThrowIfNull(argument: manifest);

        if (!manifest.IsGraphics) {
            throw new InvalidDataException(message: $"'{manifest.Name}' is a compute set; a post-process package needs vertex and fragment stages.");
        }

        var images = manifest.Bindings.Where(predicate: static binding => (binding.Kind == GpuBindingKind.SampledImage)).ToArray();

        if (
            (images.Length != 1) ||
            manifest.Bindings.Any(predicate: static binding => (binding.Kind is not (GpuBindingKind.SampledImage or GpuBindingKind.Sampler)))
        ) {
            throw new InvalidDataException(message: $"'{manifest.Name}' must declare exactly one SampledImage binding (the input image) and only Sampler bindings beside it to run as a post-process package.");
        }

        Manifest = manifest;
        m_input = images[0].Name;
        m_samplers = [.. manifest.Bindings.Where(predicate: static binding => (binding.Kind == GpuBindingKind.Sampler)).Select(selector: static binding => binding.Name)];
    }

    /// <summary>Gets the package id the set is registered under: <c>post.&lt;set id&gt;</c>.</summary>
    public string Id => (RenderGraphPackageCatalog.PostProcessPrefix + Manifest.Name);
    /// <summary>Gets the shader set the package draws.</summary>
    public ShaderSetManifest Manifest { get; }

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
    /// <exception cref="InvalidDataException">The pass does not read one image and write one, the plan laid out another
    /// interface than the set's, or the set lacks a stage's bytecode for the backend.</exception>
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
        // The plan lays the pass out from the catalog's declaration of the set; the bytecode reads the set's own. They
        // must bind alike, whatever each interface is named.
        if (!context.Parameters.Layout.Bindings.SequenceEqual(second: Manifest.FrameLayout.Layout.Bindings)) {
            throw new InvalidDataException(message: $"Pass '{context.Pass}' of package '{Id}' was planned with another interface than the set's; the catalog and the loaded set disagree.");
        }

        var lease = context.Pipelines.Acquire(
            device: context.Device,
            key: GpuPassPipelineKey.OfGraphics(
                description: new GpuGraphicsPipelineDescription(
                    Layout: context.Parameters.Layout.PipelineLayout(
                        stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
                    ),
                    Name: Manifest.Name,
                    VertexInput: new GpuVertexInputLayout(
                        FullscreenTriangle.StrideBytes,
                        [new GpuVertexAttribute(
                            Format: GpuVertexFormat.R32G32Float,
                            Location: 0,
                            OffsetBytes: 0
                        )]
                    )
                ),
                fragment: Bytecode(
                    directX: context.HostsOnDirectX,
                    stem: Manifest.Stages.Fragment!
                ),
                renderPass: new GpuRenderPassDescription(Colors: [new GpuColorAttachment(
                    FinalLayout: GpuImageLayout.RenderTarget,
                    Format: ShaderPipelineRenderNode.ParseFormat(format: context.Outputs[0].Format),
                    Load: GpuAttachmentLoad.Clear,
                    Store: GpuAttachmentStore.Store
                )]),
                vertex: Bytecode(
                    directX: context.HostsOnDirectX,
                    stem: Manifest.Stages.Vertex!
                )
            )
        );

        try {
            _ = lease.Wait(cancellationToken: cancellationToken);
        } catch {
            lease.Release();

            throw;
        }

        return new Built(lease: lease);
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="groups"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="built"/> is not this package's build, or
    /// <paramref name="groups"/> holds no pool or no block buffer per frame slot.</exception>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: groups);

        if (built is not Built objects) {
            built?.Dispose();

            throw new ArgumentException(
                message: $"Package '{Id}' was handed another package's build.",
                paramName: nameof(built)
            );
        }

        return new Recorder(
            built: objects,
            context: context,
            groups: groups,
            input: m_input,
            samplers: m_samplers
        );
    }

    // One pass's lease on its pipeline and the render pass it draws in, which its recorder owns once created.
    private sealed class Built(GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> lease) : IDisposable {
        public IGpuPipeline Pipeline { get; } = lease.Current!.Graphics!;
        public IGpuRenderPass RenderPass { get; } = lease.Current!.RenderPass!;

        public void Dispose() =>
            lease.Release();
    }
    // Records one post-process pass. Everything a frame slot binds is per slot, since the instance waits only that
    // slot's previous submission before recording into it.
    private sealed class Recorder : IRenderGraphPackageRecorder {
        private readonly Built m_built;
        // A framebuffer over each image the output can be, created at install, so a recording creates nothing.
        private readonly Dictionary<nint, IGpuFramebuffer> m_framebuffers;
        private readonly IGpuBuffer? m_geometry;
        private readonly uint m_input;
        private readonly nint[] m_samplers;
        private readonly GpuDeviceServices m_services;
        private readonly RenderGraphPackageSets? m_sets;

        private bool m_disposed;

        public Recorder(RenderGraphPackageRecorderContext context, Built built, RenderGraphPackageGroups groups, string input, string[] samplers) {
            var inFlight = context.InFlightFrames;

            m_built = built;
            m_framebuffers = new Dictionary<nint, IGpuFramebuffer>(capacity: inFlight);
            m_samplers = new nint[inFlight];
            m_services = context.Services;

            try {
                // The vertex buffer comes from the device's own factory, as a shader pass's geometry does, so it counts as
                // no created storage buffer.
                m_geometry = context.Device.Services.BufferFactory.CreateHostVisible(
                    data: FullscreenTriangle.CreateVertexData(),
                    name: new GpuObjectName(
                        detail: "geometry",
                        owner: context.Instance,
                        part: context.Pass
                    ),
                    usage: GpuBufferUsage.Vertex
                );

                foreach (var image in groups.OutputImages[0]) {
                    if (!m_framebuffers.ContainsKey(key: image.ImageHandle)) {
                        m_framebuffers.Add(
                            key: image.ImageHandle,
                            value: m_services.RenderPassFactory.CreateFramebuffer(
                                colors: [image],
                                depth: null,
                                renderPass: built.RenderPass
                            )
                        );
                    }
                }

                m_sets = new RenderGraphPackageSets(
                    context: context,
                    groupLayoutHandles: built.Pipeline.GroupLayoutHandles,
                    groups: groups
                );
                m_input = m_sets.BindingOf(member: input);

                for (var slot = 0; (slot < inFlight); slot++) {
                    m_samplers[slot] = m_services.Bindings.CreateSampler();

                    foreach (var sampler in samplers) {
                        m_services.Bindings.WriteSampler(
                            arrayElement: 0,
                            binding: m_sets.BindingOf(member: sampler),
                            descriptorSetHandle: m_sets.PassSet(slot: slot),
                            samplerHandle: m_samplers[slot]
                        );
                    }
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

            m_geometry?.Dispose();
            m_built.Dispose();
        }
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            var input = recording.Inputs[0];
            var output = recording.Outputs[0];
            var target = (output.Owned ?? throw new InvalidDataException(message: $"Output '{output.Version}' is not an image the instance owns, so it cannot be drawn into."));
            var command = recording.CommandBuffer;
            var recorder = recording.Recorder;
            var pipeline = m_built.Pipeline;
            var sets = m_sets!;

            var framebuffer = (m_framebuffers.GetValueOrDefault(key: target.ImageHandle) ?? throw new InvalidDataException(message: $"Output '{output.Version}' is an image the pass was not installed with."));

            m_services.Bindings.WriteSampledImage(
                arrayElement: 0,
                binding: m_input,
                descriptorSetHandle: sets.PassSet(slot: recording.Slot),
                imageViewHandle: input.Image.ImageViewHandle
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
                m_geometry!.BufferHandle,
                m_geometry.SizeBytes,
                FullscreenTriangle.StrideBytes
            );
            sets.Bind(
                bindPoint: GpuBindPoint.Graphics,
                commandBuffer: command,
                pipelineLayout: pipeline.LayoutHandle,
                recorder: recorder,
                slot: recording.Slot
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
