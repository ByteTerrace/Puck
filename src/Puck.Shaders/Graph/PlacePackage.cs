using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Maths;

namespace Puck.Shaders;

/// <summary>Where a host shows one <c>place</c> pass's source this frame.</summary>
/// <param name="Shown">Whether the source shows at all; a pass whose source is not shown draws nothing, and its output
/// stands for its base.</param>
/// <param name="Left">The rect's left edge, as a fraction of the output's width.</param>
/// <param name="Top">The rect's top edge, as a fraction of the output's height.</param>
/// <param name="Width">The rect's width, as a fraction of the output's width.</param>
/// <param name="Height">The rect's height, as a fraction of the output's height.</param>
/// <param name="Sharpness">The reconstruction's sharpness, from 0 (bilinear) to 1 (clamped Catmull-Rom).</param>
/// <param name="Uncovered">Whether some of the output lies outside every rect the host shows this frame, so those
/// pixels owe the letterbox. A pass whose config sets <see cref="RenderGraphPackageCatalog.PlaceLetterbox"/> and whose
/// source is not shown then writes the letterbox color everywhere, so pixels no later pass covers show it rather than
/// the base; over a covered output it stands for its base as any unshown pass does.</param>
/// <param name="LetterboxColor">The working-image RGBA color the pass writes outside its rect when it letterboxes
/// (<see cref="RenderGraphPackageCatalog.PlaceLetterboxColor"/>), or <see langword="null"/> for
/// <see cref="RenderGraphPackageCatalog.DefaultLetterboxColor"/>.</param>
/// <param name="Crop">The part of the source the rect shows, as fractions of the source's extent: left, top, width and
/// height in <c>X</c>, <c>Y</c>, <c>Z</c> and <c>W</c> (<see cref="RenderGraphPackageCatalog.PlaceCrop"/>); a zero
/// width or height shows the whole source. A crop whose pixels match the rect's is an exact copy of them.</param>
public readonly record struct RenderGraphPlacement(bool Shown, float Left, float Top, float Width, float Height, float Sharpness, bool Uncovered = false, System.Numerics.Vector4? LetterboxColor = null, System.Numerics.Vector4 Crop = default);
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
/// <summary>The placement packages (<see cref="RenderGraphPackageCatalog.Place"/>, one source, and
/// <see cref="RenderGraphPackageCatalog.PlaceViews"/>, <see cref="RenderGraphPackageCatalog.PlaceSourceCount"/> of them):
/// one compute dispatch of the build-compiled placement kernel over the output's extent, writing the base outside every
/// destination rect, or the letterbox color when the config's <see cref="RenderGraphPackageCatalog.PlaceLetterbox"/> is
/// set, and each source reconstructed inside its own, a later source over an earlier one.
/// <para>
/// The kernel reads the frame group and a pass group holding the extent, the config (the letterbox switch and color, the
/// rect, the crop and the sharpness of a pass no host places), each source's rect, crop and sharpness, which the
/// recorder writes each frame, and the images the catalog declares
/// (<see cref="RenderGraphPackageCatalog.PlaceMembers"/>). A host that places sources per frame
/// (<see cref="IRenderGraphPlacements"/>) is asked for each source by the name of the version bound to its port, and
/// sets its rect, sharpness and crop and the letterbox color in the pass block without rebinding anything. When the host
/// shows no source this frame the pass draws nothing, so the output stands for the base; when the pass may not stand in,
/// it copies the base everywhere. A letterboxing pass the host places writes the letterbox color outside every rect
/// only while its first source is shown or the host says part of the output is uncovered
/// (<see cref="RenderGraphPlacement.Uncovered"/>), and then even with nothing shown; otherwise the base shows there. Its build leases the compute pipeline, one for every place
/// pass on the device, from the pass-pipeline cache on the thread pool; its recorder allocates its sets from the instance's pool and one sampler, which the kernel never reads through but its interface binds. Its
/// ports are compute reads and a compute write, so the node's planned barriers leave the inputs shader-readable and the
/// output in the storage layout; it records no barrier.</para>
/// </summary>
public sealed class PlacePackage : IRenderGraphPackageFactory {
    /// <summary>The file stem of <see cref="RenderGraphPackageCatalog.Place"/>'s deployed kernel beside
    /// <c>Assets/Shaders/Graph</c>, completed by the backend's extension.</summary>
    public const string KernelStem = "place.comp";
    /// <summary>The file stem of <see cref="RenderGraphPackageCatalog.PlaceViews"/>'s deployed kernel, the same body
    /// under that package's interface.</summary>
    public const string ViewsKernelStem = "place-views.comp";

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
    /// <exception cref="InvalidDataException">The pass does not read a base image and its package's sources and
    /// write one image.</exception>
    /// <exception cref="IOException">The deployed kernel is missing or cannot be read.</exception>
    public async ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var views = string.Equals(a: context.Package, b: RenderGraphPackageCatalog.PlaceViews, comparisonType: StringComparison.Ordinal);
        var sources = (views ? RenderGraphPackageCatalog.PlaceSourceCount : 1);

