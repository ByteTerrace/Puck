using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Where a host shows one <c>place</c> pass's source this frame.</summary>
/// <param name="Shown">Whether the source shows at all; a pass whose source is not shown draws nothing, and its output
/// stands for its base.</param>
/// <param name="Left">The rect's left edge, as a fraction of the output's width.</param>
/// <param name="Top">The rect's top edge, as a fraction of the output's height.</param>
/// <param name="Width">The rect's width, as a fraction of the output's width.</param>
/// <param name="Height">The rect's height, as a fraction of the output's height.</param>
/// <param name="Sharpness">The reconstruction's sharpness, from 0 (bilinear) to 1 (clamped Catmull-Rom).</param>
public readonly record struct RenderGraphPlacement(bool Shown, float Left, float Top, float Width, float Height, float Sharpness);
/// <summary>Answers where a host shows each <c>place</c> pass's source this frame. The recorder asks once per recorded
/// frame, on the frame thread.</summary>
public interface IRenderGraphPlacements {
    /// <summary>Returns where the host shows one pass's source this frame.</summary>
    /// <param name="instance">The instance whose graph holds the pass.</param>
    /// <param name="pass">The pass's name.</param>
    /// <param name="placement">The placement, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the host places nothing for the pass, which then draws its bound config's
    /// rect and sharpness.</returns>
    bool TryGet(string instance, string pass, out RenderGraphPlacement placement);
}
/// <summary>The <c>place</c> package (<see cref="RenderGraphPackageCatalog.Place"/>): one compute dispatch of the
/// build-compiled <c>place.comp</c> kernel over the output's extent, writing the base outside a destination rect, or the
/// letterbox color when the config's <see cref="RenderGraphPackageCatalog.PlaceLetterbox"/> is set, and the source
/// reconstructed inside it.
/// <para>
/// The kernel reads the frame group and a pass group holding the extent, the config (the letterbox switch, the rect and
/// the sharpness) and the images the catalog declares (<see cref="RenderGraphPackageCatalog.PlaceMembers"/>). A host
/// that places the source per frame (<see cref="IRenderGraphPlacements"/>) overrides the rect and sharpness in the pass
/// block without rebinding anything, and one that shows the source nowhere this frame has the pass draw nothing, so the
/// output stands for the base; when the pass may not stand in, it copies the base everywhere, letterbox or not. Its build creates the shader module and the compute pipeline on the thread pool; its recorder allocates its
/// sets from the instance's pool and one sampler, which the kernel never reads through but its interface binds. Its
/// ports are compute reads and a compute write, so the node's planned barriers leave the inputs shader-readable and the
/// output in the storage layout; it records no barrier.</para>
/// </summary>
public sealed class PlacePackage : IRenderGraphPackageFactory {
    /// <summary>The file stem of the deployed kernel beside <c>Assets/Shaders/Graph</c>, completed by the backend's
    /// extension.</summary>
    public const string KernelStem = "place.comp";

    private const uint GroupSize = 8;

    private readonly string m_directory;
    private readonly IRenderGraphPlacements? m_placements;

    /// <summary>Initializes a new instance of the <see cref="PlacePackage"/> class.</summary>
    /// <param name="placements">Where the host shows each pass's source per frame, or <see langword="null"/> for a host
    /// whose place passes draw their bound config.</param>
    /// <param name="directory">The directory holding the deployed kernel, or <see langword="null"/> for
    /// <c>Assets/Shaders/Graph</c> beside the executable.</param>
    public PlacePackage(IRenderGraphPlacements? placements = null, string? directory = null) {
        m_directory = (directory ?? Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders",
            path4: "Graph"
        ));
        m_placements = placements;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The pass does not read two images and write one.</exception>
    /// <exception cref="IOException">The deployed kernel is missing or cannot be read.</exception>
    public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (
            (context.Inputs.Count != 2) ||
            (context.Outputs.Count != 1) ||
            context.Inputs.Concat(second: context.Outputs).Any(predicate: static resource => (resource.Kind != ShaderPipelineResourceKind.Image))
        ) {
            throw new InvalidDataException(message: $"Pass '{context.Pass}' of package '{RenderGraphPackageCatalog.Place}' must read two images and write one.");
        }

        var bytecode = File.ReadAllBytes(path: Path.Combine(
            path1: m_directory,
            path2: (KernelStem + (context.HostsOnDirectX
                ? ".dxil"
                : ".spv"))
        ));
        var built = new Built();

