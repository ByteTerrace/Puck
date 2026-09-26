using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>The recorder factory of every post-process package (<see cref="RenderGraphPackage.IsPostProcess"/>): a
/// fullscreen draw of the package's vertex and fragment stages that samples the pass's one input image and writes its one
/// output. It binds the package's interface, which the plan lays out for the pass from the catalog's declaration
/// (<see cref="ShaderPipelineParameterLayout.ForPackage"/>): the frame group, and the pass group holding the extent, the
/// package's config, the input image and its samplers. One type serves every post-process package, registered once per
/// package under its <see cref="Id"/>.
/// <para>
/// Its build reads and validates the stages' deployed bytecode for the backend, then leases the graphics pipeline, its two
/// shader modules and the render pass it draws in, through the pass's pipeline layout, from the pass-pipeline cache on the
/// thread pool, so every pass of one package into one format shares them. Its recorder takes them when the graph
/// installs, creates the fullscreen triangle's vertex buffer and one sampler per frame slot, and allocates its frame and
/// pass group sets per frame slot from the instance's pool (<see cref="RenderGraphPackageSets"/>), writing the slot's
/// sampler into every sampler the package declares. Each frame it writes the input into the slot's pass set, binds both
/// sets and records the render pass and the draw over a framebuffer on the output image, created the first time that
/// image is drawn into and kept for the recorder's life. Its ports are a fragment-sampled input and a color-attachment
/// output, so the input arrives shader-readable and the output in render-target layout, which its render pass leaves it
/// in; it records no barrier.
/// </para>
/// </summary>
public sealed class PostProcessPackage : IRenderGraphPackageFactory {
    private readonly string m_directory;
    private readonly string m_input;
    private readonly string[] m_samplers;

    /// <summary>Initializes a new instance of the <see cref="PostProcessPackage"/> class for one post-process
    /// package.</summary>
    /// <param name="package">The package, whose <see cref="RenderGraphPackage.Stages"/> name the bytecode the build
    /// reads.</param>
    /// <param name="root">The directory the stages' directory resolves against, or <see langword="null"/> for the
    /// directory beside the executable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException"><paramref name="package"/> is not a post-process package, or declares
    /// anything but one sampled image (the input) and the samplers it is read through.</exception>
    public PostProcessPackage(RenderGraphPackage package, string? root = null) {
        ArgumentNullException.ThrowIfNull(argument: package);

        if (package.Stages is not { } stages) {
            throw new InvalidDataException(message: $"Package '{package.Id}' declares no stages; a post-process package draws with a vertex and a fragment stage.");
        }

        var images = package.Members.Where(predicate: static member => (member.Kind == ShaderInterfaceMemberKind.SampledImage)).ToArray();

        if (
            (images.Length != 1) ||
            package.Members.Any(predicate: static member => (member.Kind is not (ShaderInterfaceMemberKind.SampledImage or ShaderInterfaceMemberKind.Sampler)))
        ) {
            throw new InvalidDataException(message: $"Package '{package.Id}' must declare exactly one sampled image (the input) and only samplers beside it to run as a post-process package.");
        }

        Package = package;
        m_directory = Path.Combine(
            path1: (root ?? AppContext.BaseDirectory),
            path2: stages.Directory
        );
        m_input = images[0].Name;
        m_samplers = [.. package.Members.Where(predicate: static member => (member.Kind == ShaderInterfaceMemberKind.Sampler)).Select(selector: static member => member.Name)];
    }

    /// <summary>Gets the id the package is registered under.</summary>
    public string Id => Package.Id;
    /// <summary>Gets the package the factory draws.</summary>
    public RenderGraphPackage Package { get; }

    // One stage's deployed bytecode for the backend the instance records on, validated as bytecode of its format.
    private byte[] Bytecode(string stem, bool directX) {
        var path = Path.Combine(
            path1: m_directory,
            path2: (stem + (directX
                ? ".dxil"
                : ".spv"))
        );
        var bytecode = File.ReadAllBytes(path: path);

        try {
            ShaderBytecode.ValidateFormat(bytecode: bytecode);
        } catch (ArgumentException exception) {
            throw new InvalidDataException(
                message: $"Package '{Id}' stage bytecode failed format validation: {path} ({exception.Message})",
                innerException: exception
            );
        }

        return bytecode;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The pass does not read one image and write one, or a stage's bytecode is
    /// not valid bytecode of the backend's format.</exception>
    /// <exception cref="IOException">A stage's deployed bytecode is missing or cannot be read.</exception>
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

        var stages = Package.Stages!;
        var lease = context.Pipelines.Acquire(
            device: context.Device,
            key: GpuPassPipelineKey.OfGraphics(
                description: new GpuGraphicsPipelineDescription(
                    Layout: context.Parameters.Layout.PipelineLayout(
                        pushesIndex: false,
                        stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
                    ),
                    Name: Id,
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
                    stem: stages.Fragment
                ),
                renderPass: new GpuRenderPassDescription(Colors: [new GpuColorAttachment(
                    FinalLayout: GpuImageLayout.RenderTarget,
                    Format: ShaderPipelineRenderNode.ParseFormat(format: context.Outputs[0].Format),
                    Load: GpuAttachmentLoad.Clear,
                    Store: GpuAttachmentStore.Store
                )]),
                vertex: Bytecode(
                    directX: context.HostsOnDirectX,
                    stem: stages.Vertex
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
