using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Cli.Qualification;

/// <summary>
/// A <c>puck.release.profile.v1</c> document: what <c>puck qualify</c> holds a producer-built <c>Puck.World</c>
/// package to. It records how the package was published and where a World run finds a shader compiler, the functional
/// canaries run against the package, the stability matrix (every backend, resolution and workload), each workload's
/// warm-up, soak and churn in ticks and frames, never in time, the memory threshold of every matrix cell, and the checks
/// qualification defers with the reason each is deferred.
/// </summary>
/// <param name="Schema">The document schema tag, <see cref="SchemaVersion"/>.</param>
/// <param name="Publish">How the package is published and how a World run in it finds a shader compiler.</param>
/// <param name="Backends">The graphics backends every cell runs on, in the order they run.</param>
/// <param name="DebugLayers">The backends whose validation layer every leg on them enables, by booting its World with
/// <c>--debug-layers</c>. A leg on a listed backend fails on any validation message; a leg on an unlisted backend runs
/// with its layer off.</param>
/// <param name="Functional">The <c>puck canary</c> proofs run against the package's own World before the matrix: the
/// functional half of qualification.</param>
/// <param name="Resolutions">The offscreen output extents every workload runs at.</param>
/// <param name="Workloads">What each cell boots and does.</param>
/// <param name="Thresholds">One memory threshold row per matrix cell.</param>
/// <param name="Deferred">The checks qualification does not make yet, each with why.</param>
internal sealed record ReleaseProfile(
    string Schema,
    ReleasePublish Publish,
    IReadOnlyList<string> Backends,
    IReadOnlyList<string> DebugLayers,
    IReadOnlyList<string> Functional,
    IReadOnlyList<QualificationResolution> Resolutions,
    IReadOnlyList<QualificationWorkload> Workloads,
    IReadOnlyList<QualificationThreshold> Thresholds,
    IReadOnlyList<QualificationDeferral> Deferred
) {
    /// <summary>The document schema tag every well-formed release profile carries.</summary>
    public const string SchemaVersion = "puck.release.profile.v1";
}
/// <summary>How the qualified package is published.</summary>
/// <param name="Mode">The publish mode the package's entry assembly must show.</param>
/// <param name="EntryAssembly">The entry assembly's file name inside the package, which a leg launches with
/// <c>dotnet</c>.</param>
/// <param name="Compiler">Where a World run in the package finds a shader compiler.</param>
internal sealed record ReleasePublish(
    ReleasePublishMode Mode,
    string EntryAssembly,
    ReleaseCompilerDiscovery Compiler
);
/// <summary>The publish modes a qualified package can take.</summary>
[JsonConverter(typeof(StrictEnumConverter<ReleasePublishMode>))]
internal enum ReleasePublishMode {
    /// <summary>Framework-dependent, with every managed assembly precompiled to native code beside its IL: the entry
    /// assembly carries a ReadyToRun header, and the machine supplies the .NET runtime.</summary>
    ReadyToRun,
}
/// <summary>Where a World run finds a shader compiler.</summary>
[JsonConverter(typeof(StrictEnumConverter<ReleaseCompilerDiscovery>))]
internal enum ReleaseCompilerDiscovery {
    /// <summary>Nowhere: every matrix leg runs with each directory holding <c>dxc</c> removed from its search path, so
    /// a world that asks for a compile fails its cell.</summary>
    None,
    /// <summary>On the search path the qualification run inherits, as a developer machine finds it.</summary>
    Path,
}
/// <summary>One offscreen output extent.</summary>
/// <param name="Width">The width in pixels; even, so a half-size resize is exact.</param>
/// <param name="Height">The height in pixels; even, so a half-size resize is exact.</param>
internal sealed record QualificationResolution(int Width, int Height) {
    /// <summary>Gets the extent as <c>WIDTHxHEIGHT</c>.</summary>
    [JsonIgnore]
    public string Label => $"{Width}x{Height}";
}
/// <summary>What one workload boots and does in every cell it runs in. Lengths are ticks of the world's own
/// simulation clock or frames the pipeline instance submits, never time.</summary>
/// <param name="Name">The workload's name: lowercase letters, digits and hyphens, since it names files and cells.</param>
/// <param name="World">The world document the cell boots, relative to the package directory with forward slashes; a
/// <c>.world.json</c> document, which a generated overlay beside it takes as its basis.</param>
/// <param name="WarmupTicks">The ticks run before the first reading.</param>
/// <param name="SoakTicks">The ticks of each soak window, across which no GPU object may be created.</param>
/// <param name="WorldReloads">How many times the cell reloads the world document from disk.</param>
/// <param name="Pipeline">The pipeline instance the cell churns, or <see langword="null"/> for none.</param>
/// <param name="TimeoutSeconds">The wall-clock ceiling a leg is killed at: a backstop for a hung World, never a
/// measurement or a threshold.</param>
internal sealed record QualificationWorkload(
    string Name,
    string World,
    int WarmupTicks,
    int SoakTicks,
    int WorldReloads,
    QualificationPipeline? Pipeline,
    int TimeoutSeconds
);
/// <summary>The pipeline instance a workload churns: one the world authors in <c>views.pipelines</c>, shown by a slot
/// of one of its <c>views.layouts</c> rows.</summary>
/// <param name="Instance">The <c>views.pipelines</c> row's name.</param>
/// <param name="Layout">The <c>views.layouts</c> row whose slot shows the instance.</param>
/// <param name="SettleFrames">The submissions counted after each reset before the instance is inspected, enough for a
/// replaced graph and its held images to retire.</param>
/// <param name="Reloads">How many times the instance recompiles and installs its source (<c>pipeline.reload</c>).</param>
/// <param name="Resizes">How many times the instance's slot shrinks to half its extent on each axis and returns.</param>
/// <param name="Loads">How many times the instance is unloaded (its row removed) and loaded again as a new
/// instance.</param>
internal sealed record QualificationPipeline(
    string Instance,
    string Layout,
    int SettleFrames,
    int Reloads,
    int Resizes,
    int Loads
);
/// <summary>The memory threshold of one matrix cell.</summary>
/// <param name="Workload">The workload's name.</param>
/// <param name="Backend">The backend.</param>
/// <param name="Width">The resolution's width.</param>
/// <param name="Height">The resolution's height.</param>
/// <param name="PeakOwnedPipelineBytes">The most bytes the workload's pipeline instance may own or plan to own at
/// once, read from every <c>pipeline.inspect</c>'s <c>owned=</c> and <c>peak=</c>; required for a workload with a
/// pipeline, and <see langword="null"/> for one without.</param>
/// <param name="PeakDeviceLocalBytes">The most device-local bytes the World process may hold at once, read from every
/// <c>world.counters --json</c> reading's <c>gpu.memory.device-local.peak</c>; <see langword="null"/> until the cell
/// has a reading on the reference device to set it from.</param>
internal sealed record QualificationThreshold(
    string Workload,
    string Backend,
    int Width,
    int Height,
    long? PeakOwnedPipelineBytes,
    long? PeakDeviceLocalBytes
);
/// <summary>A check qualification does not make yet.</summary>
/// <param name="Check">The check.</param>
/// <param name="Reason">Why it is deferred and what it waits on.</param>
internal sealed record QualificationDeferral(QualificationDeferredCheck Check, string Reason);
/// <summary>The checks a release profile can defer.</summary>
[JsonConverter(typeof(StrictEnumConverter<QualificationDeferredCheck>))]
internal enum QualificationDeferredCheck {
    /// <summary>A threshold on the peak of device-local bytes the whole World process holds.</summary>
    DeviceLocalPeak,
    /// <summary>Frame-time median and tail.</summary>
    FrameTime,
    /// <summary>How long a reload stalls presentation.</summary>
    ReloadStall,
}
/// <summary>Source-generated metadata for <see cref="ReleaseProfile"/> and the report <c>puck qualify</c> writes:
/// strict both ways, so an unknown member and a missing required member are refused by name.</summary>
[JsonSerializable(typeof(ReleaseProfile))]
[JsonSerializable(typeof(QualificationReport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true
)]
internal sealed partial class QualificationJsonContext : JsonSerializerContext;
