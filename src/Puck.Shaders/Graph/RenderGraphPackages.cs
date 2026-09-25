using System.Collections.ObjectModel;
using System.Globalization;
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
/// <summary>One engine package a graph can name: an id and its typed ports.</summary>
/// <param name="Id">The id a <see cref="RenderGraphPackagePass"/> names.</param>
/// <param name="Inputs">The input ports, in port order: what each version a pass of it reads carries, and the stage that
/// reads it.</param>
/// <param name="Outputs">The output ports, in port order: what each version a pass of it writes carries, and the stage
/// that writes it, at least one.</param>
/// <param name="Summary">What the package renders.</param>
/// <param name="Config">The config schema a pass of it binds its <see cref="RenderGraphPackagePass.Config"/> against,
/// name to field, or <see langword="null"/> when it takes no config.</param>
public sealed record RenderGraphPackage(string Id, IReadOnlyList<RenderGraphPackagePort> Inputs, IReadOnlyList<RenderGraphPackagePort> Outputs, string Summary, IReadOnlyDictionary<string, ShaderConfigField>? Config = null);
/// <summary>The engine packages a host offers graphs, by id.</summary>
public sealed class RenderGraphPackageCatalog {
    /// <summary>The id of the SDF world view: primary traversal, surfaces, ambient occlusion and lighting of one view,
    /// from the instance's camera. The screens it shows are the instance's reads, not ports.</summary>
    public const string SdfWorld = "sdf.world";
    /// <summary>The id of the world's SDF brick pool: brick uploads and carve bakes into one pool the world's views
    /// read. It is world-scoped, one instance for the world, and its output is a buffer, so the views reach it over
    /// buffer edges.</summary>
    public const string SdfBricks = "sdf.bricks";
    /// <summary>The id of the unified overlay: the console, HUD, toasts and cursor drawn over its input.</summary>
    public const string Overlay = "overlay";
    /// <summary>The prefix of a shipped post-process shader set's package id: <c>post.&lt;set id&gt;</c>.</summary>
    public const string PostProcessPrefix = "post.";
    /// <summary>The id of the one placement pass: its base image, with its source reconstructed into a destination rect
    /// over it, an exact copy where the rect has the source's extent, otherwise bilinear at sharpness 0 blending to
    /// clamped Catmull-Rom at sharpness 1. A rect of the whole output resamples the whole source. Its kernel is
    /// <c>Assets/Shaders/Graph/place.comp.hlsl</c>, compiled at build; its config is the rect (<see cref="PlaceRect"/>)
    /// and the sharpness (<see cref="PlaceSharpness"/>).</summary>
    public const string Place = "place";
    /// <summary>The <see cref="Place"/> config field holding the destination rect as fractions of the output's extent:
    /// left, top, width, height.</summary>
    public const string PlaceRect = "rect";
    /// <summary>The <see cref="Place"/> config field holding the reconstruction's sharpness, from 0 (bilinear) to 1
    /// (clamped Catmull-Rom).</summary>
    public const string PlaceSharpness = "sharpness";

    private readonly Dictionary<string, RenderGraphPackage> m_packages;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphPackageCatalog"/> class.</summary>
    /// <param name="packages">The packages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> or one of its entries is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A package has no id, null port lists, a null or malformed port
    /// (<see cref="RenderGraphPackagePort.IsValid"/>), an input port that writes or an output port that reads, or no
    /// output, or two share an id.</exception>
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
                package.Outputs.Any(predicate: static port => port.Reads)
            ) {
                throw new ArgumentException(
                    message: $"Package '{package.Id}' needs an id, well-formed input ports that read and output ports that write, and at least one output.",
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
    /// <summary>Gets the config schema of <see cref="Place"/>: the rect, whole by default, and the sharpness, 0 by
    /// default.</summary>
    public static IReadOnlyDictionary<string, ShaderConfigField> PlaceConfig { get; } = new ReadOnlyDictionary<string, ShaderConfigField>(dictionary: new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) {
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
    });
    /// <summary>Gets the engine's own packages: <see cref="SdfWorld"/>, <see cref="SdfBricks"/>, <see cref="Overlay"/>
    /// and <see cref="Place"/>.</summary>
    public static RenderGraphPackageCatalog Engine { get; } = new(packages: EnginePackages());
    /// <summary>Gets the catalog of a host that offers no package, whose graphs are shader passes alone.</summary>
    public static RenderGraphPackageCatalog None { get; } = new(packages: []);

    /// <summary>Gets the packages this build offers: the engine's own and one per shipped post-process shader set
    /// (<see cref="ShaderSetCatalog.Shipped"/>).</summary>
    public static RenderGraphPackageCatalog Shipped => ShippedCatalog.Value;

    private static readonly Lazy<RenderGraphPackageCatalog> ShippedCatalog = new(valueFactory: static () => WithPostProcess(postProcess: ShaderSetCatalog.Shipped));

    /// <summary>Gets the packages in ordinal id order.</summary>
    public IReadOnlyList<RenderGraphPackage> Packages { get; }

    private static IEnumerable<RenderGraphPackage> EnginePackages() => [
        new RenderGraphPackage(
            Id: SdfWorld,
            Inputs: [],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Summary: "The SDF world as the instance's camera sees it."
        ),
        new RenderGraphPackage(
            Id: SdfBricks,
            Inputs: [],
            Outputs: [BrickPool],
            Summary: "The world's SDF brick pool: brick uploads and carve bakes, which the views read over a buffer edge."
        ),
        new RenderGraphPackage(
            Id: Overlay,
            Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.FragmentSampled)],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ColorAttachmentWrite)],
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
            Summary: "The base image with the source reconstructed into a rect over it, bilinear to clamped Catmull-Rom by sharpness."
        ),
    ];

    /// <summary>Creates the engine's catalog extended with one <c>post.&lt;id&gt;</c> package per shipped post-process
    /// shader set, each reading one image, writing one, and taking its set's config schema. It reads each manifest's
    /// declaration (<see cref="ShaderSetManifest.ReadDeclaration"/>) and none of its bytecode.</summary>
    /// <param name="postProcess">The shipped shader sets.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="postProcess"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">A shipped manifest is malformed or its config schema is invalid.</exception>
    public static RenderGraphPackageCatalog WithPostProcess(ShaderSetCatalog postProcess) {
        ArgumentNullException.ThrowIfNull(argument: postProcess);

        return new RenderGraphPackageCatalog(packages: EnginePackages().Concat(second: postProcess.Ids.Select(selector: id => new RenderGraphPackage(
            Config: (postProcess.TryGetPath(
                id: id,
                path: out var path
            )
                ? ShaderSetManifest.ReadDeclaration(manifestPath: path).Config
                : null),
            Id: (PostProcessPrefix + id),
            Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.FragmentSampled)],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ColorAttachmentWrite)],
            Summary: $"The shipped post-process shader set '{id}' over the input image."
        ))));
    }
    /// <summary>Finds a package by id.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="package">The package, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the catalog declares the id.</returns>
    public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out RenderGraphPackage? package) => m_packages.TryGetValue(
        key: id,
        value: out package
    );
}
