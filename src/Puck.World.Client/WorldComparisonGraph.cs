using System.Globalization;
using System.Text.Json;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>A comparison display is an ordinary graph instance after the world's live root. Holding or measuring
/// captures that live instance through its existing capture target, so comparisons never capture themselves.</summary>
public static class WorldComparisonGraph {
    /// <summary>The generated instance which displays every active seat's comparison.</summary>
    public static string Root { get; } = WorldViewNames.Root(parts: ["compare"]);

    private const string Image = "image";
    private const string Live = "live";

    private static readonly string[] Sources = Enumerable.Range(count: PlayerRoster.MaxSlots, start: 0)
        .Select(selector: static slot => WorldViewNames.Root(parts: ["compare", "held", slot.ToString(provider: CultureInfo.InvariantCulture)])).ToArray();

    /// <summary>Returns the generated upload instance and place pass name of one zero-based seat.</summary>
    /// <param name="slot">The zero-based seat.</param>
    public static string Source(int slot) => Sources[slot];
    /// <summary>Returns the seat named by a generated upload or pass, or minus one for another name.</summary>
    /// <param name="source">The upload instance or place pass name.</param>
    public static int SeatOf(string source) => Array.IndexOf(array: Sources, value: source);
    /// <summary>Appends active holds and their comparison graph to a composed world. With comparison off, returns the
    /// original set and graph list without creating a graph, source or GPU resource.</summary>
    /// <param name="comparison">The current holds and modes.</param>
    /// <param name="set">The live instance set, replaced with the wrapped set.</param>
    /// <param name="graphs">The parallel compiled-graph list, replaced with the wrapped list.</param>
    /// <param name="root">The live root, replaced with the comparison root.</param>
    /// <exception cref="WorldRootGraphRefusedException">The wrapped instances fail graph validation.</exception>
    public static void Append(WorldFrameComparison comparison, ref RenderGraphInstanceSet set,
        ref IReadOnlyList<RenderGraphRuntimeGraph?> graphs, ref string root) {
        if (!comparison.Active) { return; }
        var active = Enumerable.Range(count: PlayerRoster.MaxSlots, start: 0)
            .Where(predicate: slot => (comparison.Seat(slot: slot) is { Mode: not WorldCompareMode.Off })).ToArray();
        var live = root;
        var instances = set.Instances.ToList();
        var composed = graphs.ToList();
        var resources = new List<ShaderPipelineResource> { Resource(external: true, name: Live) };
        var passes = new List<RenderGraphPackagePass>();
        var inputs = new List<RenderGraphRuntimeInput> { new(Producer: live, Version: Live) };
        var reads = new List<RenderGraphRead> { new(Producer: live) };

        for (var index = 0; (index < active.Length); index++) {
            var slot = active[index];
            var source = Source(slot: slot);
            var snapshot = comparison.Seat(slot: slot)!;
            using var hold = JsonDocument.Parse(json: snapshot.Sequence.ToString(provider: CultureInfo.InvariantCulture));
            using var config = JsonDocument.Parse(json: string.Create(provider: CultureInfo.InvariantCulture,
                handler: $"{{\"{RenderGraphPackageCatalog.PlaceCompareMode}\":{((uint)snapshot.Mode)},\"{RenderGraphPackageCatalog.PlaceWipe}\":{snapshot.Wipe}}}"));

            instances.Add(item: RenderGraphInstance.Source(
                name: source, producer: WorldFrameComparison.SourceProducer,
                settings: new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) {
                    ["hold"] = hold.RootElement.Clone(),
                }));
            composed.Add(item: null);
            resources.Add(item: Resource(external: true, name: source));
            inputs.Add(item: new RenderGraphRuntimeInput(Producer: source, Version: source));
            reads.Add(item: new RenderGraphRead(Producer: source));
            var previous = ((index == 0) ? Live : (Source(slot: active[(index - 1)]) + "$image"));
            var output = ((index == (active.Length - 1)) ? Image : (source + "$image"));

            resources.Add(item: Resource(external: false, name: output));
            passes.Add(item: new RenderGraphPackagePass(Name: source, Package: RenderGraphPackageCatalog.Place,
                Config: config.RootElement.Clone(),
                Inputs: [new ResourceReference(Name: previous), new ResourceReference(Name: source)],
                Outputs: [new ResourceReference(Name: output)]));
        }
        var plan = new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: new RenderGraphDefinition(
            Name: Root, Outputs: [Image], Packages: passes, Resources: resources, Schema: RenderGraphSchemas.Graph));

        instances.Add(item: new RenderGraphInstance(Name: Root, Reads: reads, Passes: passes.Count, Refresh: RenderGraphRefresh.EveryFrame));
        composed.Add(item: new RenderGraphRuntimeGraph(
            Pipeline: new CompiledShaderPipeline(plan: plan.Pipeline, shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)),
            Inputs: inputs));
        if (!RenderGraphInstanceSet.TryCreate(instances: instances, nestingDepth: set.NestingDepth, refusal: out var refusal, set: out var wrapped)) {
            throw new WorldRootGraphRefusedException(message: $"comparison graph: {refusal.Message}");
        }
        set = wrapped;
        graphs = composed;
        root = Root;
    }
    /// <summary>Demands the live display and the active immutable uploads through the comparison root.</summary>
    /// <param name="comparison">The current holds and modes.</param>
    /// <param name="liveRoot">The root shown when comparisons are off.</param>
    /// <returns>The ordinary graph footprints of the displayed comparisons.</returns>
    public static IEnumerable<RenderGraphFootprint> Footprints(WorldFrameComparison comparison, string liveRoot) {
        if (!comparison.Active) { yield break; }
        yield return new RenderGraphFootprint(Consumer: Root, Producer: liveRoot, Width: 1, Height: 1);
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (comparison.Seat(slot: slot) is { Mode: not WorldCompareMode.Off }) {
                yield return new RenderGraphFootprint(Consumer: Root, Producer: Source(slot: slot), Width: 1, Height: 1);
            }
        }
    }

    private static ShaderPipelineResource Resource(string name, bool external) => new(
        Name: name, Format: RenderGraphPackageCatalog.WorkingFormat.ToString(), Dimensions: ShaderPipelineDimensions.Relative(),
        Initialization: (external ? ShaderPipelineInitialization.External : ShaderPipelineInitialization.Undefined));
}
