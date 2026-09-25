using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>One version a package pass reads or writes, resolved for the frame it records: the image or the buffer
/// that holds it in the frame slot being recorded.</summary>
/// <param name="Version">The version name the pass binds to the port.</param>
/// <param name="Kind">What the version carries.</param>
/// <param name="Image">The image holding an image version, with its extent, its format, and the layout the pass's planned
/// barrier left it in for the recording; default for a buffer.</param>
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
/// <param name="FrameBlock">The pass's frame block for this frame, written as the instance writes a shader pass's: the
/// frame members and the pass's bound config (<see cref="RenderGraphPackageRecorderContext.Parameters"/>).</param>
/// <param name="Leases">The frame's lease list: a lease held in it retires once this frame's submission has finished, on
/// device loss or at disposal, and at once when the frame submits nothing.</param>
public readonly ref struct RenderGraphPackageRecording(nint CommandBuffer, IGpuRecorder Recorder, int Slot, uint Width, uint Height, ReadOnlySpan<RenderGraphPackageResource> Inputs, ReadOnlySpan<RenderGraphPackageResource> Outputs, ReadOnlySpan<byte> FrameBlock, LeaseRetireList Leases) {
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
    /// <summary>Gets the versions bound to the input ports.</summary>
    public ReadOnlySpan<RenderGraphPackageResource> Inputs { get; } = Inputs;
    /// <summary>Gets the versions bound to the output ports.</summary>
    public ReadOnlySpan<RenderGraphPackageResource> Outputs { get; } = Outputs;
    /// <summary>Gets the pass's frame block for this frame.</summary>
    public ReadOnlySpan<byte> FrameBlock { get; } = FrameBlock;
    /// <summary>Gets the frame's lease list.</summary>
    public LeaseRetireList Leases { get; } = Leases;
}
/// <summary>Records one package pass of one installed graph. The instance creates it when the graph installs and
/// disposes it with that graph: when a replacement retires it, on device loss and at disposal, always after the
/// submissions that recorded it are done with and before the device it recorded on is released.</summary>
public interface IRenderGraphPackageRecorder : IDisposable {
    /// <summary>Records the pass's work for one frame. It must not submit, wait or create a pipeline: the instance
    /// submits the command buffer with the rest of its frame, and the pipelines were built off the frame thread
    /// (<see cref="IRenderGraphPackageFactory.Build"/>).</summary>
    /// <param name="recording">The frame's command buffer and bound versions.</param>
    void Record(in RenderGraphPackageRecording recording);
}
/// <summary>Makes the recorders of one package id. A candidate graph's package passes build with its shader passes:
/// <see cref="Build"/> creates a pass's shader modules, pipelines and render passes on the thread pool before the graph
/// installs, and <see cref="Create"/> takes those objects on the frame thread when it installs.</summary>
public interface IRenderGraphPackageFactory {
    /// <summary>Gets the bindings of the one descriptor set a recorder allocates per frame slot, or an empty list when
    /// it binds none. The instance states them in its one descriptor pool
    /// (<see cref="ShaderPipelineRenderNode.DescriptorPools"/>), which the device's heap admits before anything is
    /// allocated.</summary>
    IReadOnlyList<GpuComputeBinding> SetBindings { get; }

