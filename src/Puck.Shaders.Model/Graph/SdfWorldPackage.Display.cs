using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The image the composite writes in a view whose graph converts for the display (<see cref="WithDisplay"/>):
    /// its scene light, exposed by the view's exposure, linear, which the bloom pyramid and the display pass read.</summary>
    public const string SceneLight = "scene-light";
    /// <summary>The bloom pyramid's finest level, the scatter the display pass adds to the scene light.</summary>
    public const string Bloom = "bloom";
    /// <summary>The view's last pass in a graph that converts for the display: its scene light sharpened, bloomed, put
    /// through the world's tone curve and encoded on the sRGB curve into the working image (<see cref="Color"/>).</summary>
    public const string Display = "display";
    /// <summary>The display pass's contrast-adaptive sharpen strength, from 0 (none) to 1 (<c>float</c>).</summary>
    public const string DisplaySharpness = "displaySharpness";
    /// <summary>The share, from 0 to 1, of the display pass's output that is the scene light as it is, past the sharpen,
    /// the bloom and the tone curve but not the encode: a framed screen's fidelity (<c>float</c>).</summary>
    public const string DisplayFidelity = "displayFidelity";
    /// <summary>One when the display pass encodes the scene light for the display, zero when it passes a debug view's
    /// picture through, whose values are already display values (<c>uint</c>).</summary>
    public const string DisplayEncode = "displayEncode";
    /// <summary>The tone curve's peak, contrast, linear start and linear length (<c>float4</c>); a zero peak applies no
    /// curve (<c>SdfToneCurve</c>).</summary>
    public const string ToneShape = "toneShape";
    /// <summary>The tone curve's toe power (<c>float</c>).</summary>
    public const string ToneToe = "toneToe";

    /// <summary>The camera's optical tail's interface, shared by the bloom pyramid's passes and the display pass: the
    /// scatter's values, the display pass's values, the two images a pass reads, its sampler and its output.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> OpticsMembers { get; } = [
        Value(name: "bloomMix", type: ShaderValueType.Float),
        Value(name: "bloomCutoff", type: ShaderValueType.Float),
        Value(name: "bloomScatter", type: ShaderValueType.Float),
        Value(name: "bloomRadius", type: ShaderValueType.Float),
        Value(name: DisplaySharpness, type: ShaderValueType.Float),
        Value(name: DisplayFidelity, type: ShaderValueType.Float),
        Value(name: DisplayEncode, type: ShaderValueType.Uint),
        Value(name: ToneToe, type: ShaderValueType.Float),
        Value(name: ToneShape, type: ShaderValueType.Float4),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: "bloomSource", type: ShaderValueType.Float4),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: "bloomFine", type: ShaderValueType.Float4),
        ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "bloomSampler"),
        ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: "bloomOutput", type: ShaderValueType.Float4),
        .. ShaderWorkCounters.Members,
    ];

    /// <summary>Makes a view's graph convert its scene light for the display, as its last stage, so no consumer of the
    /// view's output can read it unconverted: the composite writes <see cref="SceneLight"/>; at Low or High a normalized
    /// six- or five-level bloom pyramid follows from it, its first level at half or a quarter of the output's resolution,
    /// so dynamic render resolution never changes its support; then the <see cref="Display"/> pass writes
    /// <see cref="Color"/>, the working image. A view published as light for a window or a sky layer takes no display
    /// stage: its <see cref="Color"/> stays the light its consumer exposes.</summary>
    /// <param name="fragment">The view's graph, whose composite writes <see cref="Color"/>.</param>
    /// <param name="quality">The bloom's cost gate; <see cref="BloomQuality.Off"/> adds no pyramid.</param>
    /// <returns>The graph with its display stage.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    public static RenderGraphPackageFragment WithDisplay(RenderGraphPackageFragment fragment, BloomQuality quality) {
        ArgumentNullException.ThrowIfNull(fragment);
        var resources = fragment.Resources.Select(selector: static resource => ((resource.Name == Color) ? resource with { Name = SceneLight, Retained = false } : resource)).ToList();
        var passes = fragment.Passes.Select(selector: static pass => pass with {
            Inputs = [.. pass.Inputs.Select(selector: static input => ((input.Name == Color) ? input with { Name = SceneLight } : input))],
            Outputs = [.. pass.Outputs.Select(selector: static output => ((output.Name == Color) ? output with { Name = SceneLight } : output))],
        }).ToList();

        if (quality != BloomQuality.Off) {
            var levels = ((quality == BloomQuality.High) ? 6 : 5);
            var divisor = ((quality == BloomQuality.High) ? 2d : 4d);
            var source = SceneLight;

            for (var level = 0; (level < levels); level++) {
                var name = $"bloom-down-{level}";

                resources.Add(item: Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: name, retained: true) with {
                    Dimensions = ShaderPipelineDimensions.Relative(height: (1d / divisor), width: (1d / divisor)),
                });
                passes.Add(item: Pass(inputs: [source], name: name, outputs: [name]) with { Members = OpticsMembers });
                source = name;
                divisor *= 2d;
            }
            for (var level = (levels - 2); (level >= 0); level--) {
                var name = ((level == 0) ? Bloom : $"bloom-up-{level}");

                divisor = Math.Pow(x: 2d, y: (level + ((quality == BloomQuality.High) ? 1 : 2)));
                resources.Add(item: Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: name, retained: (level != 0)) with {
                    Dimensions = ShaderPipelineDimensions.Relative(height: (1d / divisor), width: (1d / divisor)),
                });
                passes.Add(item: Pass(inputs: [source, $"bloom-down-{level}"], name: $"bloom-up-{level}", outputs: [name]) with { Members = OpticsMembers });
                source = name;
            }
        }
        resources.Add(item: Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Color, retained: false));
        passes.Add(item: Pass(inputs: ((quality == BloomQuality.Off) ? [SceneLight] : [SceneLight, Bloom]), name: Display, outputs: [Color]) with { Members = OpticsMembers });

        return fragment with { Resources = resources, Passes = passes };
    }
}
