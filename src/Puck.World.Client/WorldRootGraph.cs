using System.Globalization;
using System.Text.Json;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>A world's default render graph, synthesized from its document when it authors no root of its own: the
/// <c>sdf.world</c> producers — <see cref="WorldViewGraphs.WorldInstance"/> for the first view and one more per later view
/// the world can compose (<see cref="WorldViewNames.World"/>) — and, when there is anything to draw over the world, the
/// root graph (<see cref="WorldViewGraphs.MainInstance"/>) that reads them. The root places each view's output into its
/// rect with one <c>place</c> pass per view (<c>main$view$&lt;n&gt;</c>), the first writing the letterbox color outside
/// its rect (<see cref="RenderGraphPackageCatalog.PlaceLetterbox"/>) so pixels no view covers show it, each pane over them with one <c>place</c> pass
/// per <c>views.graphs</c> instance a layout slot names, then runs each <c>views.post</c> row as a pass of its
/// post-process package, named by the row, in document order, then the <c>overlay</c> package. With one view and nothing to draw
/// over it, the producer is the root. The graph is a document value planned by <see cref="RenderGraphCompiler"/>, the one path every
/// graph takes, so a pass config that does not bind is the compiler's refusal, named by its row.</summary>
public sealed class WorldRootGraph {
    // The versions and passes the root declares for itself are generated names (WorldViewNames.Root), so none can equal a
    // pane's version or place pass, which take the pane's authored name.
    private static readonly string FrameVersion = WorldViewNames.Root("frame");
    private static readonly string OverlayPass = WorldViewNames.Root(RenderGraphPackageCatalog.Overlay);
    private static readonly string WorldVersion = WorldViewNames.Root(WorldViewGraphs.WorldInstance);
    private static readonly JsonElement FirstViewConfig = JsonDocument.Parse(json: $$"""{ "{{RenderGraphPackageCatalog.PlaceLetterbox}}": 1 }""").RootElement.Clone();

    private const string ViewPart = "view";

