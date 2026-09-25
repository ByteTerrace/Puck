using System.Collections.ObjectModel;

namespace Puck.Shaders;

/// <summary>One engine package a graph can name: an id and its ports. Every port carries an image.</summary>
/// <param name="Id">The id a <see cref="RenderGraphPackagePass"/> names.</param>
/// <param name="Inputs">The input ports: how many versions a pass of it reads.</param>
/// <param name="Outputs">The output ports: how many image versions a pass of it writes, at least one.</param>
/// <param name="Summary">What the package renders.</param>
public sealed record RenderGraphPackage(string Id, int Inputs, int Outputs, string Summary);
/// <summary>The engine packages a host offers graphs, by id.</summary>
public sealed class RenderGraphPackageCatalog {
    /// <summary>The id of the SDF world view: primary traversal, surfaces, ambient occlusion, lighting and
    /// composition from the instance's camera. The screens it shows are the instance's reads, not ports.</summary>
    public const string SdfWorld = "sdf.world";
    /// <summary>The id of the unified overlay: the console, HUD, toasts and cursor drawn over its input.</summary>
    public const string Overlay = "overlay";
    /// <summary>The prefix of a shipped post-process shader set's package id: <c>post.&lt;set id&gt;</c>.</summary>
    public const string PostProcessPrefix = "post.";
    /// <summary>The id of the one resample pass: its input reconstructed at its output's extent, an exact copy at the
    /// same extent, otherwise bilinear at sharpness 0 blending to clamped Catmull-Rom at sharpness 1. Its kernel is
    /// <c>Assets/Shaders/Graph/resample.hlsl</c>, a compute pass whose config is its sharpness.</summary>
    public const string Resample = "resample";

    private readonly Dictionary<string, RenderGraphPackage> m_packages;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphPackageCatalog"/> class.</summary>
    /// <param name="packages">The packages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> or one of its entries is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A package has no id, a negative input count, or no output, or two share an
    /// id.</exception>
    public RenderGraphPackageCatalog(IEnumerable<RenderGraphPackage> packages) {
        ArgumentNullException.ThrowIfNull(argument: packages);

        m_packages = new Dictionary<string, RenderGraphPackage>(comparer: StringComparer.Ordinal);

        foreach (var package in packages) {
            ArgumentNullException.ThrowIfNull(argument: package);

            if (
                string.IsNullOrWhiteSpace(value: package.Id) ||
                (package.Inputs < 0) ||
                (package.Outputs < 1)
            ) {
                throw new ArgumentException(
                    message: $"Package '{package.Id}' needs an id, a non-negative input count and at least one output.",
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

    /// <summary>Gets the engine's own packages: <see cref="SdfWorld"/>, <see cref="Overlay"/> and <see cref="Resample"/>.</summary>
    public static RenderGraphPackageCatalog Engine { get; } = new(packages: EnginePackages());

    /// <summary>Gets the packages this build offers: the engine's own and one per shipped post-process shader set
    /// (<see cref="ShaderSetCatalog.Shipped"/>).</summary>
    public static RenderGraphPackageCatalog Shipped => ShippedCatalog.Value;

    private static readonly Lazy<RenderGraphPackageCatalog> ShippedCatalog = new(valueFactory: static () => WithPostProcess(postProcess: ShaderSetCatalog.Shipped));

    /// <summary>Gets the packages in ordinal id order.</summary>
    public IReadOnlyList<RenderGraphPackage> Packages { get; }

    private static IEnumerable<RenderGraphPackage> EnginePackages() => [
        new RenderGraphPackage(
            Id: SdfWorld,
            Inputs: 0,
            Outputs: 1,
            Summary: "The SDF world as the instance's camera sees it."
        ),
        new RenderGraphPackage(
            Id: Overlay,
            Inputs: 1,
            Outputs: 1,
            Summary: "The console, HUD, toasts and cursor drawn over the input image."
        ),
        new RenderGraphPackage(
            Id: Resample,
            Inputs: 1,
            Outputs: 1,
            Summary: "The input image reconstructed at the output's extent, bilinear to clamped Catmull-Rom by sharpness."
        ),
    ];

    /// <summary>Creates the engine's catalog extended with one <c>post.&lt;id&gt;</c> package per shipped post-process
    /// shader set, each reading one image and writing one.</summary>
    /// <param name="postProcess">The shipped shader sets.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="postProcess"/> is <see langword="null"/>.</exception>
    public static RenderGraphPackageCatalog WithPostProcess(ShaderSetCatalog postProcess) {
        ArgumentNullException.ThrowIfNull(argument: postProcess);

        return new RenderGraphPackageCatalog(packages: EnginePackages().Concat(second: postProcess.Ids.Select(selector: static id => new RenderGraphPackage(
            Id: (PostProcessPrefix + id),
            Inputs: 1,
            Outputs: 1,
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
