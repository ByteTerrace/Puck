using System.Collections.ObjectModel;
using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>How a package pass reaches the version bound to one of its ports: the stage and access the planner plans the
/// port's barrier and layout for, exactly as it plans a shader pass's. An input port reads and an output port
/// writes.</summary>
public enum RenderGraphPortAccess : byte {
    /// <summary>Read by a compute dispatch, as a compute pass reads its inputs: an image shader-readable for the compute
    /// stage.</summary>
    ComputeRead = 0,
    /// <summary>Written by a compute dispatch, as a compute pass writes its outputs: an image in
    /// <see cref="Puck.Abstractions.Gpu.GpuImageLayout.General"/> for the compute stage.</summary>
    ComputeWrite = 1,
    /// <summary>Sampled by a fragment shader, as a graphics pass reads its inputs: an image shader-readable for the
    /// fragment stage.</summary>
    FragmentSampled = 2,
    /// <summary>Drawn into as a render pass's color attachment, as a graphics pass writes its outputs: an image in
    /// <see cref="Puck.Abstractions.Gpu.GpuImageLayout.RenderTarget"/> for color-attachment output. Only an image port
    /// takes it.</summary>
    ColorAttachmentWrite = 3,
}
/// <summary>One port of an engine package: what the version a pass binds to it carries, a buffer's storage, and how the
/// package reaches it. A pass binds a version of the port's kind, and to a buffer port a buffer of the port's stride and
/// count, or the graph compiler refuses it by name. The planner plans the port's barrier and layout from its
/// <see cref="Access"/>, and the package records none of its own.</summary>
/// <param name="Kind">What the port carries: an <see cref="ShaderPipelineResourceKind.Image"/> or a
/// <see cref="ShaderPipelineResourceKind.Buffer"/>.</param>
/// <param name="Access">The stage and access the package reaches the port's version by.</param>
/// <param name="StrideBytes">A buffer port's element stride in bytes, or <see langword="null"/> for a raw buffer or an
/// image.</param>
/// <param name="Count">A buffer port's size in elements as a sum of terms, or <see langword="null"/> for an image or a
/// buffer whose fixed <c>sizeBytes</c> the graph states.</param>
public sealed record RenderGraphPackagePort(
    ShaderPipelineResourceKind Kind,
    RenderGraphPortAccess Access,
    uint? StrideBytes = null,
    IReadOnlyList<ShaderPipelineCountTerm>? Count = null
) {
    /// <summary>Gets whether the port reads its version: <see cref="RenderGraphPortAccess.ComputeRead"/> or
    /// <see cref="RenderGraphPortAccess.FragmentSampled"/>.</summary>
    public bool Reads => (Access is RenderGraphPortAccess.ComputeRead or RenderGraphPortAccess.FragmentSampled);
    /// <summary>Gets whether the port is well formed: its access is declared, an image port declares no storage, a buffer
    /// port's stride, when it has one, is a positive multiple of four, and only an image port is a color
    /// attachment.</summary>
    public bool IsValid => (Enum.IsDefined(value: Access) && (Kind switch {
        ShaderPipelineResourceKind.Image => ((StrideBytes is null) && (Count is null)),
        ShaderPipelineResourceKind.Buffer => (
            (Access != RenderGraphPortAccess.ColorAttachmentWrite) &&
            ((StrideBytes is not { } stride) || ((stride != 0) && ((stride % 4) == 0)))
        ),
        _ => false,
    }));

    /// <summary>Creates an image port.</summary>
    /// <param name="access">How the package reaches the image.</param>
    /// <returns>The port.</returns>
    public static RenderGraphPackagePort Image(RenderGraphPortAccess access) => new(
        Access: access,
        Kind: ShaderPipelineResourceKind.Image
    );
    /// <summary>Creates a buffer port.</summary>
    /// <param name="access">How the package reaches the buffer.</param>
    /// <param name="strideBytes">The element stride in bytes, or <see langword="null"/> for a raw buffer.</param>
    /// <param name="count">The size in elements as a sum of terms, or <see langword="null"/> for a fixed size the graph
    /// states.</param>
    /// <returns>The port.</returns>
    public static RenderGraphPackagePort Buffer(RenderGraphPortAccess access, uint? strideBytes, IReadOnlyList<ShaderPipelineCountTerm>? count) => new(
        Access: access,
        Count: count,
        Kind: ShaderPipelineResourceKind.Buffer,
        StrideBytes: strideBytes
    );
    /// <summary>Returns whether a version may bind to the port: it carries the port's kind, and a buffer has the port's
    /// stride and count.</summary>
    /// <param name="resource">The version's resource declaration.</param>
    /// <returns><see langword="true"/> when the resource matches the port.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is <see langword="null"/>.</exception>
    public bool Accepts(ShaderPipelineResource resource) {
        ArgumentNullException.ThrowIfNull(argument: resource);

        return (
            (resource.Kind == Kind) &&
            (
                (Kind != ShaderPipelineResourceKind.Buffer) ||
                ((resource.StrideBytes == StrideBytes) && ShaderPipelineCountTerm.SameCount(
                    left: resource.Count,
                    right: Count
                ))
            )
        );
    }

    // What a port or resource carries, as a refusal names it: the kind, and a buffer's stride and count.
    internal static string Describe(ShaderPipelineResourceKind kind, uint? strideBytes, IReadOnlyList<ShaderPipelineCountTerm>? count) {
        if (kind != ShaderPipelineResourceKind.Buffer) {
            return kind.ToString();
        }

        var stride = ((strideBytes is { } bytes)
            ? bytes.ToString(provider: CultureInfo.InvariantCulture)
            : "raw"
        );
        var terms = ((count is null)
            ? "fixed"
            : string.Join(
                separator: " + ",
                values: count.Select(selector: static term => string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{term.Elements} per {string.Join(separator: " * ", values: term.Per)}"
                ))
            )
        );

        return $"Buffer (stride {stride}, count {terms})";
    }
}
/// <summary>One engine package a graph can name: an id, its typed ports, what its shaders read from its pass group, its
/// config schema and, for a post-process package, the stages it draws with. It is the one declaration of a package's
/// interface: the planner lays a pass of it out from it (<see cref="ShaderPipelineParameterLayout.ForPackage"/>),
/// <c>puck shaders generate</c> writes its checked-in declarations from it, and its recorder binds by it.</summary>
/// <param name="Id">The id a <see cref="RenderGraphPackagePass"/> names.</param>
/// <param name="Inputs">The input ports, in port order: what each version a pass of it reads carries, and the stage that
/// reads it.</param>
/// <param name="Outputs">The output ports, in port order: what each version a pass of it writes carries, and the stage
/// that writes it, at least one.</param>
/// <param name="Members">The pass-group members the package's shaders read beside the extent and config, in order: the
/// values its recorder writes into the pass block each frame (<see cref="RenderGraphPackageRecording.PassBlock"/>)
/// and the resources it binds in the pass group's set. Every member is in <see cref="ShaderInterfaceGroup.Pass"/>; the
/// planner lays them out with the frame group (<see cref="ShaderPipelineParameterLayout.ForPackage"/>).</param>
/// <param name="Summary">What the package renders.</param>
/// <param name="Config">The config schema a pass of it binds its <see cref="RenderGraphPackagePass.Config"/> against,
/// name to field, or <see langword="null"/> when it takes no config.</param>
/// <param name="Stages">The deployed stages of a post-process package, which samples its one input image in a fullscreen
/// draw into its one output and which <see cref="PostProcessPackage"/> serves, or <see langword="null"/> for any other
/// package. A world's <c>views.post</c> rows name post-process packages alone.</param>
/// <param name="PushesIndex">Whether the package's pipelines push one 4-byte index, which its interface declares
/// (<see cref="ShaderInterface.PushesIndex"/>) and its shaders read as <c>pushedIndex.index</c>.</param>
/// <param name="Fragment">The passes the package runs as, which the graph compiler splices into a graph in place of a
/// pass naming it (<see cref="RenderGraphPackageFragment"/>), or <see langword="null"/> for a package that runs as the
/// one pass naming it.</param>
public sealed record RenderGraphPackage(string Id, IReadOnlyList<RenderGraphPackagePort> Inputs, IReadOnlyList<RenderGraphPackagePort> Outputs, IReadOnlyList<ShaderInterfaceMember> Members, string Summary, IReadOnlyDictionary<string, ShaderConfigField>? Config = null, RenderGraphPackageStages? Stages = null, bool PushesIndex = false, RenderGraphPackageFragment? Fragment = null) {
    /// <summary>Gets whether the package is a post-process package: one with <see cref="Stages"/>.</summary>
    public bool IsPostProcess => (Stages is not null);
    /// <summary>Gets whether the package's shaders count their own work into the node's kernel counters: whether its
    /// <see cref="Members"/> declare <see cref="ShaderWorkCounters.Members"/>. A package that runs as the one pass naming
    /// it counts as that pass; a fragment's passes state each whether it counts
    /// (<see cref="RenderGraphFragmentPass.CountsKernelWork"/>).</summary>
    public bool CountsKernelWork => ShaderWorkCounters.IsDeclaredBy(members: Members);
}
/// <summary>One pass of a package fragment, as the fragment names its versions: its own resources, and the names that
/// stand for the package's ports (<see cref="RenderGraphPackageFragment.InputVersions"/>,
/// <see cref="RenderGraphPackageFragment.OutputVersions"/>).</summary>
/// <param name="Name">The pass's name within the fragment, which its recorder is created for
/// (<see cref="RenderGraphPackageRecorderContext.Part"/>) and which names the spliced pass after the pass that runs the
/// package.</param>
/// <param name="Inputs">The versions it reads.</param>
/// <param name="InputAccesses">How it reads each of <paramref name="Inputs"/>, one read access per input.</param>
/// <param name="Outputs">The versions it writes, at least one.</param>
/// <param name="OutputAccesses">How it writes each of <paramref name="Outputs"/>, one write access per output.</param>
/// <param name="Dispatch">Its dispatch shape: <see langword="null"/> or <see cref="ShaderPipelineDispatchKind.Extent"/>
/// for one invocation per pixel, fixed groups, or an indirect dispatch whose arguments one of its versions holds.</param>
/// <param name="CountsKernelWork">Whether its kernels count their own work into the node's counter buffers
/// (<see cref="GpuKernelCounters"/>): the node then clears and copies them around its frame's passes, and hands the pass
/// its row (<see cref="RenderGraphPackageRecording.WorkCounters"/>).</param>
public sealed record RenderGraphFragmentPass(string Name, IReadOnlyList<ResourceReference> Inputs, IReadOnlyList<RenderGraphPortAccess> InputAccesses, IReadOnlyList<ResourceReference> Outputs, IReadOnlyList<RenderGraphPortAccess> OutputAccesses, ShaderPipelineDispatch? Dispatch = null, bool CountsKernelWork = false);
/// <summary>The passes a package runs as. The graph compiler splices them into a graph in place of each pass naming the
/// package: each fragment pass becomes a package pass named <c>&lt;pass&gt;$&lt;fragment pass&gt;</c>, each fragment
/// resource a version named <c>&lt;pass&gt;$&lt;resource&gt;</c>, a name standing for an input port the version the pass
/// binds to it, and a version standing for an output port the version the pass binds to it, which forwards what the
/// fragment's version forwards. The planner then orders, versions and barriers the spliced passes as any other, so the
/// package's passes record no barrier of their own.</summary>
/// <param name="Resources">The fragment's own versions, output-port versions included and input-port names not.</param>
/// <param name="Passes">The fragment's passes.</param>
/// <param name="InputVersions">The name that stands for each input port, in port order.</param>
/// <param name="OutputVersions">The version that is each output port, in port order: a version of
/// <paramref name="Resources"/> whose declaration the graph's bound version replaces.</param>
public sealed record RenderGraphPackageFragment(IReadOnlyList<ShaderPipelineResource> Resources, IReadOnlyList<RenderGraphFragmentPass> Passes, IReadOnlyList<string> InputVersions, IReadOnlyList<string> OutputVersions) {
    /// <summary>Returns the name a fragment pass or resource takes in a graph, spliced after the pass that runs the
    /// package.</summary>
    /// <param name="pass">The name of the graph's pass naming the package.</param>
    /// <param name="name">The fragment pass's or resource's name.</param>
    /// <returns>The spliced name.</returns>
    public static string Spliced(string pass, string name) => $"{pass}${name}";
}
/// <summary>The deployed stages of a post-process package's fullscreen draw: the directory its bytecode ships in and each
/// stage's stem, completed by the backend's extension (<c>.spv</c> or <c>.dxil</c>).</summary>
/// <param name="Directory">The directory the stages' bytecode ships in, relative to the executable, with forward
/// slashes.</param>
/// <param name="Vertex">The vertex stage's stem.</param>
/// <param name="Fragment">The fragment stage's stem.</param>
public sealed record RenderGraphPackageStages(string Directory, string Vertex, string Fragment);
/// <summary>The engine packages a host offers graphs, by id.</summary>
public sealed class RenderGraphPackageCatalog {
    /// <summary>The format of the engine's working images: every SDF view's color, and every version of a world's root graph
    /// that the views are placed into and the post passes and the overlay draw over. A float image, one at SDR white with
    /// headroom above it, which the display encode quantizes for the display or a capture (<see cref="SurfaceEncoder"/>).</summary>
    public const GpuPixelFormat WorkingFormat = GpuPixelFormat.R16G16B16A16Float;
    /// <summary>The id of the SDF world view: primary traversal, surfaces, ambient occlusion and lighting of one view,
    /// from the instance's camera, run as the fragment <see cref="SdfWorldPackage.Fragment"/> declares. The screens it
    /// shows are the instance's reads, not ports.</summary>
    public const string SdfWorld = "sdf.world";
    /// <summary>The id of the world's SDF brick pool: brick uploads and carve bakes into one pool the world's views
    /// read. It is world-scoped, one instance for the world, and its output is a buffer, so the views reach it over
    /// buffer edges.</summary>
    public const string SdfBricks = "sdf.bricks";
    /// <summary>The id of the unified overlay: the console, HUD, toasts and cursor drawn over its input.</summary>
    public const string Overlay = "overlay";
    /// <summary>The id of the film grain post-process package: a per-pixel integer-hashed offset added over its input.
    /// The hash is a pure function of pixel cell, grain frame and seed, so it renders identically on both backends. Its
    /// fragment stage is <c>src/Puck.SdfVm/Assets/Shaders/Sdf/passes/sdf-film-grain.frag.hlsl</c>, compiled at build, and its
    /// interface is <c>sdf-film-grain</c>.</summary>
    public const string SdfFilmGrain = "sdf.film-grain";
    /// <summary>The id of the one placement pass: its base image, with its source reconstructed into a destination rect
    /// over it, an exact copy where the rect has the source's extent, otherwise bilinear at sharpness 0 blending to
    /// clamped Catmull-Rom at sharpness 1. A rect of the whole output resamples the whole source. Its kernel is
    /// <c>Assets/Shaders/Graph/place.comp.hlsl</c>, compiled at build; its config is the rect (<see cref="PlaceRect"/>),
    /// the sharpness (<see cref="PlaceSharpness"/>), whether the destination outside the rect is the letterbox color
    /// rather than the base (<see cref="PlaceLetterbox"/>) and whether the reconstructed source is tonemapped
    /// (<see cref="PlaceTonemap"/>). <see cref="PlaceCompareMode"/> selects a held/current comparison instead of normal
    /// placement, with <see cref="PlaceWipe"/> controlling its divider.</summary>
    public const string Place = "place";
    /// <summary>The <see cref="Place"/> comparison mode: 0 places normally, 1 wipes, 2 splits and 3 shows absolute
    /// RGB difference. In comparison modes the source is the held rect and the base is the current whole image.</summary>
    public const string PlaceCompareMode = "compareMode";
    /// <summary>The <see cref="Place"/> wipe position across its rect, from 0 to 1; initially 0.5.</summary>
    public const string PlaceWipe = "wipe";
    /// <summary>The <see cref="Place"/> config field that, at 1, fills the destination outside the rect with the
    /// letterbox color the kernel states rather than the base; 0, the default, keeps the base there.</summary>
    public const string PlaceLetterbox = "letterbox";
    /// <summary>The <see cref="Place"/> config field holding the destination rect as fractions of the output's extent:
    /// left, top, width, height.</summary>
    public const string PlaceRect = "rect";
    /// <summary>The <see cref="Place"/> config field holding the reconstruction's sharpness, from 0 (bilinear) to 1
    /// (clamped Catmull-Rom).</summary>
    public const string PlaceSharpness = "sharpness";
    /// <summary>The <see cref="Place"/> config field that, at 1, puts the reconstructed source through the Narkowicz
    /// ACES-fit filmic curve inside the rect: a world's root sets it on each view's place pass when
    /// <c>render.tonemap</c> is <c>Filmic</c>, so the scene is tonemapped where it enters the frame and the base, the
    /// letterbox color and every pane are not; 0, the default, writes the source as it is.</summary>
    public const string PlaceTonemap = "tonemap";
    /// <summary>The name of <see cref="Place"/>'s base image in its pass group, the first input.</summary>
    public const string PlaceBase = "base";
    /// <summary>The name of <see cref="Place"/>'s source image in its pass group, the second input.</summary>
    public const string PlaceSource = "source";
    /// <summary>The name of <see cref="Place"/>'s destination storage image in its pass group, the output.</summary>
    public const string PlaceDestination = "destination";