        if (
            (context.Inputs.Count < 2) ||
            (context.Inputs.Count > (1 + sources)) ||
            (context.Outputs.Count != 1) ||
            context.Inputs.Concat(second: context.Outputs).Any(predicate: static resource => (resource.Kind != ShaderPipelineResourceKind.Image))
        ) {
            throw new InvalidDataException(message: $"Pass '{context.Pass}' of package '{context.Package}' must read a base image and from one to {sources} source image(s) and write one.");
        }

        var bytecode = File.ReadAllBytes(path: Path.Combine(
            path1: m_directory,
            path2: ((views ? ViewsKernelStem : KernelStem) + ShaderBytecode.FileExtension(hostsOnDirectX: context.HostsOnDirectX))
        ));

        return await PassPipelineBuild.AcquireAsync(
            cancellationToken: cancellationToken,
            context: context,
            key: GpuPassPipelineKey.OfCompute(
                bytecode: bytecode,
                description: new GpuComputePipelineDescription(
                    Bindings: [],
                    Layout: context.Parameters.Layout.PipelineLayout(
                        stages: GpuShaderStage.Compute
                    ),
                    Name: context.Package,
                    PushConstantBinding: null
                )
            ),
            package: context.Package
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="groups"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="built"/> is not this package's build, or
    /// <paramref name="groups"/> holds no pool or no block buffer per frame slot.</exception>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: groups);

        var objects = PassPipelineBuild.Of(
            built: built,
            package: context.Package
        );

        return new Recorder(
            built: objects,
            context: context,
            groups: groups,
            placements: m_placements
        );
    }

    // Records one placement pass. Everything a frame slot binds is per slot, since the instance waits only that slot's
    // previous submission before recording into it.
    private sealed class Recorder : IRenderGraphPackageRecorder {
        private readonly uint m_base;
        private readonly PassPipelineBuild m_built;
        private readonly uint m_destination;
        private readonly string m_instance;
        // The name each source is placed by: the version bound to its port.
        private readonly string[] m_names;
        private readonly IRenderGraphPlacements? m_placements;
        private readonly int m_letterboxOffset;
        private readonly int m_letterboxColorOffset;
        private readonly int m_cropOffset;
        private readonly int m_cropsOffset;
        private readonly int m_rectOffset;
        private readonly int m_rectsOffset;
        private readonly GpuDeviceServices m_services;
        private readonly RenderGraphPackageSets m_sets = null!;
        private readonly int m_sharpnessOffset;
        private readonly int m_sharpnessesOffset;
        private readonly uint m_source;
        private readonly int m_sourcesOffset;
        private readonly RenderGraphPackageWorkCounters m_workCounters = null!;

        private bool m_disposed;
        private nint m_sampler;