    private WorldRootGraph(RenderGraphPlan? plan, IReadOnlyList<WorldViewPostPass> post, IReadOnlyList<string> panes, int views) {
        Plan = plan;
        Panes = panes;
        Post = post;
        Views = views;
        // One view is the world itself, which the root places with no pass of its own.
        ViewPasses = ((views > 1)
            ? [.. Enumerable.Range(count: views, start: 1).Select(selector: ViewPass)]
            : []);

        var producers = new RenderGraphInstance[views];

        for (var view = 0; (view < views); view++) {
            producers[view] = new RenderGraphInstance(
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Name: ProducerOf(view: view),
                Passes: SdfEngineNode.PassLabels.Length,
                Reads: [],
                Refresh: RenderGraphRefresh.EveryFrame
            );
        }

        Producers = producers;
        Instances = ((plan is null)
            ? producers
            : [
                .. producers,
                new RenderGraphInstance(
                    Name: WorldViewGraphs.MainInstance,
                    Passes: plan.Pipeline.Passes.Count,
                    Reads: [
                        .. producers.Select(selector: static producer => new RenderGraphRead(Producer: producer.Name)),
                        .. panes.Select(selector: static pane => new RenderGraphRead(Producer: pane)),
                    ],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
            ]);
        // With one view the root shows the world over its whole extent. With more, each view's footprint follows its rect
        // at its render scale, frame by frame, as a pane's follows its slot.
        Footprints = (((plan is null) || (views > 1))
            ? []
            : [new RenderGraphFootprint(
                Consumer: WorldViewGraphs.MainInstance,
                Height: 1.0,
                Producer: WorldViewGraphs.WorldInstance,
                Width: 1.0
            )]);
    }

    /// <summary>Gets the reads the display always shows inside the root: with one view, the root showing the world over
    /// its whole extent, or none when the world is the root. A view's footprint, with more than one view, and a pane's
    /// follow their rects, frame by frame.</summary>
    public IReadOnlyList<RenderGraphFootprint> Footprints { get; }
    /// <summary>Gets the synthesized instances: the world producers (<see cref="Producers"/>), then the root graph when
    /// there is one, which reads every producer and every pane. The panes themselves are <c>views.graphs</c> rows, which
    /// the host adds.</summary>
    public IReadOnlyList<RenderGraphInstance> Instances { get; }
    /// <summary>Gets the world producers, one per view, in view order: <see cref="WorldViewGraphs.WorldInstance"/> first.</summary>
    public IReadOnlyList<RenderGraphInstance> Producers { get; }
    /// <summary>Gets the views the graph places: the most a world's layouts compose (<see cref="ViewsOf"/>).</summary>
    public int Views { get; }
    /// <summary>Gets the root graph's place pass for each view, in view order, each also the name of the version its
    /// source is bound under; empty when the graph places no view.</summary>
    public IReadOnlyList<string> ViewPasses { get; }
    /// <summary>Gets the <c>views.graphs</c> instances the root places, one <c>place</c> pass each, in the order a layout
    /// slot first names them.</summary>
    public IReadOnlyList<string> Panes { get; }
    /// <summary>Gets the root graph's plan, or <see langword="null"/> when nothing is drawn over the world and the world
    /// is the root.</summary>
    public RenderGraphPlan? Plan { get; }
    /// <summary>Gets the <c>views.post</c> rows the root graph runs, in document order, each a pass named by its row;
    /// empty when the root runs none.</summary>
    public IReadOnlyList<WorldViewPostPass> Post { get; }
    /// <summary>Gets the name of the instance the display shows and captures read by default: the root graph whenever
    /// there is one, which there always is with more than one view.</summary>
    public string Root => ((Plan is null)
        ? WorldViewGraphs.WorldInstance
        : WorldViewGraphs.MainInstance);

    /// <summary>Synthesizes and plans a world's default render graph.</summary>
    /// <param name="post">The document's <c>views.post</c> rows, or <see langword="null"/> for none.</param>
    /// <param name="overlay">Whether the presentation draws the overlay over the world.</param>
    /// <param name="packages">The packages the host offers.</param>
    /// <param name="panes">The <c>views.graphs</c> instances a layout slot names (<see cref="PanesOf"/>), or
    /// <see langword="null"/> for none.</param>
    /// <param name="views">The most views a layout composes (<see cref="ViewsOf"/>); with more than one, the root places
    /// each.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="views"/> is below one or above
    /// <see cref="SdfWorldEngine.MaxViewports"/>.</exception>
    /// <exception cref="WorldRootGraphRefusedException">The graph compiler refused the synthesized graph, such as an
    /// row's config that does not bind against its package's schema; the message names the row and the compiler's
    /// code.</exception>
    public static WorldRootGraph Compose(IReadOnlyList<WorldViewPostPass>? post, bool overlay, RenderGraphPackageCatalog packages, IReadOnlyList<string>? panes = null, int views = 1) {
        ArgumentNullException.ThrowIfNull(argument: packages);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: views
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: SdfWorldEngine.MaxViewports,
            value: views
        );

        var entries = (post ?? []);
        var placed = (panes ?? []);
        // One view is the world itself, which needs no place pass of its own.
        var viewCount = ((views > 1)
            ? views
            : 0);
        var passCount = (((viewCount + placed.Count) + entries.Count) + (overlay ? 1 : 0));

        if (passCount == 0) {
            return new WorldRootGraph(
                panes: placed,
                plan: null,
                post: entries,
                views: views
            );
        }

        var resources = new List<ShaderPipelineResource> {
            Image(
                initialization: ShaderPipelineInitialization.External,
                name: WorldVersion
            ),
        };
        var passes = new List<RenderGraphPackagePass>(capacity: passCount);
        // The row each post pass came from, so a refusal names the document's row rather than a synthesized pass.
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

            if (index < viewCount) {
                var view = ViewPass(view: (index + 1));

                resources.Add(item: Image(
                    initialization: ShaderPipelineInitialization.External,
                    name: view
                ));
                passes.Add(item: new RenderGraphPackagePass(
                    // The first view is placed over nothing the display shows, so outside its rect it writes the
                    // letterbox color, which every later view's place pass keeps as its base.
                    Config: ((index == 0)
                        ? FirstViewConfig
                        : null),
                    Inputs: [
                        new ResourceReference(Name: input),
                        new ResourceReference(Name: view),
                    ],
                    Name: view,
                    Outputs: [new ResourceReference(Name: output)],
                    Package: RenderGraphPackageCatalog.Place
                ));
            } else if ((index - viewCount) < placed.Count) {
                var pane = placed[(index - viewCount)];

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
            } else if ((index - (viewCount + placed.Count)) < entries.Count) {
                var entryIndex = (index - (viewCount + placed.Count));
                var entry = entries[entryIndex];

                entryOf[entry.Name] = entryIndex;
                passes.Add(item: new RenderGraphPackagePass(
                    Config: entry.Config,
                    Inputs: [new ResourceReference(Name: input)],
                    Name: entry.Name,
                    Outputs: [new ResourceReference(Name: output)],
                    Package: entry.Package
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
                    ? $"views.post[{entryIndex}] '{entries[entryIndex].Name}': {diagnostic.Code}: {diagnostic.Message}"
                    : $"the default render graph: {diagnostic.Code}: {diagnostic.Message}"))
            ));
        }

        return new WorldRootGraph(
            panes: placed,
            plan: plan,
            post: entries,
            views: views
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
    /// <summary>Returns the views a world's layouts compose at most: the most slots of any <c>views.layouts</c> row that
    /// name no instance, or <see cref="PlayerRoster.MaxSlots"/> for the built-in seat ladder, whichever is larger, and at
    /// most <see cref="SdfWorldEngine.MaxViewports"/>.</summary>
    /// <param name="views">The document's <c>views</c> section.</param>
    /// <returns>The view count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="views"/> is <see langword="null"/>.</exception>
    public static int ViewsOf(WorldViewDefaults views) {
        ArgumentNullException.ThrowIfNull(argument: views);

        var most = PlayerRoster.MaxSlots;

        foreach (var layout in views.Layouts) {
            most = Math.Max(
                val1: most,
                val2: layout.Slots.Count(predicate: static slot => (slot.Instance is null))
            );
        }

        return Math.Min(
            val1: most,
            val2: SdfWorldEngine.MaxViewports
        );
    }
    /// <summary>Returns the name of the world producer that renders a view.</summary>
    /// <param name="view">The 0-based view.</param>
    /// <returns><see cref="WorldViewGraphs.WorldInstance"/> for view 0, else <see cref="WorldViewNames.World"/> of the
    /// 1-based view.</returns>
    public static string ProducerOf(int view) => ((view == 0)
        ? WorldViewGraphs.WorldInstance
        : WorldViewNames.World(view: (view + 1)));
    /// <summary>Returns the graphs the runtime installs, parallel to <see cref="Instances"/>: none for the world
    /// producers, and the root graph with its world input bound to the first producer, each view's version to its
    /// view's producer (the first view's a second version of the first producer), and each pane's version to its
    /// instance.</summary>
    /// <returns>The graphs.</returns>
    public IReadOnlyList<RenderGraphRuntimeGraph?> Graphs() => ((Plan is not { } plan)
        ? [.. Producers.Select(selector: static _ => ((RenderGraphRuntimeGraph?)null))]
        : [
            .. Producers.Select(selector: static _ => ((RenderGraphRuntimeGraph?)null)),
            new RenderGraphRuntimeGraph(
                Inputs: [
                    new RenderGraphRuntimeInput(
                        Producer: WorldViewGraphs.WorldInstance,
                        Version: WorldVersion
                    ),
                    .. ViewPasses.Select(selector: static (view, index) => new RenderGraphRuntimeInput(
                        Producer: ProducerOf(view: index),
                        Version: view
                    )),
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
    private static string ViewPass(int view) => WorldViewNames.Root(
        ViewPart,
        view.ToString(provider: CultureInfo.InvariantCulture)
    );
}
/// <summary>A world's default render graph that the graph compiler refused, such as a <c>views.post</c> row whose
/// config does not bind against its package's schema. A boot reports it as a refused definition.</summary>
/// <param name="message">The refusal, naming each refused row and the compiler's code.</param>
public sealed class WorldRootGraphRefusedException(string message) : Exception(message: message);