    private readonly Dictionary<string, RenderGraphPackage> m_packages;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphPackageCatalog"/> class.</summary>
    /// <param name="packages">The packages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> or one of its entries is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A package has no id, null port lists, a null or malformed port
    /// (<see cref="RenderGraphPackagePort.IsValid"/>), an input port that writes or an output port that reads, or no
    /// output, a fragment has no pass, a name per input port or one of its versions per output port, a post-process
    /// package does not sample one image and draw one, or two share an id.</exception>
    public RenderGraphPackageCatalog(IEnumerable<RenderGraphPackage> packages) {
        ArgumentNullException.ThrowIfNull(argument: packages);

        m_packages = new Dictionary<string, RenderGraphPackage>(comparer: StringComparer.Ordinal);

        foreach (var package in packages) {
            ArgumentNullException.ThrowIfNull(argument: package);

            if (
                string.IsNullOrWhiteSpace(value: package.Id) ||
                (package.Inputs is null) ||
                (package.Outputs is null) ||
                (package.Outputs.Count < 1) ||
                package.Inputs.Concat(second: package.Outputs).Any(predicate: static port => ((port is null) || !port.IsValid)) ||
                package.Inputs.Any(predicate: static port => !port.Reads) ||
                package.Outputs.Any(predicate: static port => port.Reads) ||
                (
                    (package.Fragment is { } fragment) &&
                    (
                        (fragment.InputVersions.Count != package.Inputs.Count) ||
                        (fragment.OutputVersions.Count != package.Outputs.Count) ||
                        (fragment.Passes.Count == 0) ||
                        !fragment.OutputVersions.All(predicate: output => fragment.Resources.Any(predicate: resource => string.Equals(
                            a: resource.Name,
                            b: output,
                            comparisonType: StringComparison.Ordinal
                        )))
                    )
                )
            ) {
                throw new ArgumentException(
                    message: $"Package '{package.Id}' needs an id, well-formed input ports that read and output ports that write, at least one output, and, for a fragment, a pass, a name per input port and one of its versions per output port.",
                    paramName: nameof(packages)
                );
            }
            if (
                package.IsPostProcess &&
                !(
                    (package.Inputs is [{ Kind: ShaderPipelineResourceKind.Image, Access: RenderGraphPortAccess.FragmentSampled }]) &&
                    (package.Outputs is [{ Kind: ShaderPipelineResourceKind.Image, Access: RenderGraphPortAccess.ColorAttachmentWrite }])
                )
            ) {
                throw new ArgumentException(
                    message: $"Post-process package '{package.Id}' must sample one image and draw one.",
                    paramName: nameof(packages)
                );
            }
            if (!m_packages.TryAdd(
                key: package.Id,
                value: package
            )) {
                throw new ArgumentException(
                    message: $"Package '{package.Id}' is declared more than once.",
                    paramName: nameof(packages)
                );
            }
        }

        Packages = new ReadOnlyCollection<RenderGraphPackage>(list: [.. m_packages.Values.OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static package => package.Id
        )]);
    }

    /// <summary>Gets the port <see cref="SdfBricks"/> writes: the brick pool, one 32-bit float distance per voxel,
    /// counted by <see cref="ShaderPipelineCountBasis.BrickPoolVoxels"/>.</summary>
    public static RenderGraphPackagePort BrickPool { get; } = RenderGraphPackagePort.Buffer(
        access: RenderGraphPortAccess.ComputeWrite,
        count: [new ShaderPipelineCountTerm(Per: [ShaderPipelineCountBasis.BrickPoolVoxels])],
        strideBytes: sizeof(float)
    );
    /// <summary>Gets the config schema of <see cref="SdfFilmGrain"/>: the peak offset, the cell size, the seed and the
    /// flicker rate.</summary>
    public static IReadOnlyDictionary<string, ShaderConfigField> SdfFilmGrainConfig { get; } = new ReadOnlyDictionary<string, ShaderConfigField>(dictionary: new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) {
        ["intensity"] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "0.05").RootElement.Clone(),
            Description: "The peak per-channel offset.",
            Max: 1,
            Min: 0,
            Type: ShaderValueType.Float
        ),
        ["size"] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "1").RootElement.Clone(),
            Description: "The grain cell size, in pixels.",
            Min: 1,
            Type: ShaderValueType.Float
        ),
        ["seed"] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "0").RootElement.Clone(),
            Description: "Folded into the per-pixel hash; two worlds with different seeds grain differently.",
            Type: ShaderValueType.Uint
        ),
        ["flickerHz"] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "24").RootElement.Clone(),
            Description: "How many times per second the grain pattern advances. The pattern is keyed on the engine tick, so a frame carries the grain of the flicker period its tick falls in; a rate that does not divide the tick rate advances every whole number of ticks nearest below its period.",
            Min: 1,
            Type: ShaderValueType.Uint
        ),
    });
    /// <summary>Gets what <see cref="SdfFilmGrain"/>'s fragment stage reads from its pass group beside the extent and
    /// config: its input image and the sampler it samples it through, then the work counters it counts every texel it
    /// writes into.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> SdfFilmGrainMembers { get; } = [
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: "source", type: ShaderValueType.Float4),
        ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "sourceSampler"),
        .. ShaderWorkCounters.Members,
    ];
    /// <summary>Gets the config schema of <see cref="Place"/>: the letterbox switch, off by default, the rect, whole by
    /// default, the sharpness, 0 by default, the tonemap switch, off by default, and optional comparison mode and wipe.</summary>
    public static IReadOnlyDictionary<string, ShaderConfigField> PlaceConfig { get; } = new ReadOnlyDictionary<string, ShaderConfigField>(dictionary: new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) {
        [PlaceCompareMode] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "0").RootElement.Clone(),
            Description: "0 places normally; 1 wipes, 2 splits, and 3 shows absolute RGB difference between the held source and the base image's current rect.",
            Max: 3,
            Min: 0,
            Type: ShaderValueType.Uint
        ),
        [PlaceLetterbox] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "0").RootElement.Clone(),
            Description: "1 fills the destination outside the rect with the letterbox color rather than the base.",
            Max: 1,
            Min: 0,
            Type: ShaderValueType.Uint
        ),
        [PlaceRect] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "[0, 0, 1, 1]").RootElement.Clone(),
            Description: "The destination rect as fractions of the destination extent: left, top, width, height.",
            Max: 1,
            Min: 0,
            Type: ShaderValueType.Float4
        ),
        [PlaceSharpness] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "0").RootElement.Clone(),
            Description: "Reconstruction sharpness: 0 is bilinear, 1 clamped Catmull-Rom.",
            Max: 1,
            Min: 0,
            Type: ShaderValueType.Float
        ),
        [PlaceTonemap] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "0").RootElement.Clone(),
            Description: "1 puts the reconstructed source through the filmic tonemap inside the rect; the base and the letterbox color are never tonemapped.",
            Max: 1,
            Min: 0,
            Type: ShaderValueType.Uint
        ),
        [PlaceWipe] = new ShaderConfigField(
            Default: System.Text.Json.JsonDocument.Parse(json: "0.5").RootElement.Clone(),
            Description: "The held image covers this fraction of the rect in wipe mode; the current image covers the rest.",
            Max: 1,
            Min: 0,
            Type: ShaderValueType.Float
        ),
    });
    /// <summary>Gets what <see cref="Place"/>'s kernel reads from its pass group beside the extent and config: its base
    /// image and its sampler, its source image and its sampler, and the destination it writes, each the member a
    /// document pass compiling the same kernel derives from ports named for them. The kernel loads every texel, so it
    /// never samples through either sampler.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> PlaceMembers { get; } = [
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: PlaceBase, type: ShaderValueType.Float4),
        ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: (PlaceBase + ShaderPipelinePassPorts.SamplerSuffix)),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: PlaceSource, type: ShaderValueType.Float4),
        ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: (PlaceSource + ShaderPipelinePassPorts.SamplerSuffix)),
        ShaderInterfaceMember.StorageImage(format: WorkingFormat, group: ShaderInterfaceGroup.Pass, name: PlaceDestination, type: ShaderValueType.Float4),
        .. ShaderWorkCounters.Members,
    ];

    /// <summary>The number of frame slots the overlay samples: images a Frame element draws, such as a face cam.</summary>
    public const int OverlayFrameSlotCount = 8;
    /// <summary>The name of the overlay's input image in its pass group.</summary>
    public const string OverlaySource = "source";
    /// <summary>The name of the one sampler the overlay reads every image through.</summary>
    public const string OverlaySampler = "linearSampler";
    /// <summary>The name of the overlay's storage buffer: its variable-length records, tokens, glyph cells and clip
    /// table.</summary>
    public const string OverlayData = "overlayData";

    /// <summary>Returns the name of one of the overlay's frame slot images.</summary>
    /// <param name="slot">The slot, below <see cref="OverlayFrameSlotCount"/>.</param>
    /// <returns>The name.</returns>
    public static string OverlayFrameSlot(int slot) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"frameSlot{slot}"
    );

    /// <summary>Gets what <see cref="Overlay"/>'s fragment stage reads from its pass group beside the extent: three
    /// per-frame values its recorder writes, which the pass block holds in name order as it holds config fields (<c>counts</c>: panel and
    /// element counts and the atlas cell's width and height; <c>misc</c>: the text, atlas and clip bases and the glyph
    /// count; <c>sdf</c>: the distance range, the outline band and the panel and element bases), then its input image, its
    /// frame slot images, the one sampler they are all read through, its storage buffer, and the work counters it counts
    /// every texel it writes into.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> OverlayMembers { get; } = [
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, name: "counts", type: ShaderValueType.Float4),
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, name: "misc", type: ShaderValueType.Float4),
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, name: "sdf", type: ShaderValueType.Float4),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: OverlaySource, type: ShaderValueType.Float4),
        .. Enumerable.Range(
            count: OverlayFrameSlotCount,
            start: 0
        ).Select(selector: static slot => ShaderInterfaceMember.SampledImage(
            group: ShaderInterfaceGroup.Pass,
            name: OverlayFrameSlot(slot: slot),
            type: ShaderValueType.Float4
        )),
        ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: OverlaySampler),
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.Pass, name: OverlayData),
        .. ShaderWorkCounters.Members,
    ];

    /// <summary>The name of a conversion package's region in its pass group, its input: the uploaded source's
    /// <c>ImageSourceUploadLayout</c> header and planes, read as raw 32-bit words.</summary>
    public const string SourceRegion = "region";
    /// <summary>The name of a conversion package's image in its pass group, its output: the storage image every consumer of
    /// the source reads.</summary>
    public const string SourceImage = "image";

    /// <summary>Returns what a conversion package's kernel reads from its pass group beside the extent: the region at
    /// binding 1, the image it writes at binding 2, and the work counters it counts every texel it writes into, as the
    /// kernels in <c>Assets/Shaders/Sources</c> declare them.</summary>
    /// <param name="format">The image's format.</param>
    /// <returns>The members.</returns>
    public static IReadOnlyList<ShaderInterfaceMember> SourceMembers(GpuPixelFormat format) => [
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.Pass, name: SourceRegion),
        ShaderInterfaceMember.StorageImage(format: format, group: ShaderInterfaceGroup.Pass, name: SourceImage, type: ShaderValueType.Float4),
        .. ShaderWorkCounters.Members,
    ];
    /// <summary>Returns the format of the image a conversion package writes: half-float RGBA for
    /// <see cref="Puck.Abstractions.Sources.ImageSourceConversion.TransferPass"/>, which writes linear light, and RGBA8
    /// for every other.</summary>
    /// <param name="package">The conversion package's id, one of <see cref="SourceConversions"/>.</param>
    /// <returns>The format.</returns>
    public static GpuPixelFormat SourceFormatOf(string package) => (string.Equals(
        a: package,
        b: Puck.Abstractions.Sources.ImageSourceConversion.TransferPass,
        comparisonType: StringComparison.Ordinal
    )
        ? GpuPixelFormat.R16G16B16A16Float
        : GpuPixelFormat.R8G8B8A8Unorm);

    /// <summary>Gets the conversion packages, one per pass <see cref="Puck.Abstractions.Sources.ImageSourceConversion"/>
    /// names, each package id the pass's name: one compute dispatch of the build-compiled kernel of that name reading an
    /// uploaded source's region and writing its image.</summary>
    public static IReadOnlyList<string> SourceConversions { get; } = [
        Puck.Abstractions.Sources.ImageSourceConversion.Nv12Pass,
        Puck.Abstractions.Sources.ImageSourceConversion.PalettePass,
        Puck.Abstractions.Sources.ImageSourceConversion.RgbaPass,
        Puck.Abstractions.Sources.ImageSourceConversion.TransferPass,
    ];
    /// <summary>Gets the engine's own packages: <see cref="SdfWorld"/>, <see cref="SdfBricks"/>, <see cref="Overlay"/>,
    /// <see cref="Place"/>, the <see cref="SourceConversions"/> and the post-process package
    /// <see cref="SdfFilmGrain"/>.</summary>
    public static RenderGraphPackageCatalog Engine { get; } = new(packages: EnginePackages());
    /// <summary>Gets the catalog of a host that offers no package, whose graphs are shader passes alone.</summary>
    public static RenderGraphPackageCatalog None { get; } = new(packages: []);

    /// <summary>Gets the packages in ordinal id order.</summary>
    public IReadOnlyList<RenderGraphPackage> Packages { get; }

    private static IEnumerable<RenderGraphPackage> EnginePackages() => [
        new RenderGraphPackage(
            Fragment: SdfWorldPackage.Fragment,
            Id: SdfWorld,
            Inputs: [],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Members: SdfWorldPackage.Members,
            Summary: "The SDF world as the instance's camera sees it."
        ),
        new RenderGraphPackage(
            Id: SdfBricks,
            Inputs: [],
            Outputs: [BrickPool],
            Members: [],
            Summary: "The world's SDF brick pool: brick uploads and carve bakes, which the views read over a buffer edge."
        ),
        new RenderGraphPackage(
            Id: Overlay,
            Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.FragmentSampled)],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ColorAttachmentWrite)],
            Members: OverlayMembers,
            Summary: "The console, HUD, toasts and cursor drawn over the input image."
        ),
        new RenderGraphPackage(
            Config: PlaceConfig,
            Id: Place,
            Inputs: [
                RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeRead),
                RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeRead),
            ],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Members: PlaceMembers,
            Summary: "The base image with the source reconstructed into a rect over it, bilinear to clamped Catmull-Rom by sharpness, and tonemapped when asked."
        ),
        .. SourceConversions.Select(selector: static id => new RenderGraphPackage(
            Id: id,
            Inputs: [
                RenderGraphPackagePort.Buffer(
                    access: RenderGraphPortAccess.ComputeRead,
                    count: null,
                    strideBytes: null
                ),
            ],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Members: SourceMembers(format: SourceFormatOf(package: id)),
            Summary: $"The uploaded source's region converted by the shipped '{id}' kernel into the image its consumers read."
        )),
        new RenderGraphPackage(
            Config: SdfFilmGrainConfig,
            Id: SdfFilmGrain,
            Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.FragmentSampled)],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ColorAttachmentWrite)],
            Members: SdfFilmGrainMembers,
            Stages: new RenderGraphPackageStages(
                Directory: "Assets/Shaders/Sdf/passes",
                Fragment: "sdf-film-grain.frag",
                Vertex: "fullscreen.vert"
            ),
            Summary: "Film grain: a per-pixel integer-hashed offset added over the input image, keyed on the engine tick."
        ),
    ];

    /// <summary>Finds a package by id.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="package">The package, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the catalog declares the id.</returns>
    public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out RenderGraphPackage? package) => m_packages.TryGetValue(
        key: id,
        value: out package
    );
}