    /// <summary>Builds what a pass's recorder needs that the frame thread must not create: its shader modules,
    /// pipelines and render passes. It runs on the thread pool, creates objects through
    /// <see cref="RenderGraphPackageRecorderContext.Services"/> only, checks the token between creations, and releases
    /// what it created when it fails or is canceled.</summary>
    /// <param name="context">The pass it builds for.</param>
    /// <param name="cancellationToken">Cancels the build when the candidate is superseded, the device is lost or the
    /// instance is disposed.</param>
    /// <returns>The built objects, which <see cref="Create"/> takes, or <see langword="null"/> when the package builds
    /// nothing. The instance disposes them when the candidate never installs.</returns>
    IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken);
    /// <summary>Creates a pass's recorder on the frame thread when its graph installs. It takes ownership of
    /// <paramref name="built"/>, allocates its descriptor sets from <paramref name="descriptorPool"/>, and creates no
    /// pipeline.</summary>
    /// <param name="context">The pass it records.</param>
    /// <param name="built">What <see cref="Build"/> returned for this pass.</param>
    /// <param name="descriptorPool">The instance's descriptor pool, which holds <see cref="SetBindings"/> once per frame
    /// slot, or zero when <see cref="SetBindings"/> is empty.</param>
    /// <returns>The recorder, which the instance disposes with its graph.</returns>
    IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, nint descriptorPool);
}
/// <summary>What a recorder is built and created for: one package pass of one instance's graph, on one device.</summary>
/// <param name="Instance">The instance's name.</param>
/// <param name="Pass">The pass's name in the instance's graph.</param>
/// <param name="Package">The package id the pass names.</param>
/// <param name="Device">The device the instance records on.</param>
/// <param name="Services">The instance's services, which count what they create under the instance.</param>
/// <param name="HostsOnDirectX">Whether the device is Direct3D 12.</param>
/// <param name="InFlightFrames">The instance's frames in flight, the range of <see cref="RenderGraphPackageRecording.Slot"/>.</param>
/// <param name="Width">The pass's extent width, in pixels, at which its graph installs.</param>
/// <param name="Height">The pass's extent height, in pixels.</param>
/// <param name="Inputs">The declarations of the versions bound to its input ports, in port order.</param>
/// <param name="Outputs">The declarations of the versions bound to its output ports, in port order.</param>
/// <param name="Parameters">The pass's frame block layout: the frame members, then the package's config fields.</param>
public sealed record RenderGraphPackageRecorderContext(string Instance, string Pass, string Package, IGpuDeviceContext Device, GpuDeviceServices Services, bool HostsOnDirectX, int InFlightFrames, uint Width, uint Height, IReadOnlyList<ShaderPipelineResource> Inputs, IReadOnlyList<ShaderPipelineResource> Outputs, ShaderPipelineParameterLayout Parameters);
/// <summary>What an external producer is created for: one external instance, on one device.</summary>
/// <param name="Instance">The instance's name.</param>
/// <param name="Package">The package id the instance names.</param>
/// <param name="Device">The device the runtime records on.</param>
/// <param name="HostsOnDirectX">Whether the device is Direct3D 12.</param>
public sealed record RenderGraphExternalProducerContext(string Instance, string Package, IGpuDeviceContext Device, bool HostsOnDirectX);
/// <summary>The engine work a host offers render graphs, by package id: the recorders that run a package pass inside a
/// graph instance's submission, and the external producers that render an external instance
/// (<see cref="RenderGraphInstanceKind.External"/>) through submissions of their own. A graph whose package pass names an
/// id no recorder serves, and an external instance whose package no producer serves, are refused by name when they are
/// installed.</summary>
public sealed class RenderGraphPackageRecorders {
    private readonly Dictionary<string, IRenderGraphPackageFactory> m_factories = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, Func<RenderGraphExternalProducerContext, IRenderGraphExternalProducer>> m_producers = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the package ids a recorder serves, in ordinal order.</summary>
    public IReadOnlyList<string> Ids => [.. m_factories.Keys.Order(comparer: StringComparer.Ordinal)];
    /// <summary>Gets the package ids an external producer serves, in ordinal order.</summary>
    public IReadOnlyList<string> ProducerIds => [.. m_producers.Keys.Order(comparer: StringComparer.Ordinal)];

    /// <summary>Registers the external producer factory for a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <param name="factory">Creates the producer for one external instance; the runtime that installs the instance
    /// owns it.</param>
    /// <exception cref="ArgumentException"><paramref name="package"/> is empty or already has a producer.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public void RegisterProducer(string package, Func<RenderGraphExternalProducerContext, IRenderGraphExternalProducer> factory) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: package);
        ArgumentNullException.ThrowIfNull(argument: factory);

        if (!m_producers.TryAdd(
            key: package,
            value: factory
        )) {
            throw new ArgumentException(
                message: $"Package '{package}' already has an external producer.",
                paramName: nameof(package)
            );
        }
    }
    /// <summary>Returns whether an external producer serves a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <returns><see langword="true"/> when a producer is registered for it.</returns>
    public bool ServesProducer(string package) => m_producers.ContainsKey(key: package);
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
    internal static string Unserved(string instance, string pass, string package) =>
        $"Instance '{instance}' pass '{pass}' names package '{package}', which no recorder serves.";
}
