using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>One version a package pass reads or writes, resolved for the frame it records: the image or the buffer
/// that holds it in the frame slot being recorded.</summary>
/// <param name="Version">The version name the pass binds to the port.</param>
/// <param name="Kind">What the version carries.</param>
/// <param name="Image">The image holding an image version, with its extent, its format, and the layout the pass's planned
/// barrier left it in for the recording, the one its port's access needs; default for a buffer.</param>
/// <param name="Buffer">The buffer holding a buffer version, or <see langword="null"/> for an image.</param>
/// <param name="Owned">The instance's own image holding an image version, which a recorder may bind as an attachment, or
/// <see langword="null"/> for a buffer or an external image another producer binds.</param>
public readonly record struct RenderGraphPackageResource(string Version, ShaderPipelineResourceKind Kind, ShaderPipelineExternalImage Image, IGpuBuffer? Buffer, IGpuImage? Owned);
/// <summary>What a package recorder records into for one frame: the command buffer the pass owns in its instance's
/// submission, already begun, with the pass's planned barriers recorded, and the versions bound to its ports.</summary>
/// <param name="CommandBuffer">The command buffer to record into. The instance ends and submits it.</param>
/// <param name="Recorder">The instance's recorder, which counts what the package records under the pass.</param>
/// <param name="Slot">The frame slot being recorded, in [0, the instance's frames in flight). The instance has waited
/// the slot's previous submission, so whatever a recorder keeps per slot is free to rewrite.</param>
/// <param name="Width">The pass's extent width, in pixels: its first output's, else the instance's frame
/// width.</param>
/// <param name="Height">The pass's extent height, in pixels.</param>
/// <param name="Inputs">The versions bound to its input ports, in port order.</param>
/// <param name="Outputs">The versions bound to its output ports, in port order.</param>
/// <param name="PassBlock">The pass's pass block for this frame, laid out by
/// <see cref="RenderGraphPackageRecorderContext.Parameters"/>: the instance has written its extent and bound config, and
/// the recorder writes the values its package declares (<see cref="RenderGraphPackage.Members"/>) at their offsets
/// (<see cref="ShaderPipelineParameterLayout.BlockOffsetOf"/>). The instance uploads the block to the slot's constant
/// buffer once the recording returns.</param>
/// <param name="Leases">The frame's lease list: a lease held in it retires once this frame's submission has finished, on
/// device loss or at disposal, and at once when the frame submits nothing.</param>
/// <param name="Context">The host's frame context the instance renders the frame with.</param>
/// <param name="MayStandIn">Whether the recording may draw nothing and leave each output standing for its input
/// (<see cref="RenderGraphPackageOutcome.DrewNothing"/>). It is <see langword="false"/> when the pass's outputs cannot
/// stand for its inputs, and when an input is a host's image in another layout than the instance publishes in: the
/// instance publishes every image in its output layout, and a host's image is handed back in the host's own, so the
/// recording must draw.</param>
/// <param name="Arguments">The buffer holding an indirect dispatch's group counts, which the pass's planned barrier left
/// in the indirect-argument state, or <see langword="null"/> for a pass that is not dispatched indirectly.</param>
/// <param name="Reads">The latest completed image of each instance the pass's instance reads that its graph binds to no
/// version, such as the sources an SDF view's screens show, or <see langword="null"/> when there is none. A recording
/// takes the lease of each image it samples (<see cref="RenderGraphExternalReads.Take"/>) and holds it in
/// <paramref name="Leases"/>; the runtime retires the rest once the frame is produced. Each image rests in its producer's
/// published layout, shader-readable, and the planner plans no barrier for it.</param>
/// <param name="WorkCounters">Where the pass's kernels count their own work this frame: the frame slot's counter
/// buffer and the pass's row, which the node clears before its first pass and copies after its last, or
/// <see langword="null"/> for a pass whose fragment does not count (<see cref="RenderGraphFragmentPass.CountsKernelWork"/>).
/// A recording binds the buffer read-write at every dispatch and tells its kernels the row; it records no barrier for
/// it, since every pass only adds to it atomically.</param>
/// <param name="FrameWidth">The instance output width; zero uses the pass width for standalone recordings.</param>
/// <param name="FrameHeight">The instance output height; zero uses the pass height for standalone recordings.</param>
/// <param name="RenderWidth">The width of the instance's render grid this frame; zero uses the output width.</param>
/// <param name="RenderHeight">The height of the instance's render grid this frame; zero uses the output height.</param>
public readonly ref struct RenderGraphPackageRecording(nint CommandBuffer, IGpuRecorder Recorder, int Slot, uint Width, uint Height, ReadOnlySpan<RenderGraphPackageResource> Inputs, ReadOnlySpan<RenderGraphPackageResource> Outputs, Span<byte> PassBlock, LeaseRetireList Leases, FrameContext Context, bool MayStandIn, IGpuBuffer? Arguments = null, RenderGraphExternalReads? Reads = null, GpuKernelCounterRow? WorkCounters = null, uint FrameWidth = 0, uint FrameHeight = 0, uint RenderWidth = 0, uint RenderHeight = 0) {
    /// <summary>Gets the command buffer to record into.</summary>
    public nint CommandBuffer { get; } = CommandBuffer;
    /// <summary>Gets the instance's counting recorder.</summary>
    public IGpuRecorder Recorder { get; } = Recorder;
    /// <summary>Gets the frame slot being recorded.</summary>
    public int Slot { get; } = Slot;
    /// <summary>Gets the pass's extent width, in pixels.</summary>
    public uint Width { get; } = Width;
    /// <summary>Gets the pass's extent height, in pixels.</summary>
    public uint Height { get; } = Height;
    /// <summary>Gets the instance output width, independently of this pass's render grid.</summary>
    public uint FrameWidth { get; } = ((FrameWidth == 0) ? Width : FrameWidth);
    /// <summary>Gets the instance output height, independently of this pass's render grid.</summary>
    public uint FrameHeight { get; } = ((FrameHeight == 0) ? Height : FrameHeight);
    /// <summary>Gets the width of the render grid every render-sized pass of the instance records at this frame: the
    /// node's one resolution of it, which a pass of another extent reads rather than resolving the grid again.</summary>
    public uint RenderWidth { get; } = ((RenderWidth != 0) ? RenderWidth : ((FrameWidth == 0) ? Width : FrameWidth));
    /// <summary>Gets the height of the render grid every render-sized pass of the instance records at this
    /// frame.</summary>
    public uint RenderHeight { get; } = ((RenderHeight != 0) ? RenderHeight : ((FrameHeight == 0) ? Height : FrameHeight));
    /// <summary>Gets the versions bound to the input ports.</summary>
    public ReadOnlySpan<RenderGraphPackageResource> Inputs { get; } = Inputs;
    /// <summary>Gets the versions bound to the output ports.</summary>
    public ReadOnlySpan<RenderGraphPackageResource> Outputs { get; } = Outputs;
    /// <summary>Gets the pass's pass block for this frame, which the recorder writes its declared values into.</summary>
    public Span<byte> PassBlock { get; } = PassBlock;
    /// <summary>Gets the frame's lease list.</summary>
    public LeaseRetireList Leases { get; } = Leases;
    /// <summary>Gets the host's frame context.</summary>
    public FrameContext Context { get; } = Context;
    /// <summary>Gets whether the recording may draw nothing and leave each output standing for its input.</summary>
    public bool MayStandIn { get; } = MayStandIn;
    /// <summary>Gets the buffer holding an indirect dispatch's group counts, or <see langword="null"/>.</summary>
    public IGpuBuffer? Arguments { get; } = Arguments;
    /// <summary>Gets the images of the instances the pass's instance reads that its graph binds to no version, or
    /// <see langword="null"/>.</summary>
    public RenderGraphExternalReads? Reads { get; } = Reads;
    /// <summary>Gets where the pass's kernels count their own work this frame, or <see langword="null"/>.</summary>
    public GpuKernelCounterRow? WorkCounters { get; } = WorkCounters;
}
/// <summary>What a package pass's recording did with its outputs this frame.</summary>
public enum RenderGraphPackageOutcome : byte {
    /// <summary>The recording wrote every output.</summary>
    Drew = 0,
    /// <summary>The recording wrote nothing, so each output port stands for the version bound to the input port at its
    /// position: the instance publishes and captures that input's image in place of the output's, with no copy. An
    /// instance accepts it only from a recording told it may (<see cref="RenderGraphPackageRecording.MayStandIn"/>).</summary>

