using System.Globalization;
using System.Text.Json;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>A world's default render graph, synthesized from its document when it authors no root of its own: the
/// <c>sdf.world</c> producers — <see cref="WorldViewGraphs.WorldInstance"/> for the first view and one more per later view
/// the world can compose (<see cref="WorldViewNames.World"/>) — and, when there is anything to draw over the world, the
/// root graph (<see cref="WorldViewGraphs.MainInstance"/>) that reads them. The root places each view's output into its
/// rect with one <c>place</c> pass per view (<c>main$view$&lt;n&gt;</c>), the first writing the letterbox color outside
/// its rect (<see cref="RenderGraphPackageCatalog.PlaceLetterbox"/>) so pixels no view covers show it. When
/// <c>render.tonemap</c> is <c>Filmic</c>, each view's place pass tonemaps the view it reconstructs
/// (<see cref="RenderGraphPackageCatalog.PlaceTonemap"/>), so the scene is tonemapped where it enters the frame and the
/// letterbox color, written beside it, reaches the display exact; with one view the root then places that view too, as it
/// does when a temporally resolved view sharpens, which its place pass applies (<see cref="Sharpens"/>).
/// Then the root places each pane over the scene with one <c>place</c> pass per <c>views.graphs</c> instance a layout
/// slot names, and runs each <c>views.post</c> row as a pass of its post-process package, named by the row, in document
/// order. That image is the scene (<see cref="Scene"/>): the world as the editor holds and compares it. A presentation
/// that draws the overlay draws it in an instance of its own over the scene (<see cref="OverlayInstance"/>, appended by
/// <see cref="AppendOverlay"/>), which is then the display's root, so whatever is composed between them, an editor
/// comparison among them, sits under the HUD, the console and the cursor. A pane is display-referred, its own shader's
/// tonemap included, so no pane is tonemapped by the root, and the HUD composes over the finished frame and is never
/// tonemapped. With one view and nothing to draw or tonemap over it, the producer is the scene. The graphs are document
/// values planned by <see cref="RenderGraphCompiler"/>, the one path every graph takes, so a pass config that does not
/// bind is the compiler's refusal, named by its row.</summary>
public sealed class WorldRootGraph {
    // The versions and passes the root declares for itself are generated names (WorldViewNames.Root), so none can equal a
    // pane's version or place pass, which take the pane's authored name.
    private static readonly string FrameVersion = WorldViewNames.Root("frame");
    private static readonly string OverlayPass = WorldViewNames.Root(RenderGraphPackageCatalog.Overlay);
    private static readonly string SceneVersion = WorldViewNames.Root("scene");
    private static readonly string WorldVersion = WorldViewNames.Root(WorldViewGraphs.WorldInstance);
    private static readonly JsonElement FirstViewConfig = JsonDocument.Parse(json: $$"""{ "{{RenderGraphPackageCatalog.PlaceLetterbox}}": 1 }""").RootElement.Clone();
    private static readonly JsonElement FirstTonemappedViewConfig = JsonDocument.Parse(json: $$"""{ "{{RenderGraphPackageCatalog.PlaceLetterbox}}": 1, "{{RenderGraphPackageCatalog.PlaceTonemap}}": 1 }""").RootElement.Clone();
    private static readonly JsonElement TonemappedViewConfig = JsonDocument.Parse(json: $$"""{ "{{RenderGraphPackageCatalog.PlaceTonemap}}": 1 }""").RootElement.Clone();

    private const string ViewPart = "view";

    // The overlay's one pipeline, so each composition hands the runtime the same pipeline and moving what the overlay
    // draws over (a comparison turned on or off) rebinds its input and builds nothing.
    private readonly CompiledShaderPipeline? m_overlayPipeline;

