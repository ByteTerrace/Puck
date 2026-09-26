using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.Overlays;

/// <summary>
/// The <c>overlay</c> package (<see cref="RenderGraphPackageCatalog.Overlay"/>): the unified overlay drawn as a package
/// pass over its one input image into its one output, through <see cref="OverlayFrameComposer"/> and
/// <c>overlay-unified.frag.hlsl</c>, which reads the package's interface as the plan lays it out: the frame group, and the
/// pass group holding the extent, the three per-frame values the recorder writes, the input image, the frame slot
/// images, the one sampler they are all read through and the storage buffer
/// (<see cref="RenderGraphPackageCatalog.OverlayMembers"/>).
/// <para>
/// Its build creates the two shader modules, the render pass and the graphics pipeline, through the pass's pipeline
/// layout, on the thread pool. It states one region (<see cref="Regions"/>), the storage buffer the shader reads: the
/// static prefix (the token slab and the glyph pack), then the frame's packed records at the builder's own bases, which
/// it writes into the pass block. The instance creates the region under the policy the device's memory selects and
/// flushes and copies it. The recorder takes the built objects when the graph installs and, since it never waits a fence
/// of its own, keeps its frame and pass group sets per frame slot from the instance's pool
/// (<see cref="RenderGraphPackageSets"/>); it writes the static prefix into the region once, the token slab again on a
/// theme change, and each frame's records, so the region owes only the words a frame changes. The <c>Frame</c>
/// elements' leases go to the frame's lease list, which retires them after the slot's fence. A frame with nothing visible records nothing and
/// reports <see cref="RenderGraphPackageOutcome.DrewNothing"/>, so the instance publishes the input in the output's
/// place and a capture follows it, when the recording may stand in
/// (<see cref="RenderGraphPackageRecording.MayStandIn"/>); otherwise it draws the empty frame, which reproduces the
/// input. Its ports are a fragment-sampled input and a color-attachment output, whose barriers the instance records from
/// the plan, so the recorder records none.
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
                description: new GpuGraphicsPipelineDescription(
                    Layout: context.Parameters.Layout.PipelineLayout(
                        stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
                    ),
                    Name: "overlay-unified",
                    VertexInput: new GpuVertexInputLayout(
                        Attributes: [new GpuVertexAttribute(
                            Format: GpuVertexFormat.R32G32Float,
                            Location: 0,
                            OffsetBytes: 0
                        )],
                        StrideBytes: FullscreenTriangle.StrideBytes
                    )
                ),
                name: new GpuObjectName(
                    owner: context.Instance,
                    part: context.Pass
                ),
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
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="groups"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="built"/> is not this package's build, or
    /// <paramref name="groups"/> holds no pool, no block buffer per frame slot or not the one region
    /// <see cref="Regions"/> states.</exception>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: groups);

        if (built is not Built objects) {
            built?.Dispose();

            throw new ArgumentException(
                message: "The overlay package was handed another package's build.",
                paramName: nameof(built)
            );
        }

        OverlayFrameComposer composer;

        try {
            composer = new OverlayFrameComposer(
                capacity: capacity,
                frameSources: frameSources,
                glyphs: glyphs,
                height: context.Height,
                sources: sources,
                theme: in m_theme,
                width: context.Width
            );
        } catch {
            objects.Dispose();

            throw;
        }

        return new Recorder(
            built: objects,
            composer: composer,
            context: context,
            groups: groups,
            package: this
        );
    }
    /// <inheritdoc/>
    /// <remarks>The overlay's one region is the storage buffer its shader reads, <c>data</c>, of
    /// <see cref="OverlayFrameBuilder.WordCountOf"/> words, which depends on the glyph pack alone.</remarks>
    public IReadOnlyList<RenderGraphPackageRegion> Regions(RenderGraphPackageRecorderContext context) => [new RenderGraphPackageRegion(
        ByteCount: (OverlayFrameBuilder.WordCountOf(glyphs: glyphs) * sizeof(uint)),
        Name: "data"
    )];
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
        // The storage buffer the shader reads, the instance's region: the static prefix (the token slab and the glyph
        // pack), then the frame's records at the builder's own bases, so the pass block's bases are the same every frame.
        private readonly GpuRegion m_data;
        private readonly uint[] m_frameSlots = new uint[RenderGraphPackageCatalog.OverlayFrameSlotCount];
        // A framebuffer over each image the output can be, created at install, so a recording creates nothing.
        private readonly Dictionary<nint, IGpuFramebuffer> m_framebuffers;
        private readonly IGpuBuffer m_geometry;
        private readonly OverlayPackage m_package;
        private readonly nint m_sampler;
        private readonly GpuDeviceServices m_services;
        private readonly RenderGraphPackageSets m_sets;
        private readonly uint m_source;
        private readonly int m_values;

        private bool m_disposed;
        private int m_themeRevision;

        public Recorder(RenderGraphPackageRecorderContext context, Built built, OverlayFrameComposer composer, RenderGraphPackageGroups groups, OverlayPackage package) {
            var inFlight = context.InFlightFrames;
            var builder = composer.Builder;
            var parameters = context.Parameters;

            m_built = built;
            m_composer = composer;
            if (
                (groups.Regions is not [var data]) ||
                (data.ByteCount != (builder.WordCount * sizeof(uint)))
            ) {
                built.Dispose();

                throw new ArgumentException(
                    message: "The overlay records into the one region it states.",
                    paramName: nameof(groups)
                );
            }

            m_data = data;
            m_framebuffers = new Dictionary<nint, IGpuFramebuffer>(capacity: inFlight);
            m_package = package;
            m_services = context.Services;
            m_themeRevision = package.m_themeRevision;
            // The three values lie one after another in the pass block, each a float4 row, as WritePassValues writes them.
            m_values = ((int)parameters.BlockOffsetOf(member: "counts"));

            if (
                (parameters.BlockOffsetOf(member: "misc") != (m_values + 16)) ||
                (parameters.BlockOffsetOf(member: "sdf") != (m_values + 32))
            ) {
                built.Dispose();

                throw new InvalidDataException(message: "The overlay's pass block does not hold counts, misc and sdf as three consecutive float4 rows.");
            }

            try {
                // The vertex buffer comes from the device's own factory, as a shader pass's geometry does.
                m_geometry = context.Device.Services.BufferFactory.CreateHostVisible(
                    name: new GpuObjectName(owner: context.Instance, part: context.Pass, detail: "geometry"),
                    data: FullscreenTriangle.CreateVertexData(),
                    usage: GpuBufferUsage.Vertex
                );
                m_sets = new RenderGraphPackageSets(
                    context: context,
                    groupLayoutHandles: built.Pipeline!.GroupLayoutHandles,
                    groups: groups
                );
                m_source = m_sets.BindingOf(member: RenderGraphPackageCatalog.OverlaySource);

                for (var slot = 0; (slot < m_frameSlots.Length); slot++) {
                    m_frameSlots[slot] = m_sets.BindingOf(member: RenderGraphPackageCatalog.OverlayFrameSlot(slot: slot));
                }

                foreach (var image in groups.OutputImages[0]) {
                    if (!m_framebuffers.ContainsKey(key: image.ImageHandle)) {
                        m_framebuffers.Add(
                            key: image.ImageHandle,
                            value: m_services.RenderPassFactory.CreateFramebuffer(
                                colors: [image],
                                depth: null,
                                renderPass: built.RenderPass!
                            )
                        );
                    }
                }

                m_sampler = m_services.Bindings.CreateSampler();
                // The token slab and the glyph pack are static, so the region takes them once.
                _ = m_data.Write(
                    bytes: MemoryMarshal.AsBytes(span: builder.Scratch[..builder.PanelBaseWords]),
                    offset: 0
                );

                for (var slot = 0; (slot < inFlight); slot++) {
                    m_services.Bindings.WriteSampler(
                        arrayElement: 0,
                        binding: m_sets.BindingOf(member: RenderGraphPackageCatalog.OverlaySampler),
                        descriptorSetHandle: m_sets.PassSet(slot: slot),
                        samplerHandle: m_sampler
                    );
                    // A slot's buffer is the same for the region's life: its own under a ring, the one destination staged.
                    m_services.Bindings.WriteBuffer(
                        binding: m_sets.BindingOf(member: RenderGraphPackageCatalog.OverlayData),
                        bufferHandle: m_data.Buffer(slot: slot).BufferHandle,
                        bufferSize: ((ulong)m_data.ByteCount),
                        descriptorSetHandle: m_sets.PassSet(slot: slot),
                        elementStride: 0,
                        kind: GpuBindingKind.ReadOnlyBuffer
                    );
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

            // A lease the table still holds was bound by a recording that threw before handing it to its frame, so no
            // submission samples it.
            m_composer.FrameSlots.RetireAll();
            foreach (var framebuffer in m_framebuffers.Values) {
                framebuffer.Dispose();
            }

            m_framebuffers.Clear();

            if (m_sampler != 0) {
                m_services.Bindings.DestroySampler(samplerHandle: m_sampler);
            }

            m_geometry?.Dispose();
            m_built.Dispose();
        }
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            if (m_themeRevision != m_package.m_themeRevision) {
                m_themeRevision = m_package.m_themeRevision;
                m_composer.UpdateTheme(theme: in m_package.m_theme);
                _ = m_data.Write(
                    bytes: MemoryMarshal.AsBytes(span: m_composer.Builder.Scratch[..OverlayTokenBlock.WordCount]),
                    offset: 0
                );
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
            var framebuffer = (m_framebuffers.GetValueOrDefault(key: target.ImageHandle) ?? throw new InvalidDataException(message: $"Output '{output.Version}' is an image the overlay was not installed with."));

            WriteImages(
                set: m_sets.PassSet(slot: recording.Slot),
                worldView: input.Image.ImageViewHandle
            );
            m_composer.WritePassValues(values: recording.PassBlock.Slice(
                length: OverlayFrameComposer.PassValueBytes,
                start: m_values
            ));
            m_composer.UploadFrameRegions(buffer: m_data);
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
            m_sets.Bind(
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
            m_composer.FrameSlots.MoveTo(destination: recording.Leases);

            return RenderGraphPackageOutcome.Drew;
        }

        // Writes the slot's images: the world image, and each frame slot's bound lease or, unbound, the world image, so
        // every image the shader can reach is valid.
        private void WriteImages(nint set, nint worldView) {
            var slots = m_composer.FrameSlots;
            var boundCount = slots.BoundCount;

            m_services.Bindings.WriteSampledImage(
                arrayElement: 0,
                binding: m_source,
                descriptorSetHandle: set,
                imageViewHandle: worldView
            );

            for (var slot = 0; (slot < m_frameSlots.Length); slot++) {
                m_services.Bindings.WriteSampledImage(
                    arrayElement: 0,
                    binding: m_frameSlots[slot],
                    descriptorSetHandle: set,
                    imageViewHandle: ((slot < boundCount)
                        ? slots.LeaseAt(slot: slot).ImageViewHandle
                        : worldView)
                );
            }
        }
    }
}
