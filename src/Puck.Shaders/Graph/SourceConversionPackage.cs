using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>A conversion package (<see cref="RenderGraphPackageCatalog.SourceConversions"/>): one compute dispatch of the
/// build-compiled kernel its id names, from <c>Assets/Shaders/Sources</c>, over the uploaded source's extent, reading the
/// region bound to its input port and writing the image bound to its output.
/// <para>
/// The kernel reads the pass group the catalog declares (<see cref="RenderGraphPackageCatalog.SourceMembers"/>): the
/// paper-white level in its pass block, which the recorder writes every frame, the region at binding 1, the image at
/// binding 2 and the work counters at binding 3 of set 3, eight by eight threads a group, one thread a pixel, each pixel it
/// writes counting one texel (<see cref="RenderGraphPackageWorkCounters"/>). Its build
/// leases the compute pipeline, one for every conversion of its kind on the device, from the pass-pipeline cache on the
/// thread pool; its recorder allocates its sets from the
/// instance's pool. Its ports are a compute read and a compute write, so the node's planned barriers leave the region
/// readable and the image in the storage layout; it records no barrier.</para>
/// </summary>
public sealed class SourceConversionPackage : IRenderGraphPackageFactory {
    private const uint GroupSize = 8;

    private readonly string m_directory;
    private readonly string m_package;
    private readonly float m_paperWhiteNits;

