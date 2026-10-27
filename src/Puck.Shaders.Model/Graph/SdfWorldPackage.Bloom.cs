using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The optional exported optical scatter image, sampled only by endpoint placement.</summary>
    public const string Bloom = "bloom";
    /// <summary>The complete direct image before a filmed camera's standalone optical combine.</summary>
    public const string BloomDirect = "bloom-direct";

    /// <summary>The normalized bloom filters' independent interface.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> BloomMembers { get; } = [
        Value(name: "bloomMix", type: ShaderValueType.Float),
        Value(name: "bloomCutoff", type: ShaderValueType.Float),
        Value(name: "bloomScatter", type: ShaderValueType.Float),
        Value(name: "bloomRadius", type: ShaderValueType.Float),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: "bloomSource", type: ShaderValueType.Float4),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: "bloomFine", type: ShaderValueType.Float4),
        ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, name: "bloomSampler"),
        ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: "bloomOutput", type: ShaderValueType.Float4),
        .. ShaderWorkCounters.Members,
    ];

    /// <summary>Adds a six-level normalized pyramid after the complete HDR composite. Its first level is
    /// half output resolution at High, quarter at Low; dynamic render resolution never changes its support.</summary>
    /// <param name="fragment">The view's complete HDR graph.</param>
    /// <param name="quality">The optical endpoint's cost gate.</param>
    /// <param name="standalone">Whether the filmed camera needs its own combine instead of root placement.</param>
    /// <returns>The original graph at Off, otherwise the graph exporting its scatter alongside the direct image.</returns>
    public static RenderGraphPackageFragment WithBloom(RenderGraphPackageFragment fragment, BloomQuality quality, bool standalone = false) {
        ArgumentNullException.ThrowIfNull(fragment);
        if (quality == BloomQuality.Off) { return fragment; }
        var levels = ((quality == BloomQuality.High) ? 6 : 5);
        var divisor = ((quality == BloomQuality.High) ? 2d : 4d);
        var resources = fragment.Resources.ToList();
        var passes = fragment.Passes.ToList();
        var source = Color;

        for (var level = 0; (level < levels); level++) {
            var name = $"bloom-down-{level}";

            resources.Add(item: Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: name, retained: true) with {
                Dimensions = ShaderPipelineDimensions.Relative(height: (1d / divisor), width: (1d / divisor)),
            });
            passes.Add(item: Pass(inputs: [source], name: name, outputs: [name]) with { Members = BloomMembers });
            source = name;
            divisor *= 2d;
        }
        for (var level = (levels - 2); (level >= 0); level--) {
            var name = ((level == 0) ? Bloom : $"bloom-up-{level}");

            divisor = Math.Pow(x: 2d, y: (level + ((quality == BloomQuality.High) ? 1 : 2)));
            resources.Add(item: Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: name, retained: (level != 0)) with {
                Dimensions = ShaderPipelineDimensions.Relative(height: (1d / divisor), width: (1d / divisor)),
            });
            passes.Add(item: Pass(inputs: [source, $"bloom-down-{level}"], name: $"bloom-up-{level}", outputs: [name]) with { Members = BloomMembers });
            source = name;
        }
        if (standalone) {
            const string Direct = BloomDirect;

            resources = [.. resources.Select(selector: resource => ((resource.Name == Color) ? resource with { Name = Direct, Retained = true } : resource))];
            passes = [.. passes.Select(selector: pass => pass with {
                Inputs = [.. pass.Inputs.Select(selector: input => ((input.Name == Color) ? input with { Name = Direct } : input))],
                Outputs = [.. pass.Outputs.Select(selector: output => ((output.Name == Color) ? output with { Name = Direct } : output))],
            })];
            resources.Add(item: Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Color, retained: false));
            passes.Add(item: Pass(inputs: [Bloom, Direct], name: "bloom-apply", outputs: [Color]) with { Members = BloomMembers });
        }
        return fragment with { Resources = resources, Passes = passes, OutputVersions = [.. fragment.OutputVersions, Bloom] };
    }
}