    private WorldRootGraph(RenderGraphPlan? plan, RenderGraphPlan? overlay, IReadOnlyList<WorldViewPostPass> post, IReadOnlyList<string> panes, int views, WorldTonemap tonemap, bool sharpens) {
        Plan = plan;
        OverlayPlan = overlay;
        m_overlayPipeline = ((overlay is null)
            ? null
            : new CompiledShaderPipeline(
                plan: overlay.Pipeline,
                shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
            ));
        Panes = panes;
        Post = post;
        Tonemap = tonemap;
        Sharpens = sharpens;
        Views = views;
        ViewPasses = (PlacesViews(sharpens: sharpens, tonemap: tonemap, views: views)
            ? [.. Enumerable.Range(count: views, start: 1).Select(selector: ViewPass)]
            : []);

        var producers = new RenderGraphInstance[views];

        for (var view = 0; (view < views); view++) {
            producers[view] = new RenderGraphInstance(
                ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Name: ProducerOf(view: view),
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
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
        // A root that places no view shows the world over its whole extent. A placed view's footprint follows its rect at
        // its render scale, frame by frame, as a pane's follows its slot.
        Footprints = (((plan is null) || (ViewPasses.Count > 0))
            ? []
            : [new RenderGraphFootprint(
                Consumer: WorldViewGraphs.MainInstance,
                Height: 1.0,
                Producer: WorldViewGraphs.WorldInstance,
                Width: 1.0
            )]);
    }

    /// <summary>Gets the instance a presentation that draws the overlay draws it in, over the scene or whatever is
    /// composed over the scene (<see cref="AppendOverlay"/>): <c>main$overlay</c>.</summary>
    public static string OverlayInstance { get; } = WorldViewNames.Root(RenderGraphPackageCatalog.Overlay);

    /// <summary>Gets the reads the display always shows inside the root: when the root places no view, the root showing
    /// the world over its whole extent, or none when the world is the root. A placed view's footprint and a pane's follow
    /// their rects, frame by frame. The overlay's read is <see cref="OverlayFootprint"/>, over whatever it draws on.</summary>
    public IReadOnlyList<RenderGraphFootprint> Footprints { get; }
    /// <summary>Gets the synthesized instances of the scene: the world producers (<see cref="Producers"/>), then the root
    /// graph when there is one, which reads every producer and every pane. The panes themselves are <c>views.graphs</c>
    /// rows, which the host adds, and the overlay's instance is appended over the scene
    /// (<see cref="AppendOverlay"/>).</summary>
    public IReadOnlyList<RenderGraphInstance> Instances { get; }
    /// <summary>Gets the world producers, one per view, in view order: <see cref="WorldViewGraphs.WorldInstance"/> first.</summary>
    public IReadOnlyList<RenderGraphInstance> Producers { get; }
    /// <summary>Gets the views the graph places: the most a world's layouts compose (<see cref="ViewsOf"/>).</summary>
    public int Views { get; }
    /// <summary>Gets the root graph's place pass for each view, in view order, each also the name of the version its
    /// source is bound under: one per view with more than one view, a tonemap or a sharpen, and none otherwise, when the
    /// world's one view is the root's base as it is.</summary>
    public IReadOnlyList<string> ViewPasses { get; }
    /// <summary>Gets the <c>views.graphs</c> instances the root places, one <c>place</c> pass each, in the order a layout
    /// slot first names them.</summary>
    public IReadOnlyList<string> Panes { get; }
    /// <summary>Gets the root graph's plan, or <see langword="null"/> when nothing is drawn into the scene over the world
    /// and the world is the scene.</summary>
    public RenderGraphPlan? Plan { get; }
    /// <summary>Gets the overlay instance's plan, one <c>overlay</c> pass over the image it is bound to, or
    /// <see langword="null"/> when the presentation draws no overlay.</summary>
    public RenderGraphPlan? OverlayPlan { get; }
    /// <summary>Gets whether the presentation draws the overlay, in <see cref="OverlayInstance"/>.</summary>
    public bool Overlays => (OverlayPlan is not null);
    /// <summary>Gets the <c>views.post</c> rows the root graph runs, in document order, each a pass named by its row;
    /// empty when the root runs none.</summary>
    public IReadOnlyList<WorldViewPostPass> Post { get; }
    /// <summary>Gets the tonemap the root graph applies: <see cref="WorldTonemap.Filmic"/> tonemaps each view as its place
    /// pass reconstructs it, and nothing else; <see cref="WorldTonemap.None"/> tonemaps nothing.</summary>
    public WorldTonemap Tonemap { get; }
    /// <summary>Gets whether a temporally resolved view sharpens: its place pass applies the contrast-adaptive sharpen at
    /// the rect's own extent, so the root places even a lone view.</summary>
    public bool Sharpens { get; }
    /// <summary>Gets the name of the instance holding the scene, the world image before the overlay: the root graph
    /// whenever there is one, which there always is with more than one view, else the world producer.</summary>
    public string Scene => ((Plan is null)
        ? WorldViewGraphs.WorldInstance
        : WorldViewGraphs.MainInstance);
    /// <summary>Gets the name of the instance the display shows and captures read by default when nothing is composed
    /// between the scene and the overlay: <see cref="OverlayInstance"/> when the presentation draws the overlay, else
    /// <see cref="Scene"/>.</summary>
    public string Root => (Overlays
        ? OverlayInstance
        : Scene);

    /// <summary>Synthesizes and plans a world's default render graph.</summary>
    /// <param name="post">The document's <c>views.post</c> rows, or <see langword="null"/> for none.</param>
    /// <param name="overlay">Whether the presentation draws the overlay, in an instance of its own over the scene.</param>
    /// <param name="packages">The packages the host offers.</param>
    /// <param name="panes">The <c>views.graphs</c> instances a layout slot names (<see cref="PanesOf"/>), or
    /// <see langword="null"/> for none.</param>
    /// <param name="views">The most views a layout composes (<see cref="ViewsOf"/>); with more than one, with a tonemap or
    /// with a sharpen, the root places each.</param>
    /// <param name="tonemap">The document's <c>render.tonemap</c>, or <see langword="null"/> for none.</param>
    /// <param name="sharpens">Whether a temporally resolved view sharpens (<see cref="Sharpens"/>).</param>
    /// <returns>The graph.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="views"/> is below one.</exception>
    /// <exception cref="WorldRootGraphRefusedException">The graph compiler refused the synthesized graph, such as an
    /// row's config that does not bind against its package's schema; the message names the row and the compiler's
    /// code.</exception>
    public static WorldRootGraph Compose(IReadOnlyList<WorldViewPostPass>? post, bool overlay, RenderGraphPackageCatalog packages, IReadOnlyList<string>? panes = null, int views = 1, WorldTonemap? tonemap = null, bool sharpens = false) {
        ArgumentNullException.ThrowIfNull(argument: packages);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 1,
            value: views
        );

        var entries = (post ?? []);
        var placed = (panes ?? []);
        var curve = (tonemap ?? WorldTonemap.None);
        var viewCount = (PlacesViews(sharpens: sharpens, tonemap: curve, views: views)
            ? views
            : 0);
        var passCount = ((viewCount + placed.Count) + entries.Count);
        var overlayPlan = (overlay
            ? PlanOverlay(packages: packages)
            : null);

        if (passCount == 0) {
            return new WorldRootGraph(
                overlay: overlayPlan,
                panes: placed,
                plan: null,
                post: entries,
                sharpens: sharpens,
                tonemap: curve,
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
                    Config: ViewConfig(
                        first: (index == 0),
                        tonemap: curve
                    ),
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
            } else {
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
            overlay: overlayPlan,
            panes: placed,
            plan: plan,
            post: entries,
            sharpens: sharpens,
            tonemap: curve,
            views: views
        );
    }
    /// <summary>Appends the overlay's instance over the instance the display would otherwise show, which it reads at the
    /// whole display's extent (<see cref="OverlayFootprint"/>) and draws the overlay over, and makes it the root. A graph
    /// whose presentation draws no overlay leaves everything as it is.</summary>
    /// <param name="set">The instance set, replaced with the set carrying the overlay's instance.</param>
    /// <param name="graphs">The parallel graph list, replaced with the list carrying the overlay's graph.</param>
    /// <param name="root">The instance the overlay draws over, replaced with <see cref="OverlayInstance"/>.</param>
    /// <exception cref="WorldRootGraphRefusedException">The set with the overlay's instance fails validation.</exception>
    public void AppendOverlay(ref RenderGraphInstanceSet set, ref IReadOnlyList<RenderGraphRuntimeGraph?> graphs, ref string root) {
        if ((OverlayPlan is not { } plan) || (m_overlayPipeline is not { } pipeline)) {
            return;
        }
        if (!RenderGraphInstanceSet.TryCreate(
            instances: [
                .. set.Instances,
                new RenderGraphInstance(
                    Name: OverlayInstance,
                    Passes: plan.Pipeline.Passes.Count,
                    Reads: [new RenderGraphRead(Producer: root)],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
            ],
            refusal: out var refusal,
            set: out var overlaid
        )) {
            throw new WorldRootGraphRefusedException(message: $"the overlay: {refusal.Message}");
        }

        set = overlaid;
        graphs = [
            .. graphs,
            new RenderGraphRuntimeGraph(
                Inputs: [new RenderGraphRuntimeInput(
                    Producer: root,
                    Version: SceneVersion
                )],
                Pipeline: pipeline
            ),
        ];
        root = OverlayInstance;
    }
    /// <summary>Returns the overlay instance's read of the instance it draws over, at the whole display's extent.</summary>
    /// <param name="beneath">The instance the overlay draws over.</param>
    /// <returns>The footprint.</returns>
    public static RenderGraphFootprint OverlayFootprint(string beneath) => new(
        Consumer: OverlayInstance,
        Height: 1.0,
        Producer: beneath,
        Width: 1.0
    );
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
    /// name no instance, or <see cref="PlayerRoster.MaxSlots"/> for the built-in seat ladder, whichever is larger.</summary>
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

        return most;
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

    // The overlay's own graph: one overlay pass drawing over the image bound to its one input.
    private static RenderGraphPlan PlanOverlay(RenderGraphPackageCatalog packages) {
        var definition = new RenderGraphDefinition(
            Name: OverlayInstance,
            Outputs: [FrameVersion],
            Packages: [
                new RenderGraphPackagePass(
                    Inputs: [new ResourceReference(Name: SceneVersion)],
                    Name: OverlayPass,
                    Outputs: [new ResourceReference(Name: FrameVersion)],
                    Package: RenderGraphPackageCatalog.Overlay
                ),
            ],
            Resources: [
                Image(
                    initialization: ShaderPipelineInitialization.External,
                    name: SceneVersion
                ),
                Image(
                    initialization: ShaderPipelineInitialization.Undefined,
                    name: FrameVersion
                ),
            ],
            Schema: RenderGraphSchemas.Graph
        );

        return (new RenderGraphCompiler(packages: packages).TryCompile(
            definition: definition,
            diagnostics: out var diagnostics,
            plan: out var plan
        )
            ? plan
            : throw new WorldRootGraphRefusedException(message: string.Join(
                separator: "; ",
                values: diagnostics.Select(selector: static diagnostic => $"the overlay: {diagnostic.Code}: {diagnostic.Message}")
            )));
    }
    // Every version the root declares is a working image; a pane or view it places binds whatever image its instance
    // publishes, which the place pass samples.
    private static ShaderPipelineResource Image(string name, ShaderPipelineInitialization initialization) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: RenderGraphPackageCatalog.WorkingFormat.ToString(),
        Initialization: initialization,
        Name: name
    );
    // Whether the root places each view with a pass of its own: with more than one view, and with a tonemap or a sharpen,
    // which a view's place pass applies. Otherwise the world's one view is the root's base as it is.
    private static bool PlacesViews(WorldTonemap tonemap, int views, bool sharpens) => ((views > 1) || sharpens || (tonemap == WorldTonemap.Filmic));
    // A view's place pass config: the first view is placed over nothing the display shows, so outside its rect it writes the
    // letterbox color, which every later view's place pass keeps as its base; with a tonemap, each view's pass tonemaps the
    // view it reconstructs and nothing else, so the letterbox color is never tonemapped.
    private static JsonElement? ViewConfig(bool first, WorldTonemap tonemap) => ((tonemap == WorldTonemap.Filmic)
        ? (first
            ? FirstTonemappedViewConfig
            : TonemappedViewConfig)
        : (first
            ? FirstViewConfig
            : null));
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