        public Recorder(RenderGraphPackageRecorderContext context, PassPipelineBuild built, RenderGraphPackageGroups groups, IRenderGraphPlacements? placements) {
            var parameters = context.Parameters;

            m_built = built;
            m_instance = context.Instance;
            m_names = [.. context.Inputs.Skip(count: 1).Select(selector: static input => input.Name)];
            m_placements = placements;
            m_services = context.Services;

            try {
                m_letterboxOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceLetterbox));
                m_letterboxColorOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceLetterboxColor));
                m_cropOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceCrop));
                m_cropsOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceCrops));
                m_rectOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceRect));
                m_rectsOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceRects));
                m_sharpnessOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceSharpness));
                m_sharpnessesOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceSharpnesses));
                m_sourcesOffset = ((int)parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceSources));
                m_sets = new RenderGraphPackageSets(
                    context: context,
                    groupLayoutHandles: built.Pipeline.GroupLayoutHandles,
                    groups: groups
                );
                m_base = m_sets.BindingOf(member: RenderGraphPackageCatalog.PlaceBase);
                m_source = m_sets.BindingOf(member: RenderGraphPackageCatalog.PlaceSource);
                m_destination = m_sets.BindingOf(member: RenderGraphPackageCatalog.PlaceDestination);
                m_workCounters = new RenderGraphPackageWorkCounters(
                    context: context,
                    sets: m_sets
                );
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

        // What the host places this frame: whether it places the pass's sources at all (a pass it does not place draws
        // its config), whether any source is shown, and whether a letterboxing pass writes the letterbox outside every
        // rect: where its first source is shown, which is placed over nothing the display shows, or where the host says
        // part of the output lies outside everything shown. Otherwise the base shows there, as it does behind a pane
        // placed over a view that stands for the world.
        private bool TryPlaced(ReadOnlySpan<byte> passBlock, out bool shown, out bool letterboxes) {
            shown = false;
            letterboxes = false;
            if (m_placements is null) {
                return false;
            }

            var placed = false;
            var uncovered = false;
            var firstShown = false;

            for (var source = 0; (source < m_names.Length); source++) {
                if (!m_placements.TryGet(instance: m_instance, pass: m_names[source], placement: out var placement)) { continue; }
                placed = true;
                shown |= placement.Shown;
                uncovered |= placement.Uncovered;
                firstShown |= ((source == 0) && placement.Shown);
            }
            letterboxes = ((uncovered || firstShown) && (BinaryPrimitives.ReadUInt32LittleEndian(source: passBlock[m_letterboxOffset..]) != 0u));

            return placed;
        }
        private static void WriteVector(Span<byte> destination, System.Numerics.Vector4 value) {
            BinaryPrimitives.WriteSingleLittleEndian(destination: destination, value: value.X);
            BinaryPrimitives.WriteSingleLittleEndian(destination: destination[4..], value: value.Y);
            BinaryPrimitives.WriteSingleLittleEndian(destination: destination[8..], value: value.Z);
            BinaryPrimitives.WriteSingleLittleEndian(destination: destination[12..], value: value.W);
        }
        private static System.Numerics.Vector4 ReadVector(ReadOnlySpan<byte> source) => new(
            x: BinaryPrimitives.ReadSingleLittleEndian(source: source),
            y: BinaryPrimitives.ReadSingleLittleEndian(source: source[4..]),
            z: BinaryPrimitives.ReadSingleLittleEndian(source: source[8..]),
            w: BinaryPrimitives.ReadSingleLittleEndian(source: source[12..])
        );
        // Writes one source's row of the pass block: its rect, an empty one for a source not shown, its crop and its
        // sharpness.
        private void WriteSource(Span<byte> passBlock, int source, System.Numerics.Vector4 rect, System.Numerics.Vector4 crop, float sharpness) {
            WriteVector(destination: passBlock[(m_rectsOffset + (16 * source))..], value: rect);
            WriteVector(destination: passBlock[(m_cropsOffset + (16 * source))..], value: crop);
            BinaryPrimitives.WriteSingleLittleEndian(destination: passBlock[(m_sharpnessesOffset + (4 * source))..], value: sharpness);
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
        /// <inheritdoc/>
        /// <remarks>A pass none of whose sources is shown draws nothing, unless it owes the letterbox wherever nothing
        /// else shows.</remarks>
        public bool DrawsNothing(ReadOnlySpan<byte> passBlock) =>
            (TryPlaced(passBlock: passBlock, shown: out var shown, letterboxes: out var letterboxes) && !shown && !letterboxes);
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            var whole = new System.Numerics.Vector4(w: 1f, x: 0f, y: 0f, z: 1f);

            if (TryPlaced(passBlock: recording.PassBlock, shown: out var shown, letterboxes: out var letterboxes)) {
                if (!shown && !letterboxes && recording.MayStandIn) {
                    return RenderGraphPackageOutcome.DrewNothing;
                }

                System.Numerics.Vector4? letterboxColor = null;

                for (var source = 0; (source < m_names.Length); source++) {
                    var placement = (m_placements!.TryGet(instance: m_instance, pass: m_names[source], placement: out var found) ? found : default);

                    letterboxColor ??= placement.LetterboxColor;
                    WriteSource(
                        crop: (((placement.Crop.Z > 0f) && (placement.Crop.W > 0f)) ? placement.Crop : whole),
                        passBlock: recording.PassBlock,
                        rect: (placement.Shown ? new System.Numerics.Vector4(w: placement.Height, x: placement.Left, y: placement.Top, z: placement.Width) : default),
                        sharpness: placement.Sharpness,
                        source: source
                    );
                }
                WriteVector(
                    destination: recording.PassBlock[m_letterboxColorOffset..],
                    value: (letterboxColor ?? RenderGraphPackageCatalog.DefaultLetterboxColor)
                );

                // A pass that does not letterbox this frame shows its base outside every rect: everywhere, for one
                // with nothing shown that must still draw.
                if (!letterboxes) {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destination: recording.PassBlock[m_letterboxOffset..],
                        value: 0u
                    );
                }
            } else {
                // A pass no host places draws its bound config: its first source in the config's rect.
                WriteSource(
                    crop: ReadVector(source: recording.PassBlock[m_cropOffset..]),
                    passBlock: recording.PassBlock,
                    rect: ReadVector(source: recording.PassBlock[m_rectOffset..]),
                    sharpness: BinaryPrimitives.ReadSingleLittleEndian(source: recording.PassBlock[m_sharpnessOffset..]),
                    source: 0
                );
                for (var source = 1; (source < m_names.Length); source++) {
                    WriteSource(crop: whole, passBlock: recording.PassBlock, rect: default, sharpness: 0f, source: source);
                }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                destination: recording.PassBlock[m_sourcesOffset..],
                value: (uint)m_names.Length
            );

            var command = recording.CommandBuffer;
            var recorder = recording.Recorder;
            var pipeline = m_built.Pipeline;
            var set = m_sets.PassSet(slot: recording.Slot);

            var destination = recording.Outputs[0].ReadImage(latch: recording.Latch);
            var baseImage = recording.Inputs[0].ReadImage(latch: recording.Latch);

            m_workCounters.Write(
                passSet: set,
                recording: recording
            );
            m_services.Bindings.WriteStorageImage(
                arrayElement: 0,
                binding: m_destination,
                descriptorSetHandle: set,
                imageViewHandle: destination.ImageViewHandle
            );
            m_services.Bindings.WriteSampledImage(
                arrayElement: 0,
                binding: m_base,
                descriptorSetHandle: set,
                imageViewHandle: baseImage.ImageViewHandle
            );
            // Every element of the source array is bound: the pass's sources, then the base, which the kernel never
            // reads there.
            for (var element = 0; (element < RenderGraphPackageCatalog.PlaceSourceCount); element++) {
                m_services.Bindings.WriteSampledImage(
                    arrayElement: (uint)element,
                    binding: m_source,
                    descriptorSetHandle: set,
                    imageViewHandle: ((element < m_names.Length)
                        ? recording.Inputs[(1 + element)].ReadImage(latch: recording.Latch).ImageViewHandle
                        : baseImage.ImageViewHandle)
                );
            }
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
                groupCountX: recording.Width.CeilingDivide(divisor: GroupSize),
                groupCountY: recording.Height.CeilingDivide(divisor: GroupSize),
                groupCountZ: 1
            );

            return RenderGraphPackageOutcome.Drew;
        }
    }
}
