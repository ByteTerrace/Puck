using System.Globalization;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>A world's default render graph, synthesized from its document when it authors no root of its own: the
/// <c>sdf.world</c> producer (<see cref="WorldViewGraphs.WorldInstance"/>), and, when there is anything to draw over it,
/// the root graph (<see cref="WorldViewGraphs.MainInstance"/>) that reads it, places each pane over it with one
/// <c>place</c> pass per <c>views.graphs</c> instance a layout slot names, then runs each <c>render.extensions</c> pass as
/// its <c>post.&lt;id&gt;</c> package in document order, then the <c>overlay</c> package. With nothing to draw over it, the
/// producer is the root. The graph is a document value planned by <see cref="RenderGraphCompiler"/>, the one path every
/// graph takes, so a pass config that does not bind is the compiler's refusal, named by its entry.</summary>
public sealed class WorldRootGraph {
    // The versions and passes the root declares for itself are generated names (WorldViewNames.Root), so none can equal a
    // pane's version or place pass, which take the pane's authored name.
    private static readonly string FrameVersion = WorldViewNames.Root("frame");
    private static readonly string OverlayPass = WorldViewNames.Root(RenderGraphPackageCatalog.Overlay);
    private static readonly string WorldVersion = WorldViewNames.Root(WorldViewGraphs.WorldInstance);

    private const string PostPart = "post";

    private WorldRootGraph(RenderGraphPlan? plan, IReadOnlyDictionary<string, IReadOnlyList<string>> postPasses, IReadOnlyList<string> panes) {
        Plan = plan;
        Panes = panes;
        PostPasses = postPasses;

        var world = new RenderGraphInstance(
            ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
            Name: WorldViewGraphs.WorldInstance,
            Passes: SdfEngineNode.PassLabels.Length,
            Reads: [],
            Refresh: RenderGraphRefresh.EveryFrame
        );

        Instances = ((plan is null)
            ? [world]
            : [
                world,
                new RenderGraphInstance(
                    Name: WorldViewGraphs.MainInstance,
                    Passes: plan.Pipeline.Passes.Count,
                    Reads: [
                        new RenderGraphRead(Producer: WorldViewGraphs.WorldInstance),
                        .. panes.Select(selector: static pane => new RenderGraphRead(Producer: pane)),
                    ],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
            ]);
        Footprints = ((plan is null)
            ? []
            : [new RenderGraphFootprint(
                Consumer: WorldViewGraphs.MainInstance,
                Height: 1.0,
                Producer: WorldViewGraphs.WorldInstance,
                Width: 1.0
            )]);
    }

    /// <summary>Gets the reads the display always shows inside the root: the root showing the world over its whole
    /// extent, or none when the world is the root. A pane's footprint follows its slot, frame by frame.</summary>
    public IReadOnlyList<RenderGraphFootprint> Footprints { get; }
    /// <summary>Gets the synthesized instances: the world producer, then the root graph when there is one, which reads
    /// the world and every pane. The panes themselves are <c>views.graphs</c> rows, which the host adds.</summary>
    public IReadOnlyList<RenderGraphInstance> Instances { get; }
    /// <summary>Gets the <c>views.graphs</c> instances the root places, one <c>place</c> pass each, in the order a layout
    /// slot first names them.</summary>
    public IReadOnlyList<string> Panes { get; }
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
    /// <param name="panes">The <c>views.graphs</c> instances a layout slot names (<see cref="PanesOf"/>), or
    /// <see langword="null"/> for none.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> is <see langword="null"/>.</exception>
    /// <exception cref="WorldRootGraphRefusedException">The graph compiler refused the synthesized graph, such as an
    /// entry's config that does not bind against its set's schema; the message names the entry and the compiler's
    /// code.</exception>
    public static WorldRootGraph Compose(IReadOnlyList<WorldRenderExtensionEntry>? extensions, bool overlay, RenderGraphPackageCatalog packages, IReadOnlyList<string>? panes = null) {
        ArgumentNullException.ThrowIfNull(argument: packages);

        var entries = (extensions ?? []);
        var placed = (panes ?? []);
        var passCount = ((placed.Count + entries.Count) + (overlay ? 1 : 0));
        var postPasses = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);

        if (passCount == 0) {
            return new WorldRootGraph(
                panes: placed,
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

            if (index < placed.Count) {
                var pane = placed[index];

                resources.Add(item: Image(
                    initialization: ShaderPipelineInitialization.External,
                    name: pane
                ));
                passes.Add(item: new RenderGraphPackagePass(
                    Inputs: [
                        new ResourceReference(Name: input),
                        new ResourceReference(Name: pane),
                    ],
                    Name: pane,
                    Outputs: [new ResourceReference(Name: output)],
                    Package: RenderGraphPackageCatalog.Place
                ));
            } else if ((index - placed.Count) < entries.Count) {
                var entryIndex = (index - placed.Count);
                var entry = entries[entryIndex];

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
                    ? WorldViewNames.Root(PostPart, entry.Id)
                    : WorldViewNames.Root(PostPart, entry.Id, (named.Count + 1).ToString(provider: CultureInfo.InvariantCulture)));

                named.Add(item: name);
                entryOf[name] = entryIndex;
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
                    Name: OverlayPass,
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
            panes: placed,
            plan: plan,
            postPasses: postPasses.ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static pair => ((IReadOnlyList<string>)pair.Value),
                keySelector: static pair => pair.Key
            )
        );
    }
    /// <summary>Returns the <c>views.graphs</c> instances a world's layouts place: every instance a slot of any layout
    /// names, in the order a slot first names it, so a layout switch places an instance the root already reads.</summary>
    /// <param name="views">The document's <c>views</c> section.</param>
    /// <returns>The instance names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> PanesOf(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        List<string>? panes = null;

        foreach (var layout in views.Layouts) {
            foreach (var slot in layout.Slots) {
                if (
                    (slot.Instance is { } instance) &&
                    !(panes?.Contains(item: instance) ?? false)
                ) {
                    (panes ??= []).Add(item: instance);
                }
            }
        }

        return (((IReadOnlyList<string>?)panes) ?? []);
    }
    /// <summary>Returns the graphs the runtime installs, parallel to <see cref="Instances"/>: none for the world
    /// producer, and the root graph with its world input bound to the producer and each pane's version to its
    /// instance.</summary>
    /// <returns>The graphs.</returns>
    public IReadOnlyList<RenderGraphRuntimeGraph?> Graphs() => ((Plan is not { } plan)
        ? [null]
        : [
            null,
            new RenderGraphRuntimeGraph(
                Inputs: [
                    new RenderGraphRuntimeInput(
                        Producer: WorldViewGraphs.WorldInstance,
                        Version: WorldVersion
                    ),
                    .. Panes.Select(selector: static pane => new RenderGraphRuntimeInput(
                        Producer: pane,
                        Version: pane
                    )),
                ],
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
    private static string StageVersion(int index) => WorldViewNames.Root(
        "stage",
        index.ToString(provider: CultureInfo.InvariantCulture)
    );
}
/// <summary>A world's default render graph that the graph compiler refused, such as a <c>render.extensions</c> entry
/// whose config does not bind against its shader set's schema. A boot reports it as a refused definition.</summary>
/// <param name="message">The refusal, naming each refused entry and the compiler's code.</param>
public sealed class WorldRootGraphRefusedException(string message) : Exception(message: message);
