using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>One version a package pass reads or writes, resolved for the frame it records: the image or the buffer
/// that holds it in the frame slot being recorded.</summary>
/// <param name="Version">The version name the pass binds to the port.</param>
/// <param name="Kind">What the version carries.</param>
/// <param name="Image">The image holding an image version, with its extent and format; default for a buffer.</param>
/// <param name="Buffer">The buffer holding a buffer version, or <see langword="null"/> for an image.</param>
public readonly record struct RenderGraphPackageResource(string Version, ShaderPipelineResourceKind Kind, ShaderPipelineExternalImage Image, IGpuBuffer? Buffer);
/// <summary>What a package recorder records into for one frame: the command buffer the pass owns in its instance's
/// submission, already begun, with the pass's planned barriers recorded, and the versions bound to its ports.</summary>
/// <param name="CommandBuffer">The command buffer to record into. The instance ends and submits it.</param>
/// <param name="Recorder">The instance's recorder, which counts what the package records under the pass.</param>
/// <param name="Slot">The frame slot being recorded, in [0, the instance's frames in flight).</param>
/// <param name="Width">The pass's extent width, in pixels: its first output's, else the instance's frame
/// width.</param>
/// <param name="Height">The pass's extent height, in pixels.</param>
/// <param name="Inputs">The versions bound to its input ports, in port order.</param>
/// <param name="Outputs">The versions bound to its output ports, in port order.</param>
public readonly ref struct RenderGraphPackageRecording(nint CommandBuffer, IGpuRecorder Recorder, int Slot, uint Width, uint Height, ReadOnlySpan<RenderGraphPackageResource> Inputs, ReadOnlySpan<RenderGraphPackageResource> Outputs) {
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
}
/// <summary>Records one package pass of one installed graph. The instance creates it when the graph installs and
/// disposes it with that graph: when a replacement retires it, on device loss and at disposal, always after the
/// submissions that recorded it are done with and before the device it recorded on is released.</summary>
public interface IRenderGraphPackageRecorder : IDisposable {
    /// <summary>Records the pass's work for one frame. It must not submit, wait or create a pipeline: the instance
    /// submits the command buffer with the rest of its frame, and a recorder that needs pipelines builds them off the
    /// frame thread and records nothing until they are ready.</summary>
    /// <param name="recording">The frame's command buffer and bound versions.</param>
    void Record(in RenderGraphPackageRecording recording);
}
/// <summary>What a recorder is created for: one package pass of one instance's graph, on one device.</summary>
/// <param name="Instance">The instance's name.</param>
/// <param name="Pass">The pass's name in the instance's graph.</param>
/// <param name="Package">The package id the pass names.</param>
/// <param name="Device">The device the instance records on.</param>
/// <param name="HostsOnDirectX">Whether the device is Direct3D 12.</param>
/// <param name="InFlightFrames">The instance's frames in flight, the range of <see cref="RenderGraphPackageRecording.Slot"/>.</param>
public sealed record RenderGraphPackageRecorderContext(string Instance, string Pass, string Package, IGpuDeviceContext Device, bool HostsOnDirectX, int InFlightFrames);
/// <summary>The recorders a host offers package passes, by package id: the adapters that run engine work inside a
/// graph instance's submission. A graph whose package pass names an id nothing here serves is refused by name when it is
/// installed.</summary>
public sealed class RenderGraphPackageRecorders {
    private readonly Dictionary<string, Func<RenderGraphPackageRecorderContext, IRenderGraphPackageRecorder>> m_factories = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the package ids served, in ordinal order.</summary>
    public IReadOnlyList<string> Ids => [.. m_factories.Keys.Order(comparer: StringComparer.Ordinal)];

    /// <summary>Registers the recorder factory for a package id.</summary>
    /// <param name="package">The package id.</param>
    /// <param name="factory">Creates the recorder for one pass of one installed graph.</param>
    /// <exception cref="ArgumentException"><paramref name="package"/> is empty or already served.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public void Register(string package, Func<RenderGraphPackageRecorderContext, IRenderGraphPackageRecorder> factory) {
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
    /// <see cref="ShaderPipelinePass.Source"/> is the package id.</param>
    /// <returns><see langword="true"/> when a package pass is unserved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public bool TryFindUnserved(ShaderPipelinePlan plan, [NotNullWhen(returnValue: true)] out ShaderPipelinePlannedPass? pass) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        foreach (var planned in plan.Passes) {
            if (
                (planned.Kind == ShaderPipelinePassKind.Package) &&
                !Serves(package: planned.Declaration.Source)
            ) {
                pass = planned;

                return true;
            }
        }

        pass = null;

        return false;
    }

    internal IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context) {
        if (!m_factories.TryGetValue(
            key: context.Package,
            value: out var factory
        )) {
            throw new InvalidDataException(message: Unserved(
                instance: context.Instance,
                package: context.Package,
                pass: context.Pass
            ));
        }

        return (factory(arg: context) ?? throw new InvalidOperationException(message: $"The recorder factory for package '{context.Package}' returned null."));
    }
    internal static string Unserved(string instance, string pass, string package) =>
        $"Instance '{instance}' pass '{pass}' names package '{package}', which no recorder serves.";
}