    DrewNothing = 1,
}
/// <summary>Records one package pass of one installed graph. The instance creates it when the graph installs and
/// disposes it with that graph: when a replacement retires it, on device loss and at disposal, always after the
/// submissions that recorded it are done with and before the device it recorded on is released.</summary>
public interface IRenderGraphPackageRecorder : IDisposable {
    /// <summary>Records the pass's work for one frame. It must not submit, wait, create a pipeline or record a barrier:
    /// the instance submits the command buffer with the rest of its frame, the pipelines were built off the frame thread
    /// (<see cref="IRenderGraphPackageFactory.BuildAsync"/>), and the planned barriers the instance recorded before it left
    /// each bound version in the layout its port's access needs (<see cref="RenderGraphPortAccess"/>): a sampled input
    /// shader-readable and a color-attachment output in <see cref="GpuImageLayout.RenderTarget"/>, which a render pass
    /// the package draws through must leave it in. A recording that draws nothing records nothing and says so, and never
    /// copies an input into an output to stand for it.</summary>
    /// <param name="recording">The frame's command buffer and bound versions.</param>
    /// <returns>Whether the recording wrote its outputs, or left each to stand for its input.</returns>
    RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording);
    /// <summary>Returns whether the pass records nothing this frame: neither its work nor the planned barriers of its
    /// accesses, which the instance asks before it records them. Every storage the pass would have accessed stays in the
    /// state its last recorded access left it in, which the next access starts from, so a pass that skips must be one
    /// whose outputs no later pass reads the contents of on the frames it skips. The default records every
    /// frame.</summary>
    /// <param name="context">The frame being recorded.</param>
    /// <returns><see langword="true"/> when the pass records nothing this frame.</returns>
    bool Skips(in FrameContext context) => false;
    /// <summary>Returns the identity of every package-owned input determining this pass's output, or null to execute.
    /// The node asks after <see cref="Skips"/> and before recording. The recorder may use its existing preparation of
    /// borrowed dependencies here, preserving that preparation's counting and queue ordering; it records no access to
    /// this pass's graph-bound versions. Include borrowed regions, view state, config, unbound reads and every other input not
    /// represented by graph versions; return null when an input has no reliable identity or the pass is forced.
    /// Equal signatures permit standing only while every graph input's last write and the retained output contents
    /// also remain valid. A standing pass records neither work nor barriers; its consumers read its retained result.</summary>
    /// <param name="context">The frame being recorded.</param>
    /// <returns>The output's package-input identity, or null to force execution.</returns>
    ulong? Signature(in FrameContext context) => null;
}
/// <summary>Makes the recorders of one package id. A candidate graph's package passes build with its shader passes:
/// <see cref="BuildAsync"/> creates a pass's shader modules, pipelines and render passes on the thread pool before the
/// graph installs, and <see cref="Create"/> takes those objects on the frame thread when it installs.</summary>
public interface IRenderGraphPackageFactory {
    /// <summary>Builds what a pass's recorder needs that the frame thread must not create: its shader modules,
    /// pipelines and render passes. It runs on the thread pool, awaits whatever it waits for (a pipeline lease, a
    /// residency's tables) so a waiting build holds no thread, creates objects through
    /// <see cref="RenderGraphPackageRecorderContext.Services"/> only, checks the token between creations, and releases
    /// what it created when it fails or is canceled.</summary>
    /// <param name="context">The pass it builds for.</param>
    /// <param name="cancellationToken">Cancels the build when the candidate is superseded, the device is lost or the
    /// instance is disposed.</param>
    /// <returns>The built objects, which <see cref="Create"/> takes, or <see langword="null"/> when the package builds
    /// nothing. The instance disposes them when the candidate never installs.</returns>
    ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken);
    /// <summary>Creates a pass's recorder on the frame thread when its graph installs. It takes ownership of
    /// <paramref name="built"/>, allocates its frame and pass group sets from the instance's pool
    /// (<see cref="RenderGraphPackageSets"/>), and creates no pipeline.</summary>
    /// <param name="context">The pass it records.</param>
    /// <param name="built">What <see cref="BuildAsync"/> returned for this pass.</param>
    /// <param name="groups">The instance's pool, which holds the pass's two sets once per frame slot, and the constant
    /// buffers its frame group and pass group blocks live in, one per frame slot.</param>
    /// <returns>The recorder, which the instance disposes with its graph.</returns>
    IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups);
    /// <summary>States the host-written regions a pass's recorder writes, which the instance creates for it when its graph
    /// installs and hands over in <see cref="RenderGraphPackageGroups.Regions"/>, in this order. The instance chooses each
    /// region's residency (<see cref="GpuResidency.Select"/>, with a reader in flight), flushes each frame slot's share
    /// after the frame's recordings and records every staged copy with its buffer barriers ahead of the frame's passes,
    /// so a recorder only writes a region's contents and binds <see cref="GpuRegion.Buffer"/>. It runs on the thread
    /// pool with <see cref="BuildAsync"/>; a package that writes no region states none.</summary>
    /// <param name="context">The pass it states the regions of.</param>
    /// <returns>The regions, in the order the recorder receives them.</returns>
    IReadOnlyList<RenderGraphPackageRegion> Regions(RenderGraphPackageRecorderContext context) => [];

    /// <summary>Gets whether the package's recorders sample the images of the instances their instance reads that its
    /// graph binds to no version (<see cref="RenderGraphPackageRecording.Reads"/>), as an SDF view's screens do. The
    /// runtime binds those reads, and acquires what they read, only for an instance whose graph runs such a
    /// package.</summary>
    bool SamplesReads => false;

    /// <summary>Returns what an instance's passes of the package allocate their counted storages by
    /// (<see cref="ShaderPipelineResource.Count"/>) beside the extent, or <see langword="null"/> for the extent alone. An
    /// instance's node reads it for every graph it builds that runs a pass of the package, and rebuilds its graph beside
    /// the installed one whenever the counter's revision moves.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The counter, or <see langword="null"/>.</returns>
    IShaderPipelineStorageCounter? CounterOf(string instance) => null;
    /// <summary>Returns the instance's render extent inside its output, or null when both extents are the same.
    /// Render-relative resources allocate at its ceiling; unsized package passes and render-relative passes record at
    /// its current grid. A ceiling revision rebuilds beside the installed graph.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The render-extent provider, or null.</returns>
    IShaderPipelineRenderExtent? RenderExtentOf(string instance) => null;
    /// <summary>Selects an implicit package instance's fragment, or null for the catalog's fragment. Returning another
    /// immutable fragment rebuilds its graph beside the installed one; unchanged frames return the same object.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <returns>The selected fragment, or null.</returns>
    RenderGraphPackageFragment? FragmentOf(string instance) => null;
    /// <summary>Returns whether nothing an instance's passes of the package render from has changed since the instance's
    /// latest completed render, so that render stands for the frame. The runtime asks on the frame thread before it
    /// schedules each frame, for every instance whose graph binds no input and runs only package passes, and declares an
    /// instance unchanged (<see cref="RenderGraphFrame.Unchanged"/>) when every one of its passes' packages answers
    /// <see langword="true"/> and no capture of it is pending.</summary>
    /// <param name="instance">The instance's name.</param>
    /// <param name="context">The host's frame context of the frame being scheduled.</param>
    /// <returns><see langword="true"/> when the instance's latest render stands for this frame.</returns>
    bool IsUnchanged(string instance, in FrameContext context) => false;
    /// <summary>Releases whatever the factory holds on the device after the device was lost, without waiting for any
    /// submission. The runtime calls it once its nodes have released theirs.</summary>
    void OnDeviceLost() { }
    /// <summary>Discards presentation history for an instance whose unnamed graph the runtime released. The runtime
    /// calls each registered factory on the frame thread after the graph and its completed outputs are gone. A factory
    /// that has no state for this instance does nothing; other instances and shared residency state stay intact.</summary>
    /// <param name="instance">The instance whose graph was released.</param>
    void OnGraphReleased(string instance) { }
    /// <summary>Starts a produced frame. The runtime calls it on the frame thread once a frame, before it asks any package
    /// whether an instance is unchanged and before any instance renders.</summary>
    /// <param name="context">The host's frame context of the frame being produced.</param>
    void BeginFrame(in FrameContext context) { }
    /// <summary>Starts a frozen convergence epoch for a captured instance or one of its dependencies. A package that
    /// samples on the capture's behalf takes each render's sample index from the convergence's counted samples
    /// (<see cref="RenderGraphConvergence.Samples"/>), so a frame the runtime does not count renders the same sample
    /// again.</summary>
    /// <param name="instance">The instance whose output contributes to the capture.</param>
    /// <param name="convergence">The capture as the runtime counts it; its request's completion ends the frozen
    /// interval.</param>
    void BeginConvergence(string instance, RenderGraphConvergence convergence) { }
}
/// <summary>A host-written region a package pass's recorder writes (<see cref="IRenderGraphPackageFactory.Regions"/>).</summary>
/// <param name="Name">The region's part name, which names its buffers after the instance and the pass.</param>
/// <param name="ByteCount">The region's size, in bytes; positive and a whole number of uints.</param>
public readonly record struct RenderGraphPackageRegion(string Name, int ByteCount) {
    /// <summary>Gets the package writer's retained CPU scratch payload for this region, in bytes. The node separately
    /// counts the region's own CPU shadow and upload header; this value must not include either.</summary>
    public ulong CpuScratchBytes { get; init; }
}
/// <summary>What a recorder is built and created for: one package pass of one instance's graph, on one device.</summary>
/// <param name="Instance">The instance's name.</param>
/// <param name="Pass">The pass's name in the instance's graph.</param>
/// <param name="Package">The package id the pass names.</param>
/// <param name="Device">The device the instance records on.</param>
/// <param name="Services">The instance's services, which count what they create under the instance.</param>
/// <param name="Pipelines">The composition's pass pipelines, which a package leases every pipeline it records with from,
/// so the same pass in two instances or two installs is one pipeline, counted under the cache rather than the
/// instance.</param>
/// <param name="HostsOnDirectX">Whether the device is Direct3D 12.</param>
/// <param name="InFlightFrames">The instance's frames in flight, the range of <see cref="RenderGraphPackageRecording.Slot"/>.</param>
/// <param name="Width">The pass's extent width, in pixels, at which its graph installs.</param>
/// <param name="Height">The pass's extent height, in pixels.</param>
/// <param name="Inputs">The declarations of the versions bound to its input ports, in port order.</param>
/// <param name="Outputs">The declarations of the versions bound to its output ports, in port order.</param>
/// <param name="Parameters">The pass's interface as the plan lays it out: the frame group at set 0, and the pass group at
/// set 3, whose block holds the extent, the package's config and the values it declares, followed by its declared
/// resources. A recorder creates its pipeline through its <see cref="ShaderInterfaceLayout.PipelineLayout"/> and reads
/// its values' offsets and its resources' bindings from it.</param>
/// <param name="Part">The fragment pass the pass runs (<see cref="RenderGraphFragmentPass.Name"/>), or
/// <see langword="null"/> for a package that runs as one pass.</param>
/// <param name="Dispatch">The pass's dispatch shape, or <see langword="null"/> for one invocation per pixel of its
/// extent.</param>
public sealed record RenderGraphPackageRecorderContext(string Instance, string Pass, string Package, IGpuDeviceContext Device, GpuDeviceServices Services, GpuPassPipelineCache Pipelines, bool HostsOnDirectX, int InFlightFrames, uint Width, uint Height, IReadOnlyList<ShaderPipelineResource> Inputs, IReadOnlyList<ShaderPipelineResource> Outputs, ShaderPipelineParameterLayout Parameters, string? Part = null, ShaderPipelineDispatch? Dispatch = null);
/// <summary>What an external producer is created for: one external instance, on one device.</summary>
/// <param name="Instance">The instance's name.</param>
/// <param name="Package">The package id the instance names.</param>
/// <param name="Device">The device the runtime records on.</param>
/// <param name="HostsOnDirectX">Whether the device is Direct3D 12.</param>
/// <param name="Settings">A source instance's settings object (<see cref="RenderGraphInstance.Settings"/>), which its
/// producer opens the image with, or <see langword="null"/> for its defaults and for any other instance.</param>
public sealed record RenderGraphExternalProducerContext(string Instance, string Package, IGpuDeviceContext Device, bool HostsOnDirectX, IReadOnlyDictionary<string, JsonElement>? Settings = null);
/// <summary>The engine work a host offers render graphs, by package id: the recorders that run a package pass inside a
/// graph instance's submission, the external producers that render an external instance
/// (<see cref="RenderGraphInstanceKind.External"/>) through submissions of their own, and the uploads an uploaded
/// source instance's graph converts. A package id has at most one producer or upload. A graph whose package pass names an
/// id no recorder serves, and an external instance whose package neither a producer nor an upload serves, are refused by
/// name when they are installed.</summary>
/// <param name="regionCopy">The device's region-copy pipelines, which an instance leases when a region it records selects
/// the staged policy, or <see langword="null"/> for a host whose regions never stage; an instance refuses a staged
/// region by name then.</param>
public sealed class RenderGraphPackageRecorders(GpuRegionCopyPass? regionCopy = null) {
    private readonly Dictionary<string, IRenderGraphPackageFactory> m_factories = new(comparer: StringComparer.Ordinal);
    private readonly List<IRenderGraphPackageFactory> m_distinctFactories = [];
    private readonly Dictionary<string, Func<RenderGraphExternalProducerContext, IRenderGraphExternalProducer>> m_producers = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, Func<RenderGraphExternalProducerContext, IRenderGraphSourceUpload>> m_sources = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the device's region-copy pipelines the host offers, or <see langword="null"/> for none.</summary>
    public GpuRegionCopyPass? RegionCopy { get; } = regionCopy;