    /// <summary>Initializes a new instance of the <see cref="SourceConversionPackage"/> class.</summary>
    /// <param name="package">The conversion package's id, one of <see cref="RenderGraphPackageCatalog.SourceConversions"/>,
    /// which is also the stem of its kernel's file.</param>
    /// <param name="directory">The directory holding the deployed kernels, or <see langword="null"/> for
    /// <c>Assets/Shaders/Sources</c> beside the executable.</param>
    /// <param name="paperWhiteNits">The paper-white level, in cd/m², a working value of one shows at: the host's
    /// (<c>PresentationOptions.PaperWhiteNits</c>), which an HDR source's luminance is converted relative to.</param>
    /// <exception cref="ArgumentException"><paramref name="package"/> names no conversion package.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paperWhiteNits"/> is outside the range
    /// <see cref="DisplayOutput.RequirePaperWhite"/> accepts.</exception>
    public SourceConversionPackage(string package, string? directory = null, double paperWhiteNits = DisplayOutput.SdrWhiteNits) {
        if (!RenderGraphPackageCatalog.SourceConversions.Contains(value: package)) {
            throw new ArgumentException(
                message: $"'{package}' is not a conversion package.",
                paramName: nameof(package)
            );
        }

        m_paperWhiteNits = ((float)DisplayOutput.RequirePaperWhite(nits: paperWhiteNits));

        m_directory = (directory ?? Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "Shaders",
            path4: "Sources"
        ));
        m_package = package;
    }

    /// <summary>Registers one conversion package per pass <see cref="RenderGraphPackageCatalog.SourceConversions"/> names
    /// with a host's packages.</summary>
    /// <param name="packages">The host's packages.</param>
    /// <param name="directory">The directory holding the deployed kernels, or <see langword="null"/> for
    /// <c>Assets/Shaders/Sources</c> beside the executable.</param>
    /// <param name="paperWhiteNits">The paper-white level, in cd/m², every conversion writes relative to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A conversion package already has a recorder.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paperWhiteNits"/> is outside the range
    /// <see cref="DisplayOutput.RequirePaperWhite"/> accepts.</exception>
    public static void RegisterAll(RenderGraphPackageRecorders packages, string? directory = null, double paperWhiteNits = DisplayOutput.SdrWhiteNits) {
        ArgumentNullException.ThrowIfNull(argument: packages);

        foreach (var package in RenderGraphPackageCatalog.SourceConversions) {
            packages.Register(
                factory: new SourceConversionPackage(
                    directory: directory,
                    package: package,
                    paperWhiteNits: paperWhiteNits
                ),
                package: package
            );
        }
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The pass does not read one buffer and write one image.</exception>
    /// <exception cref="IOException">The deployed kernel is missing or cannot be read.</exception>
    public async ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (
            (context.Inputs.Count != 1) ||
            (context.Outputs.Count != 1) ||
            (context.Inputs[0].Kind != ShaderPipelineResourceKind.Buffer) ||
            (context.Outputs[0].Kind != ShaderPipelineResourceKind.Image)
        ) {
            throw new InvalidDataException(message: $"Pass '{context.Pass}' of package '{m_package}' must read one buffer and write one image.");
        }

        var bytecode = File.ReadAllBytes(path: Path.Combine(
            path1: m_directory,
            path2: (m_package + (context.HostsOnDirectX
                ? ".comp.dxil"
                : ".comp.spv"))
        ));
        var lease = context.Pipelines.Acquire(
            device: context.Device,
            key: GpuPassPipelineKey.OfCompute(
                bytecode: bytecode,
                description: new GpuComputePipelineDescription(
                    Bindings: [],
                    Layout: context.Parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute),
                    Name: m_package,
                    PushConstantBinding: null
                )
            )
        );

        try {
            _ = await lease.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        } catch {
            lease.Release();

            throw;
        }

        return new Built(lease: lease);
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="groups"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="built"/> is not this package's build.</exception>
    public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: groups);

        if (built is not Built objects) {
            built?.Dispose();

            throw new ArgumentException(
                message: $"Package '{m_package}' was handed another package's build.",
                paramName: nameof(built)
            );
        }

        return new Recorder(
            built: objects,
            context: context,
            groups: groups,
            paperWhiteNits: m_paperWhiteNits
        );
    }

    // The module and pipeline one pass's build creates, which its recorder owns once created.
    private sealed class Built(GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> lease) : IDisposable {
        public IGpuComputePipeline Pipeline { get; } = lease.Current!.Compute!;

        public void Dispose() =>
            lease.Release();
    }
    // Records one conversion. The pass set is per frame slot, since the instance waits only that slot's previous
    // submission before recording into it, and the region's buffer is the slot's own.
    private sealed class Recorder : IRenderGraphPackageRecorder {
        private readonly Built m_built;
        private readonly uint m_image;
        private readonly float m_paperWhiteNits;
        private readonly int m_paperWhiteOffset;
        private readonly uint m_region;
        private readonly GpuDeviceServices m_services;
        private readonly RenderGraphPackageSets m_sets = null!;
        private readonly RenderGraphPackageWorkCounters m_workCounters = null!;

        private bool m_disposed;

        public Recorder(RenderGraphPackageRecorderContext context, Built built, RenderGraphPackageGroups groups, float paperWhiteNits) {
            m_built = built;
            m_paperWhiteNits = paperWhiteNits;
            m_paperWhiteOffset = ((int)context.Parameters.BlockOffsetOf(member: RenderGraphPackageCatalog.SourcePaperWhite));
            m_services = context.Services;

            try {
                m_sets = new RenderGraphPackageSets(
                    context: context,
                    groupLayoutHandles: built.Pipeline.GroupLayoutHandles,
                    groups: groups
                );
                m_region = m_sets.BindingOf(member: RenderGraphPackageCatalog.SourceRegion);
                m_image = m_sets.BindingOf(member: RenderGraphPackageCatalog.SourceImage);
                m_workCounters = new RenderGraphPackageWorkCounters(
                    context: context,
                    sets: m_sets
                );
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
            m_built.Dispose();
        }
        public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
            var command = recording.CommandBuffer;
            var recorder = recording.Recorder;
            var pipeline = m_built.Pipeline;
            var set = m_sets.PassSet(slot: recording.Slot);
            var region = recording.Inputs[0].Buffer!;

            BinaryPrimitives.WriteSingleLittleEndian(
                destination: recording.PassBlock[m_paperWhiteOffset..],
                value: m_paperWhiteNits
            );
            m_workCounters.Write(
                passSet: set,
                recording: recording
            );
            m_services.Bindings.WriteBuffer(
                binding: m_region,
                bufferHandle: region.BufferHandle,
                bufferSize: region.SizeBytes,
                descriptorSetHandle: set,
                elementStride: 0U,
                kind: GpuBindingKind.ReadOnlyBuffer
            );
            m_services.Bindings.WriteStorageImage(
                arrayElement: 0,
                binding: m_image,
                descriptorSetHandle: set,
                imageViewHandle: recording.Outputs[0].Image.ImageViewHandle
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