        try {
            cancellationToken.ThrowIfCancellationRequested();
            built.Module = context.Services.ShaderModuleFactory.Create(
                bytecode: bytecode,
                stage: GpuShaderStage.Compute
            );
            cancellationToken.ThrowIfCancellationRequested();
            built.Pipeline = context.Services.PipelineFactory.Create(
                computeShaderModule: built.Module,
                description: new GpuComputePipelineDescription(
                    Bindings: [],
                    Layout: context.Parameters.Layout.PipelineLayout(
                        pushesIndex: false,
                        stages: GpuShaderStage.Compute
                    ),
                    Name: RenderGraphPackageCatalog.Place,
                    PushConstantBinding: null
                ),
                name: new GpuObjectName(
                    owner: context.Instance,
                    part: context.Pass
                )
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
    /// <paramref name="groups"/> holds no pool or no block buffer per frame slot.</exception>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: groups);

        if (built is not Built objects) {
            built?.Dispose();

            throw new ArgumentException(
                message: $"Package '{RenderGraphPackageCatalog.Place}' was handed another package's build.",
                paramName: nameof(built)
            );
        }

        return new Recorder(
            built: objects,
            context: context,
            groups: groups,
            placements: m_placements
        );
    }

    // The module and pipeline one pass's build creates, which its recorder owns once created.
    private sealed class Built : IDisposable {
        public IGpuShaderModule? Module;
        public IGpuComputePipeline? Pipeline;

        public void Dispose() {
            Pipeline?.Dispose();
            Pipeline = null;
            Module?.Dispose();
            Module = null;
        }
    }
    // Records one place pass. Everything a frame slot binds is per slot, since the instance waits only that slot's
    // previous submission before recording into it.
    private sealed class Recorder : IRenderGraphPackageRecorder {
        private readonly uint m_base;
        private readonly Built m_built;
        private readonly uint m_destination;
        private readonly string m_instance;
        private readonly string m_pass;
        private readonly IRenderGraphPlacements? m_placements;
        private readonly int m_letterboxOffset;
        private readonly int m_rectOffset;
        private readonly GpuDeviceServices m_services;
        private readonly RenderGraphPackageSets m_sets = null!;
        private readonly int m_sharpnessOffset;
        private readonly uint m_source;

        private bool m_disposed;
        private nint m_sampler;

        public Recorder(RenderGraphPackageRecorderContext context, Built built, RenderGraphPackageGroups groups, IRenderGraphPlacements? placements) {
            var parameters = context.Parameters;

            m_built = built;
            m_instance = context.Instance;
            m_pass = context.Pass;
            m_placements = placements;
            m_services = context.Services;

            try {
                m_letterboxOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceLetterbox));
                m_rectOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceRect));
                m_sharpnessOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceSharpness));
                m_sets = new RenderGraphPackageSets(
                    context: context,
                    groupLayoutHandles: built.Pipeline!.GroupLayoutHandles,
                    groups: groups
                );
                m_base = m_sets.BindingOf(member: RenderGraphPackageCatalog.PlaceBase);
                m_source = m_sets.BindingOf(member: RenderGraphPackageCatalog.PlaceSource);
                m_destination = m_sets.BindingOf(member: RenderGraphPackageCatalog.PlaceDestination);
                m_sampler = m_services.Bindings.CreateSampler();

                for (var slot = 0; (slot < context.InFlightFrames); slot++) {
                    foreach (var image in ((ReadOnlySpan<string>)[RenderGraphPackageCatalog.PlaceBase, RenderGraphPackageCatalog.PlaceSource])) {
                        m_services.Bindings.WriteSampler(
                            arrayElement: 0,
                            binding: m_sets.BindingOf(member: (image + ShaderPipelinePassPorts.SamplerSuffix)),
                            descriptorSetHandle: m_sets.PassSet(slot: slot),
                            samplerHandle: m_sampler
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

            if (m_sampler != 0) {
                m_services.Bindings.DestroySampler(samplerHandle: m_sampler);
                m_sampler = 0;
            }

            m_built.Dispose();
        }
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            if (
                (m_placements is not null) &&
                m_placements.TryGet(
                    instance: m_instance,
                    pass: m_pass,
                    placement: out var placement
                )
            ) {
                if (
                    !placement.Shown &&
                    recording.MayStandIn
                ) {
                    return RenderGraphPackageOutcome.DrewNothing;
                }

                var rect = recording.PassBlock[m_rectOffset..];

                BinaryPrimitives.WriteSingleLittleEndian(destination: rect, value: (placement.Shown ? placement.Left : 0f));
                BinaryPrimitives.WriteSingleLittleEndian(destination: rect[4..], value: (placement.Shown ? placement.Top : 0f));
                BinaryPrimitives.WriteSingleLittleEndian(destination: rect[8..], value: (placement.Shown ? placement.Width : 0f));
                BinaryPrimitives.WriteSingleLittleEndian(destination: rect[12..], value: (placement.Shown ? placement.Height : 0f));
                BinaryPrimitives.WriteSingleLittleEndian(
                    destination: recording.PassBlock[m_sharpnessOffset..],
                    value: placement.Sharpness
                );

                // A source shown nowhere that must still draw copies its base everywhere, letterboxed or not.
                if (!placement.Shown) {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination: recording.PassBlock[m_letterboxOffset..],
                        value: 0u
                    );
                }
            }

            var command = recording.CommandBuffer;
            var recorder = recording.Recorder;
            var pipeline = m_built.Pipeline!;
            var set = m_sets.PassSet(slot: recording.Slot);

            m_services.Bindings.WriteStorageImage(
                arrayElement: 0,
                binding: m_destination,
                descriptorSetHandle: set,
                imageViewHandle: recording.Outputs[0].Image.ImageViewHandle
            );
            m_services.Bindings.WriteSampledImage(
                arrayElement: 0,
                binding: m_base,
                descriptorSetHandle: set,
                imageViewHandle: recording.Inputs[0].Image.ImageViewHandle
            );
            m_services.Bindings.WriteSampledImage(
                arrayElement: 0,
                binding: m_source,
                descriptorSetHandle: set,
                imageViewHandle: recording.Inputs[1].Image.ImageViewHandle
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: command,
                pipelineHandle: pipeline.Handle
            );
            m_sets.Bind(
                bindPoint: GpuBindPoint.Compute,
                commandBuffer: command,
                pipelineLayout: pipeline.LayoutHandle,
                recorder: recorder,
                slot: recording.Slot
            );
            recorder.Dispatch(
                commandBufferHandle: command,
                groupCountX: (((recording.Width + GroupSize) - 1) / GroupSize),
                groupCountY: (((recording.Height + GroupSize) - 1) / GroupSize),
                groupCountZ: 1
            );

            return RenderGraphPackageOutcome.Drew;
        }
    }
}