    /// <summary>Gets the package ids a recorder serves, in ordinal order.</summary>
    public IReadOnlyList<string> Ids => [.. m_factories.Keys.Order(comparer: StringComparer.Ordinal)];
    /// <summary>Gets the package ids an external producer serves, in ordinal order.</summary>
    public IReadOnlyList<string> ProducerIds => [.. m_producers.Keys.Order(comparer: StringComparer.Ordinal)];
    /// <summary>Gets the package ids an upload serves, in ordinal order.</summary>
    public IReadOnlyList<string> SourceIds => [.. m_sources.Keys.Order(comparer: StringComparer.Ordinal)];

    // Every registered recorder factory, each once however many ids it serves, in registration order.
    internal IReadOnlyList<IRenderGraphPackageFactory> Factories => m_distinctFactories;

    /// <summary>Registers the external producer factory for a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <param name="factory">Creates the producer for one external instance; the runtime that installs the instance
    /// owns it.</param>
    /// <exception cref="ArgumentException"><paramref name="package"/> is empty or already has a producer or an
    /// upload.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public void RegisterProducer(string package, Func<RenderGraphExternalProducerContext, IRenderGraphExternalProducer> factory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: package);
        ArgumentNullException.ThrowIfNull(argument: factory);

        if (
            m_sources.ContainsKey(key: package) ||
            !m_producers.TryAdd(
                key: package,
                value: factory
            )
        ) {
            throw new ArgumentException(
                message: $"Package '{package}' already has an external producer or an upload.",
                paramName: nameof(package)
            );
        }
    }
    /// <summary>Registers the upload factory for an uploaded source's package id (<c>source.&lt;producer id&gt;</c>): the
    /// runtime renders each instance of it through the one-pass conversion graph its upload's descriptor names, reading
    /// the region the upload writes.</summary>
    /// <param name="package">The package id.</param>
    /// <param name="factory">Opens the upload for one source instance from its settings; the runtime that installs the
    /// instance owns it.</param>
    /// <exception cref="ArgumentException"><paramref name="package"/> is empty or already has a producer or an
    /// upload.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public void RegisterSource(string package, Func<RenderGraphExternalProducerContext, IRenderGraphSourceUpload> factory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: package);
        ArgumentNullException.ThrowIfNull(argument: factory);

        if (
            m_producers.ContainsKey(key: package) ||
            !m_sources.TryAdd(
                key: package,
                value: factory
            )
        ) {
            throw new ArgumentException(
                message: $"Package '{package}' already has an external producer or an upload.",
                paramName: nameof(package)
            );
        }
    }
    /// <summary>Returns whether an external producer serves a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <returns><see langword="true"/> when a producer is registered for it.</returns>
    public bool ServesProducer(string package) => m_producers.ContainsKey(key: package);
    /// <summary>Returns whether an upload serves a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <returns><see langword="true"/> when an upload is registered for it.</returns>
    public bool ServesSource(string package) => m_sources.ContainsKey(key: package);
    /// <summary>Registers the recorder factory for a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <param name="factory">Builds and creates the recorder for one pass of one installed graph.</param>
    /// <exception cref="ArgumentException"><paramref name="package"/> is empty or already served.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public void Register(string package, IRenderGraphPackageFactory factory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: package);
        ArgumentNullException.ThrowIfNull(argument: factory);

        if (!m_factories.TryAdd(
            key: package,
            value: factory
        )) {
            throw new ArgumentException(
                message: $"Package '{package}' already has a recorder.",
                paramName: nameof(package)
            );
        }
        if (!m_distinctFactories.Contains(item: factory)) {
            m_distinctFactories.Add(item: factory);
        }
    }
    /// <summary>Returns whether a recorder serves a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <returns><see langword="true"/> when a recorder is registered for it.</returns>
    public bool Serves(string package) => m_factories.ContainsKey(key: package);
    /// <summary>Finds the first package pass of a plan, in execution order, that no recorder serves.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="pass">The unserved pass, when this returns <see langword="true"/>; its
    /// <see cref="ShaderPipelinePlannedPass.Package"/> step names the package.</param>
    /// <returns><see langword="true"/> when a package pass is unserved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public bool TryFindUnserved(ShaderPipelinePlan plan, [NotNullWhen(returnValue: true)] out ShaderPipelinePlannedPass? pass) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        foreach (var planned in plan.Passes) {
            if (
                (planned.Package is { } step) &&
                !Serves(package: step.Package)
            ) {
                pass = planned;

                return true;
            }
        }

        pass = null;

        return false;
    }
    /// <summary>Returns the factory that serves a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <param name="factory">The factory, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a recorder is registered for it.</returns>
    public bool TryGetFactory(string package, [NotNullWhen(returnValue: true)] out IRenderGraphPackageFactory? factory) => m_factories.TryGetValue(
        key: package,
        value: out factory
    );

    internal IRenderGraphPackageFactory FactoryFor(string instance, string pass, string package) => (m_factories.TryGetValue(
        key: package,
        value: out var factory
    )
        ? factory
        : throw new InvalidDataException(message: Unserved(
            instance: instance,
            package: package,
            pass: pass
        )));
    internal IRenderGraphExternalProducer CreateProducer(RenderGraphExternalProducerContext context) => (m_producers[context.Package](arg: context) ?? throw new InvalidOperationException(message: $"The external producer factory for package '{context.Package}' returned null."));
    internal IRenderGraphSourceUpload CreateSource(RenderGraphExternalProducerContext context) => (m_sources[context.Package](arg: context) ?? throw new InvalidOperationException(message: $"The upload factory for package '{context.Package}' returned null."));
    internal static string Unserved(string instance, string pass, string package) =>
        $"Instance '{instance}' pass '{pass}' names package '{package}', which no recorder serves.";
}
