using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The residency's environment map and reduction, exporting both actual buffers. The host supplies the typed
/// fragment to the engine's environment package; consumers select each buffer through an explicit graph read.</summary>
public static class SdfSkyEnvironmentGraph {
    /// <summary>The map-producing pass and retained two-plane map version.</summary>
    public const string Map = "map";
    /// <summary>The reduction pass and exported coefficient version.</summary>
    public const string Coefficients = "coefficients";
    /// <summary>The external coefficient dependency in each consuming view or finite lighting solve.</summary>
    public const string Input = "skyEnvironment";
    /// <summary>The external map dependency in each consuming view or finite lighting solve.</summary>
    public const string MapInput = "skyEnvironmentMap";

    /// <summary>Gets the graph whose two buffers are borrowed from one residency.</summary>
    public static RenderGraphPackageFragment Fragment { get; } = new(
        InputVersions: [], OutputVersions: [Coefficients, Map],
        Resources: [
            new(Name: Map, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: SdfSkyEnvironment.MapBytes,
                StrideBytes: sizeof(uint) * 2),
            new(Name: Coefficients, Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: SdfSkyEnvironment.CoefficientBytes,
                StrideBytes: sizeof(float) * 4),
        ],
        Passes: [
            new(Name: Map, Inputs: [], InputAccesses: [], Outputs: [new ResourceReference(Map)],
                OutputAccesses: [RenderGraphPortAccess.ComputeWrite], CountsKernelWork: true, Members: SdfKernelInterfaces.EnvironmentMembers),
            new(Name: Coefficients, Inputs: [new ResourceReference(Map)], InputAccesses: [RenderGraphPortAccess.ComputeRead],
                Outputs: [new ResourceReference(Coefficients)], OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                CountsKernelWork: true, Members: SdfKernelInterfaces.EnvironmentMembers),
        ]);

    /// <summary>Copies the canonical view's image reads for its shared environment. A world-derived image feeds back
    /// its previous publication: its current render consumes an environment itself. Independent source images retain
    /// their authored current or previous read, and the existing view footprints still demand their producers.</summary>
    /// <param name="set">The existing, validated instance graph before its environment producers are appended.</param>
    /// <param name="view">The canonical view whose screen-source names the environment uses.</param>
    /// <returns>The environment's image reads, without a current dependency back through a world view.</returns>
    public static IReadOnlyList<RenderGraphRead> ReadsOf(RenderGraphInstanceSet set, string view) {
        var world = new bool[set.Instances.Count];
        foreach (var index in set.Order) {
            world[index] = set.Instances[index].ExternalPackage == RenderGraphPackageCatalog.SdfWorld;
            foreach (var read in set.Reads[index]) {
                if (!read.PreviousFrame && world[read.Producer]) { world[index] = true; }
            }
        }
        var source = set.Instances[set.IndexOf(name: view)];
        return [.. source.Reads.Where(static read => read.Kind == ShaderPipelineResourceKind.Image)
            .Select(read => read with { PreviousFrame = read.PreviousFrame || read.Producer == view || world[set.IndexOf(name: read.Producer)] })];
    }

    /// <summary>Adds the shared environment dependency to the view's lighting and composition passes.</summary>
    /// <param name="fragment">The selected view fragment.</param>
    /// <returns>The same passes and storages with their current environment dependency.</returns>
    public static RenderGraphPackageFragment WithEnvironment(RenderGraphPackageFragment fragment) => fragment with {
        InputVersions = [.. fragment.InputVersions, Input, MapInput],
        Resources = [.. fragment.Resources, new ShaderPipelineResource(Name: Input,
            Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: SdfSkyEnvironment.CoefficientBytes,
            StrideBytes: sizeof(float) * 4, Initialization: ShaderPipelineInitialization.External),
            new ShaderPipelineResource(Name: MapInput, Kind: ShaderPipelineResourceKind.Buffer,
                SizeBytes: SdfSkyEnvironment.MapBytes, StrideBytes: sizeof(uint) * 2,
                Initialization: ShaderPipelineInitialization.External)],
        Passes = [.. fragment.Passes.Select(static pass => pass.Name is SdfWorldPackage.Parts.Views or SdfWorldPackage.Parts.Composite
            ? pass with { Inputs = [.. pass.Inputs, new ResourceReference(Input), new ResourceReference(MapInput)],
                InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead] }
            : pass)],
    };
}
