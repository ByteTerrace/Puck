using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.Overlays;

/// <summary>
/// The <c>overlay</c> package (<see cref="RenderGraphPackageCatalog.Overlay"/>): the unified overlay drawn as a package
/// pass over its one input image into its one output, through the same <see cref="OverlayFrameComposer"/> and fragment
/// shader as <see cref="UnifiedOverlayNode"/>, in today's binding layout (<see cref="OverlayPassLayout"/>).
/// <para>
/// Its build creates the two shader modules, the render pass and the graphics pipeline on the thread pool. Its recorder
/// takes them when the graph installs and, since it never waits a fence of its own, keeps what a frame rewrites per
/// frame slot: a descriptor set per slot from the instance's pool, and a region of the storage buffer per slot after the
/// static prefix (the token slab and the glyph pack), whose bases it pushes. The <c>Frame</c> elements' leases go to the
/// frame's lease list, which retires them after the slot's fence. A frame with nothing visible records nothing and
/// reports <see cref="RenderGraphPackageOutcome.DrewNothing"/>, so the instance publishes the input in the output's
/// place and a capture follows it, when the recording may stand in
/// (<see cref="RenderGraphPackageRecording.MayStandIn"/>); otherwise it draws the empty frame, which reproduces the
/// input. Its ports are a fragment-sampled input and a color-attachment output, whose barriers the
/// instance records from the plan, so the recorder records none.
/// </para>
/// </summary>
/// <param name="sources">The per-surface read seams and the feed tick.</param>
/// <param name="capacity">The host's declared counts the lease table is derived from.</param>
/// <param name="glyphs">The shared SDF glyph pack.</param>
/// <param name="frameSources">The host's <see cref="OverlayHudElementKind.Frame"/> content seam.</param>
/// <param name="vertexBytecode">The fullscreen vertex shader, in the host backend's bytecode format.</param>
/// <param name="fragmentBytecode">The unified overlay fragment shader, in the host backend's bytecode format.</param>
/// <param name="theme">The theme a recorder starts from; <see cref="UpdateTheme"/> moves it.</param>
public sealed class OverlayPackage(UnifiedOverlaySources sources, OverlayCapacity capacity, OverlayGlyphSdfPack glyphs, IOverlayFrameSources frameSources, ReadOnlyMemory<byte> vertexBytecode, ReadOnlyMemory<byte> fragmentBytecode, OverlayThemeValues theme = default) : IRenderGraphPackageFactory {
    private OverlayThemeValues m_theme = theme;
    private int m_themeRevision;

    /// <inheritdoc/>
    public IReadOnlyList<GpuComputeBinding> SetBindings => OverlayPassLayout.SetBindings;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The pass does not read one image and write one.</exception>
    public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (
            (context.Inputs.Count != 1) ||
            (context.Outputs.Count != 1) ||
            (context.Inputs[0].Kind != ShaderPipelineResourceKind.Image) ||
            (context.Outputs[0].Kind != ShaderPipelineResourceKind.Image)
        ) {
            throw new InvalidDataException(message: $"Pass '{context.Pass}' of package '{RenderGraphPackageCatalog.Overlay}' must read one image and write one.");
        }

        var services = context.Services;
        var built = new Built();

