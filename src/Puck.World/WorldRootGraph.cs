using System.Globalization;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SdfVm;

namespace Puck.World;

/// <summary>A world's default render graph, synthesized from its document when it authors no root of its own: the
/// <c>sdf.world</c> producer (<see cref="WorldViewGraphs.WorldInstance"/>), and, when there is anything to draw over it,
/// the root graph (<see cref="WorldViewGraphs.MainInstance"/>) that reads it and runs each <c>render.extensions</c> pass
/// as its <c>post.&lt;id&gt;</c> package in document order, then the <c>overlay</c> package. With nothing to draw over it,
/// the producer is the root. The graph is a document value planned by <see cref="RenderGraphCompiler"/>, the one path
/// every graph takes, so a pass config that does not bind is the compiler's refusal, named by its entry.</summary>
public sealed class WorldRootGraph {
    private const string FrameVersion = "frame";
    private const string WorldVersion = "world";

    private WorldRootGraph(RenderGraphPlan? plan, IReadOnlyDictionary<string, IReadOnlyList<string>> postPasses) {
        Plan = plan;
        PostPasses = postPasses;

        var world = new RenderGraphInstance(
            ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
            Name: WorldViewGraphs.WorldInstance,
            Passes: SdfEngineNode.PassLabels.Length,
            Reads: [],
            Refresh: RenderGraphRefresh.EveryFrame
        );
        IReadOnlyList<RenderGraphInstance> instances = ((plan is null)
            ? [world]
            : [
                world,
                new RenderGraphInstance(
                    Name: WorldViewGraphs.MainInstance,
                    Passes: plan.Pipeline.Passes.Count,
                    Reads: [new RenderGraphRead(Producer: WorldViewGraphs.WorldInstance)],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
            ]);

        if (!RenderGraphInstanceSet.TryCreate(
            instances: instances,
            refusal: out var refusal,
            set: out var set
        )) {
            throw new InvalidOperationException(message: $"The default render graph's instances were refused: {refusal.Message}");
        }

        Instances = set;
        Footprints = ((plan is null)
            ? []
            : [new RenderGraphFootprint(
                Consumer: WorldViewGraphs.MainInstance,
                Height: 1.0,
                Producer: WorldViewGraphs.WorldInstance,
                Width: 1.0
            )]);
    }

    /// <summary>Gets the reads the display shows inside the root: the root showing the world over its whole
    /// extent, or none when the world is the root.</summary>
    public IReadOnlyList<RenderGraphFootprint> Footprints { get; }
    /// <summary>Gets the instances: the world producer, then the root graph when there is one.</summary>
    public RenderGraphInstanceSet Instances { get; }
    /// <summary>Gets the root graph's plan, or <see langword="null"/> when nothing is drawn over the world and the world
    /// is the root.</summary>
    public RenderGraphPlan? Plan { get; }
    /// <summary>Gets each <c>render.extensions</c> id's passes in the root graph, in document order; an id the document
    /// names more than once has one pass per entry.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PostPasses { get; }
    /// <summary>Gets the name of the instance the display shows and captures read by default.</summary>
    public string Root => ((Plan is null)
        ? WorldViewGraphs.WorldInstance
        : WorldViewGraphs.MainInstance);

    /// <summary>Synthesizes and plans a world's default render graph.</summary>
    /// <param name="extensions">The document's <c>render.extensions</c> entries, or <see langword="null"/> for none.</param>
    /// <param name="overlay">Whether the presentation draws the overlay over the world.</param>
    /// <param name="packages">The packages the host offers: the engine's and one per shipped post-process set.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> is <see langword="null"/>.</exception>
    /// <exception cref="WorldRootGraphRefusedException">The graph compiler refused the synthesized graph, such as an
    /// entry's config that does not bind against its set's schema; the message names the entry and the compiler's
    /// code.</exception>
    public static WorldRootGraph Compose(IReadOnlyList<WorldRenderExtensionEntry>? extensions, bool overlay, RenderGraphPackageCatalog packages) {
        ArgumentNullException.ThrowIfNull(argument: packages);

        var entries = (extensions ?? []);
        var passCount = (entries.Count + (overlay ? 1 : 0));
        var postPasses = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);

        if (passCount == 0) {
            return new WorldRootGraph(
                plan: null,
                postPasses: new Dictionary<string, IReadOnlyList<string>>(comparer: StringComparer.Ordinal)
            );
        }

        var resources = new List<ShaderPipelineResource> {
            Image(
                initialization: ShaderPipelineInitialization.External,
                name: WorldVersion
            ),
        };
        var passes = new List<RenderGraphPackagePass>(capacity: passCount);
        // The entry each pass came from, so a refusal names the document's entry rather than a synthesized pass.
        var entryOf = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < passCount); index++) {
            var input = ((index == 0)
                ? WorldVersion
                : StageVersion(index: index));
            var output = ((index == (passCount - 1))
                ? FrameVersion
                : StageVersion(index: (index + 1)));

            resources.Add(item: Image(
                initialization: ShaderPipelineInitialization.Undefined,
                name: output
            ));

            if (index < entries.Count) {
                var entry = entries[index];

                if (!postPasses.TryGetValue(
                    key: entry.Id,
                    value: out var named
                )) {
                    named = [];
                    postPasses.Add(
                        key: entry.Id,
                        value: named
                    );
                }

                var name = ((named.Count == 0)
                    ? entry.Id
                    : string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"{entry.Id}-{(named.Count + 1)}"
                    ));

                named.Add(item: name);
                entryOf[name] = index;
                passes.Add(item: new RenderGraphPackagePass(
                    Config: entry.Config,
                    Inputs: [new ResourceReference(Name: input)],
                    Name: name,
                    Outputs: [new ResourceReference(Name: output)],
                    Package: (RenderGraphPackageCatalog.PostProcessPrefix + entry.Id)
                ));
            } else {
                passes.Add(item: new RenderGraphPackagePass(
                    Inputs: [new ResourceReference(Name: input)],
                    Name: RenderGraphPackageCatalog.Overlay,
                    Outputs: [new ResourceReference(Name: output)],
                    Package: RenderGraphPackageCatalog.Overlay
                ));
            }
        }

        var definition = new RenderGraphDefinition(
            Name: WorldViewGraphs.MainInstance,
            Outputs: [FrameVersion],
            Packages: passes,
            Resources: resources,
            Schema: RenderGraphSchemas.Graph
        );

        if (!new RenderGraphCompiler(packages: packages).TryCompile(
            definition: definition,
            diagnostics: out var diagnostics,
            plan: out var plan
        )) {
            throw new WorldRootGraphRefusedException(message: string.Join(
                separator: "; ",
                values: diagnostics.Select(selector: diagnostic => (((diagnostic.Name is { } pass) && entryOf.TryGetValue(
                    key: pass,
                    value: out var entryIndex
                ))
                    ? $"render.extensions[{entryIndex}] '{entries[entryIndex].Id}': {diagnostic.Code}: {diagnostic.Message}"
                    : $"the default render graph: {diagnostic.Code}: {diagnostic.Message}"))
            ));
        }

        return new WorldRootGraph(
            plan: plan,
            postPasses: postPasses.ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static pair => ((IReadOnlyList<string>)pair.Value),
                keySelector: static pair => pair.Key
            )
        );
    }
    /// <summary>Returns the graphs the runtime installs, parallel to <see cref="Instances"/>: none for the world
    /// producer, and the root graph with its world input bound to the producer.</summary>
    /// <returns>The graphs.</returns>
    public IReadOnlyList<RenderGraphRuntimeGraph?> Graphs() => ((Plan is not { } plan)
        ? [null]
        : [
            null,
            new RenderGraphRuntimeGraph(
                Inputs: [new RenderGraphRuntimeInput(
                    Producer: WorldViewGraphs.WorldInstance,
                    Version: WorldVersion
                )],
                Pipeline: new CompiledShaderPipeline(
                    plan: plan.Pipeline,
                    shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
                )
            ),
        ]);

    private static ShaderPipelineResource Image(string name, ShaderPipelineInitialization initialization) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: "R8G8B8A8Unorm",
        Initialization: initialization,
        Name: name
    );
    private static string StageVersion(int index) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"stage{index}"
    );
}
/// <summary>A world's default render graph that the graph compiler refused, such as a <c>render.extensions</c> entry
/// whose config does not bind against its shader set's schema. A boot reports it as a refused definition.</summary>
/// <param name="message">The refusal, naming each refused entry and the compiler's code.</param>
public sealed class WorldRootGraphRefusedException(string message) : Exception(message: message);
