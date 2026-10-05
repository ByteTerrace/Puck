using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The package-owned bank of single-precision light-view depth maps.</summary>
    public const string IndirectLightDepth = "indirectLightDepth";
    /// <summary>The depth publisher's writable bank.</summary>
    public const string IndirectLightDepthWritten = "indirectLightDepthRW";
    /// <summary>The depth-only fragment's final publication pass.</summary>
    public const string LightDepth = "light-depth";
    /// <summary>The conservative footprint of the scheduled depth camera, zero for an ordinary camera.</summary>
    public const string LightSweepRadius = "lightSweepRadius";
    /// <summary>The scheduled map index plus one, zero for an ordinary view.</summary>
    public const string LightMap = "lightMap";
    /// <summary>The current valid map projections and their exact generations.</summary>
    public const string LightMaps = "lightMaps";
    /// <summary>The number of allocated map records in <see cref="LightMaps"/>.</summary>
    public const string LightMapCount = "lightMapCount";

    /// <summary>Creates one camera's depth-only fragment, retaining its traversal scratch and borrowing one bank for
    /// all held/fading maps from the package owner. Traversal storage follows the package's native render extent,
    /// independently of its buffer output's scheduler placeholder.
    /// Surface, ambient, shadow, material lighting, sky and color resolve stages are absent.</summary>
    /// <param name="maps">The held and incoming region capacity.</param>
    /// <returns>The depth-bank producer.</returns>
    public static RenderGraphPackageFragment LightViewFragment(int maps) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: maps);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: maps, other: SdfIndirectLightLayout.MaxMaps);
        var passes = NativeFragment.Passes.Take(count: 6).Where(predicate: static pass => (pass.Name != Parts.Tape))
            .Select(selector: static pass => ((pass.Name == Parts.Primary) ? pass with {
                Inputs = [.. pass.Inputs.Where(predicate: static input => (input.Name != Parts.SegmentTapes))],
                InputAccesses = [.. pass.Inputs.Select(selector: (input, index) => (input, index))
                    .Where(predicate: static pair => (pair.input.Name != Parts.SegmentTapes)).Select(selector: pair => pass.InputAccesses[pair.index])],
            } : pass)).Append(element:
            Pass(inputs: [Parts.Visibility, Parts.CullBounds], name: LightDepth, outputs: [IndirectLightDepth])).ToArray();
        var used = passes.SelectMany(selector: static pass => pass.Inputs.Concat(second: pass.Outputs)).Select(selector: static port => port.Name).ToHashSet(comparer: StringComparer.Ordinal);

        used.Add(item: Parts.Arguments);
        return new RenderGraphPackageFragment(InputVersions: [], OutputVersions: [IndirectLightDepth], Passes: passes,
            Resources: [.. NativeFragment.Resources.Where(predicate: resource => used.Contains(item: resource.Name)).Select(selector: AtRenderExtent),
                new ShaderPipelineResource(Name: IndirectLightDepth, Kind: ShaderPipelineResourceKind.Buffer,
                    StrideBytes: sizeof(float), SizeBytes: Math.Max(val1: sizeof(float), val2: (((ulong)maps) * SdfIndirectLightLayout.MapBytes)))]);
    }
    /// <summary>Adds the explicit depth-bank dependency to the passes that look up residency light visibility.</summary>
    /// <param name="fragment">The consumer fragment.</param>
    /// <param name="maps">The allocation's map capacity.</param>
    /// <returns>The consumer with its external buffer edge.</returns>
    public static RenderGraphPackageFragment WithLightViews(RenderGraphPackageFragment fragment, int maps) => fragment with {
        InputVersions = [.. fragment.InputVersions, IndirectLightDepth],
        Resources = [.. fragment.Resources, new ShaderPipelineResource(Name: IndirectLightDepth,
            Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: Math.Max(val1: sizeof(float), val2: (((ulong)maps) * SdfIndirectLightLayout.MapBytes)),
            StrideBytes: sizeof(float), Initialization: ShaderPipelineInitialization.External)],
        Passes = [.. fragment.Passes.Select(selector: static pass => ((pass.Name is Parts.Views or IndirectShade) ? pass with {
            Inputs = [.. pass.Inputs, new ResourceReference(Name: IndirectLightDepth)],
            InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
        } : pass))],
    };
}