        try {
            cancellationToken.ThrowIfCancellationRequested();
            built.Vertex = services.ShaderModuleFactory.Create(
                bytecode: vertexBytecode,
                stage: GpuShaderStage.Vertex
            );
            cancellationToken.ThrowIfCancellationRequested();
            built.Fragment = services.ShaderModuleFactory.Create(
                bytecode: fragmentBytecode,
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
            cancellationToken.ThrowIfCancellationRequested();
            built.Pipeline = services.PipelineFactory.Create(
                name: new GpuObjectName(owner: context.Instance, part: context.Pass),
                description: OverlayPassLayout.PipelineDescription(),
                fragmentShaderModule: built.Fragment,
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
                message: "The overlay package was handed another package's build.",
                paramName: nameof(built)
            );
        }
        if (descriptorPool == 0) {
            objects.Dispose();

            throw new ArgumentException(
                message: "The overlay package allocates its sets from the instance's pool, and none was created.",
                paramName: nameof(descriptorPool)
            );
        }

        return new Recorder(
            built: objects,
            composer: new OverlayFrameComposer(
                capacity: capacity,
                frameSources: frameSources,
                glyphs: glyphs,
                height: context.Height,
                sources: sources,
                theme: in m_theme,
                width: context.Width
            ),
            context: context,
            descriptorPool: descriptorPool,
            package: this
        );
    }
    /// <summary>Republishes the theme every recorder's writers read; each recorder refills its token slab on its next
    /// frame.</summary>
    /// <param name="theme">The newly resolved theme.</param>
    public void UpdateTheme(in OverlayThemeValues theme) {
        m_theme = theme;
        m_themeRevision++;
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
    // Records one overlay pass. Everything a frame rewrites is per frame slot, since the instance waits only that slot's
    // previous submission before recording into it.
    private sealed class Recorder : IRenderGraphPackageRecorder {
        private readonly Built m_built;
        private readonly OverlayFrameComposer m_composer;
        private readonly IGpuStorageBuffer m_data;
        private readonly Dictionary<nint, IGpuFramebuffer> m_framebuffers;
        private readonly IGpuBuffer m_geometry;
        private readonly OverlayPackage m_package;
        private readonly byte[] m_push = new byte[OverlayPassLayout.PushConstantBytes];
        private readonly nint m_sampler;
        private readonly GpuDeviceServices m_services;
        private readonly nint[] m_sets;

        private bool m_disposed;
        private int m_themeRevision;

        public Recorder(RenderGraphPackageRecorderContext context, Built built, OverlayFrameComposer composer, nint descriptorPool, OverlayPackage package) {
            var inFlight = context.InFlightFrames;
            var builder = composer.Builder;
            var totalWords = (builder.PanelBaseWords + (inFlight * composer.DynamicWords));

            m_built = built;
            m_composer = composer;
            m_framebuffers = new Dictionary<nint, IGpuFramebuffer>(capacity: inFlight);
            m_package = package;
            m_services = context.Services;
            m_sets = new nint[inFlight];
            m_themeRevision = package.m_themeRevision;
            // The vertex buffer comes from the device's own factory, as a shader pass's geometry does.
            m_geometry = context.Device.Services.BufferFactory.CreateHostVisible(
                name: new GpuObjectName(owner: context.Instance, part: context.Pass, detail: "geometry"),
                data: FullscreenTriangle.CreateVertexData(),
                usage: GpuBufferUsage.Vertex
            );

            try {
                m_data = m_services.BufferFactory.CreateHostVisible(
                    name: new GpuObjectName(owner: context.Instance, part: context.Pass, detail: "data"),
                    sizeBytes: (((ulong)totalWords) * sizeof(uint)),
                    usage: GpuBufferUsage.Storage
                );
                m_sampler = m_services.Bindings.CreateSampler();

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
                    m_services.Bindings.WriteBuffer(
                        binding: OverlayPassLayout.StorageBufferBinding,
                        bufferHandle: m_data.BufferHandle,
                        bufferSize: (((ulong)totalWords) * sizeof(uint)),
                        descriptorSetHandle: m_sets[slot],
                        elementStride: OverlayPassLayout.StorageElementStrideBytes,
                        kind: GpuBindingKind.ReadOnlyBuffer
                    );
                }

                // The token slab and the glyph pack are static and shared by every slot's region.
                m_data.Write<uint>(data: builder.Scratch[..builder.PanelBaseWords]);
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

            if (m_sampler != 0) {
                m_services.Bindings.DestroySampler(samplerHandle: m_sampler);
            }

            m_data?.Dispose();
            m_geometry.Dispose();
            m_built.Dispose();
        }
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            if (m_themeRevision != m_package.m_themeRevision) {
                m_themeRevision = m_package.m_themeRevision;
                m_composer.UpdateTheme(theme: in m_package.m_theme);
                m_data.Write<uint>(data: m_composer.Builder.Scratch[..OverlayTokenBlock.WordCount]);
            }

            var visible = m_composer.Compose(renderTicks: recording.Context.RenderTicks);

            // With nothing visible the world stands for the overlay's output when the instance lets it; otherwise the
            // empty frame is drawn, which reproduces the world into the output.
            if (
                !visible &&
                recording.MayStandIn
            ) {
                m_composer.FrameSlots.MoveTo(destination: recording.Leases);

                return RenderGraphPackageOutcome.DrewNothing;
            }

            var input = recording.Inputs[0];
            var output = recording.Outputs[0];
            var target = (output.Owned ?? throw new InvalidDataException(message: $"Output '{output.Version}' is not an image the instance owns, so the overlay cannot draw into it."));
            var command = recording.CommandBuffer;
            var recorder = recording.Recorder;
            var pipeline = m_built.Pipeline!;
            var set = m_sets[recording.Slot];
            var shift = (recording.Slot * m_composer.DynamicWords);

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

            WriteImageDescriptors(
                set: set,
                worldView: input.Image.ImageViewHandle
            );
            m_composer.WritePushConstants(
                block: m_push,
                shiftWords: shift
            );
            m_composer.UploadFrameRegions(
                buffer: m_data,
                shiftWords: shift
            );
            recorder.BeginRenderPass(
                commandBufferHandle: command,
                framebuffer: framebuffer
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                pipelineHandle: pipeline.Handle
            );
            recorder.BindVertexBuffer(
                bufferHandle: m_geometry.BufferHandle,
                commandBufferHandle: command,
                sizeBytes: m_geometry.SizeBytes,
                strideBytes: FullscreenTriangle.StrideBytes
            );
            recorder.PushConstants(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                data: m_push,
                offset: 0,
                pipelineLayoutHandle: pipeline.LayoutHandle,
                stageFlags: GpuShaderStage.Fragment
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
            m_composer.FrameSlots.MoveTo(destination: recording.Leases);

            return RenderGraphPackageOutcome.Drew;
        }

        // Writes the slot's image bindings: the world image, and each frame slot's bound lease or, unbound, the world
        // image, so every binding the shader can reach is valid.
        private void WriteImageDescriptors(nint set, nint worldView) {
            var slots = m_composer.FrameSlots;
            var boundCount = slots.BoundCount;

            m_services.Bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: OverlayPassLayout.SamplerBinding,
                descriptorSetHandle: set,
                imageViewHandle: worldView,
                samplerHandle: m_sampler
            );

            for (var slot = 0; (slot < OverlayFrameSlots.SlotCount); slot++) {
                m_services.Bindings.WriteCombinedImageSampler(
                    arrayElement: 0,
                    binding: (OverlayPassLayout.FrameSlotFirstBinding + ((uint)slot)),
                    descriptorSetHandle: set,
                    imageViewHandle: ((slot < boundCount)
                        ? slots.LeaseAt(slot: slot).ImageViewHandle
                        : worldView),
                    samplerHandle: m_sampler
                );
            }
        }
    }
}
