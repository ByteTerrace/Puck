using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

// The screens of every world the presentation shows other than the boot world: a world seats are presented in, at depth
// 0, and every session's destination, one level deeper than the screen showing it. Each is a WorldNestedScreens whose
// session screens are session feeds of their own, to the boot world's views.nestingDepth, so a portal seen through a
// portal renders its own destination, recursively. Every other screen shows that world's own sources: a machine or a
// probe its own host's (MachinesOf), a camera its own camera (WorldScreenBinder.NestedCameras), and text its own font
// catalog's (WorldSessionSceneEmitter). Every feed renders as any session does: its destination's endpoint
// scene while its session discloses everything, which every seat presented there and every other such feed at any depth
// shares, or a residency of its own. Each view of a residency binds the screens of the level it renders
// (ISdfScreenSources.ReadOf takes the view), so one world seen at two depths shows each level's own images.
internal sealed partial class WorldScreenBinder {
    // Every screen index a program may declare, which a residency of another world binds.
    private static readonly int[] AllScreens = [.. Enumerable.Range(
        count: SdfProgramBuilder.MaxScreenSurfaces,
        start: 0
    )];

    // The presentation's nesting depth (the boot world's views.nestingDepth), read each produced frame.
    private int m_nestingDepth = RenderGraphInstanceSet.DefaultNestingDepth;
    // The screens of each world seats are presented in, by its scene.
    private readonly Dictionary<WorldRoutedScene, WorldNestedScreens<SessionFeed>> m_routedScreens = new(comparer: ReferenceEqualityComparer.Instance);
    private readonly List<WorldRoutedScene> m_retiredRoutedScreens = [];
    // What each set of nested screens presents: its world's instance and the eye a window among its screens fits to.
    private readonly Dictionary<WorldNestedScreens<SessionFeed>, NestedOwner> m_nestedOwners = new(comparer: ReferenceEqualityComparer.Instance);
    // Every session feed, the boot world's first, then each level's beneath its world, and each by its view's name;
    // rebuilt when a feed opens or closes.
    private readonly List<SessionFeed> m_feeds = [];
    private readonly Dictionary<string, SessionFeed> m_feedsByName = new(comparer: StringComparer.Ordinal);
    private bool m_feedsMoved = true;
    // The source instances the routed worlds' screens show, and those only sessions' screens show, as the last moved
    // frame published them.
    private IReadOnlyList<RenderGraphInstance> m_nestedSources = [];

    private NestedSessions? m_nestedSessions;

    // Opens and closes nested session feeds over the sessions the authority holds.
    private NestedSessions NestedSessionsOf() => (m_nestedSessions ??= new NestedSessions(binder: this));
    // The presentation's nesting depth: the boot world's, as its authority holds it.
    private int BootNestingDepth() => (BootDefinition()?.Views.NestingDepth ?? RenderGraphInstanceSet.DefaultNestingDepth);
    // Whether a producer's content is a pure function of its settings, which every world a screen shows shares; a
    // producer of the local device's content (a camera, a desktop capture) is never opened for a world a screen shows.
    private static bool SharesProducer(string id) => (
        WorldImageProducerVocabulary.TryGet(
            id: id,
            shape: out var shape
        ) &&
        (shape.Content == ImageContentClass.Deterministic)
    );
    // Follows every presented world's screens for the frame being prepared: the nesting depth, the boot world's session
    // feeds' destinations, each world a seat is presented in, and every level beneath them. Allocates only when a level's
    // screens or the feeds they show move.
    private void ReconcileNesting() {
        var depth = BootNestingDepth();
        var moved = false;

        if (depth != m_nestingDepth) {
            m_nestingDepth = depth;
            moved = true;
            ReconcileMappings();
        }

        var sessions = NestedSessionsOf();

        foreach (var slot in m_slots.Values) {
            if (m_nestingDepth == 0) {
                ReleaseSlotSession(slot: slot);

                continue;
            }

            if (slot.Session is { } feed) {
                moved |= ReconcileFeed(
                    feed: feed,
                    sessions: sessions
                );
            }
        }

        if (Presenter is { } presenter) {
            foreach (var scene in presenter.RoutedScenes) {
                if (
                    (scene.ViewCount != 0) &&
                    !m_routedScreens.ContainsKey(key: scene)
                ) {
                    var screens = new WorldNestedScreens<SessionFeed>(
                        definition: () => scene.Endpoint.Definition,
                        depth: 0,
                        head: WorldViewNames.Routed(authority: scene.Endpoint.Identity),
                        shares: SharesProducer,
                        world: scene.Endpoint.Identity
                    );

                    m_routedScreens.Add(
                        key: scene,
                        value: screens
                    );
                    m_nestedOwners.Add(
                        key: screens,
                        value: new NestedOwner(
                            Eye: () => (scene.TrySeatCamera(camera: out var camera)
                                ? camera.Position
                                : null),
                            Feed: null,
                            Instance: scene.Endpoint.Identity,
                            Local: () => scene.Endpoint.Definition,
                            Scene: scene
                        )
                    );
                    moved = true;
                }
            }

            foreach (var (scene, screens) in m_routedScreens) {
                if (
                    !presenter.Presents(scene: scene) ||
                    (scene.ViewCount == 0)
                ) {
                    m_retiredRoutedScreens.Add(item: scene);
                }
            }
        }

        foreach (var scene in m_retiredRoutedScreens) {
            if (m_routedScreens.Remove(
                key: scene,
                value: out var retired
            )) {
                retired.Close(sessions: sessions);
                _ = m_nestedOwners.Remove(key: retired);
                moved = true;
            }
        }

        m_retiredRoutedScreens.Clear();

        foreach (var screens in m_routedScreens.Values) {
            moved |= ReconcileLevel(
                screens: screens,
                sessions: sessions
            );
        }
        foreach (var entry in InfinityEntries()) {
            if (entry.Screens is { } screens) { moved |= ReconcileLevel(screens, sessions); }
        }

        if (!(moved || m_feedsMoved)) {
            return;
        }

        RebuildFeeds();
        PublishNesting();
        PruneNestedFilms();
        ReconcileViews();
    }
    // Follows one level's screens and every feed they show.
    private bool ReconcileLevel(WorldNestedScreens<SessionFeed> screens, NestedSessions sessions) {
        var moved = screens.Reconcile(
            nestingDepth: m_nestingDepth,
            sessions: sessions
        );

        // Indexed, since a level's views may close while the frame walks them.
        for (var index = 0; (index < screens.Views.Count); index++) {
            moved |= ReconcileFeed(
                feed: screens.Views[index],
                sessions: sessions
            );
        }

        return moved;
    }
    // Follows a feed's destination's screens, opening them the first time it shows its destination.
    private bool ReconcileFeed(SessionFeed feed, NestedSessions sessions) {
        var moved = false;

        if (feed.Nested is not { } screens) {
            screens = new WorldNestedScreens<SessionFeed>(
                definition: () => feed.Mirror.Definition,
                depth: feed.Depth,
                head: feed.RegistrationName,
                shares: SharesProducer,
                world: feed.InstanceName
            );
            feed.Nested = screens;
            m_nestedOwners[screens] = new NestedOwner(
                Eye: () => CameraOf(feed: feed)?.Position,
                Feed: feed,
                Instance: feed.InstanceName,
                Local: () => feed.Mirror.Definition,
                Scene: null
            );
            moved = true;
        }

        return moved | ReconcileLevel(
            screens: screens,
            sessions: sessions
        );
    }
    // Lists the feeds again when one opened or closed since they were last listed.
    private void EnsureFeeds() {
        if (m_feedsMoved) {
            RebuildFeeds();
        }
    }
    // Lists every feed, the boot world's first, then each level beneath its world, and names them.
    private void RebuildFeeds() {
        m_feeds.Clear();
        m_feedsByName.Clear();

        foreach (var slot in m_slots.Values) {
            if (slot.Session is { } feed) {
                AddFeed(feed: feed);
            }
        }

        foreach (var screens in m_routedScreens.Values) {
            for (var index = 0; (index < screens.Views.Count); index++) {
                AddFeed(feed: screens.Views[index]);
            }
        }
        foreach (var entry in InfinityEntries()) {
            if (entry.Screens is not { } screens) { continue; }
            foreach (var feed in screens.Views) { AddFeed(feed); }
        }

        m_feedsMoved = false;
    }
    private void AddFeed(SessionFeed feed) {
        m_feeds.Add(item: feed);
        m_feedsByName[feed.RegistrationName] = feed;

        if (feed.Nested is { } screens) {
            for (var index = 0; (index < screens.Views.Count); index++) {
                AddFeed(feed: screens.Views[index]);
            }
        }
    }
    // Publishes the source instances the levels' screens show: a routed world's beside the boot world's, which every
    // view of a world the display shows reads, and every other level's beside the views, which only the session whose
    // screens show them reads.
    private void PublishNesting() {
        var routed = new List<RenderGraphInstance>();

        foreach (var screens in m_routedScreens.Values) {
            foreach (var source in screens.Sources) {
                if (!WorldSourceInstances.Holds(
                    instances: routed,
                    name: source.Name
                )) {
                    routed.Add(item: source);
                }
            }
        }

        Mappings.ReconcileRouted(sources: routed);

        var nested = new List<RenderGraphInstance>();

        foreach (var screens in m_feeds.Select(feed => feed.Nested).Concat(InfinityEntries().Select(entry => entry.Screens)).OfType<WorldNestedScreens<SessionFeed>>()) {
            foreach (var source in screens.Sources) {
                if (
                    !WorldSourceInstances.Holds(
                        instances: Mappings.Sources.Instances,
                        name: source.Name
                    ) &&
                    !WorldSourceInstances.Holds(
                        instances: nested,
                        name: source.Name
                    )
                ) {
                    nested.Add(item: source);
                }
            }
        }

        m_nestedSources = nested;
    }
    // The camera a feed renders its destination with this frame: a window's fit, or its ordinary projection while the fit
    // has no answer and for a camera projection.
    private CameraSnapshot? CameraOf(SessionFeed feed) {
        if (
            (feed.Projection == WorldScreenProjection.Window) &&
            (feed.WindowFit?.Invoke() is { } fitted)
        ) {
            return fitted;
        }

        return WorldSessionSceneEmitter.ResolveCamera(
            cameraName: feed.EffectiveCamera,
            domains: m_domains,
            height: ((uint)(feed.Resolution?.Height ?? WorldViewInstances.DefaultSessionHeight)),
            mirror: feed.Mirror,
            width: ((uint)(feed.Resolution?.Width ?? WorldViewInstances.DefaultSessionWidth))
        );
    }
    // The feed rendering a window view of a scene, by its index in the scene's frame.
    private SessionFeed? WindowFeedOf(WorldRoutedScene scene, int view) {
        foreach (var feed in m_feeds) {
            if (
                (feed.WindowRoute.Window is { } window) &&
                ReferenceEquals(
                    objA: window.Scene,
                    objB: scene
                ) &&
                (window.Index == view)
            ) {
                return feed;
            }
        }

        return null;
    }
    // The screens a view of a scene shows: a seat's, the routed world's own; a window's, its feed's destination's; a
    // camera's, the level that films it.
    private WorldNestedScreens<SessionFeed>? ScreensOf(WorldRoutedScene scene, int view) => ((view < scene.SeatViewCount)
        ? m_routedScreens.GetValueOrDefault(key: scene)
        : (WindowFeedOf(
            scene: scene,
            view: view
        )?.Nested ?? FilmedLevelOf(
            scene: scene,
            view: view
        )));
    // The screens a world view instance shows when its seat is presented in another world, or null for the boot world's.
    private WorldNestedScreens<SessionFeed>? RoutedScreensOf(string instance) {
        if (
            (Presenter is { } presenter) &&
            (string.Equals(
                a: instance,
                b: WorldViewGraphs.WorldInstance,
                comparisonType: StringComparison.Ordinal
            ) || (WorldViewNames.ViewOf(instance: instance) is not null)) &&
            presenter.TryRoutedView(
                index: out _,
                scene: out var scene,
                view: (WorldViewNames.ViewOf(instance: instance) ?? 0)
            )
        ) {
            return m_routedScreens.GetValueOrDefault(key: scene);
        }

        return null;
    }
    // Whether a world view instance's seat is presented in another world.
    private bool IsRouted(string instance) => (
        (Presenter is { } presenter) &&
        (string.Equals(
            a: instance,
            b: WorldViewGraphs.WorldInstance,
            comparisonType: StringComparison.Ordinal
        ) || (WorldViewNames.ViewOf(instance: instance) is not null)) &&
        presenter.TryRoutedView(
            index: out _,
            scene: out _,
            view: (WorldViewNames.ViewOf(instance: instance) ?? 0)
        )
    );

    /// <summary>Describes every level the presentation nests, one segment each: the nesting depth, then each world seats
    /// are presented in (<c>routed$&lt;digest&gt; depth 0 world &lt;authority&gt;</c>) and each session view, at every
    /// depth, by name — its
    /// depth, destination, the residency it renders through (<c>endpoint:&lt;authority&gt;</c>, shared with every seat
    /// and window presenting that world, or <c>own</c>), and what each of its world's screens shows: a session view one
    /// level deeper, a camera view of that world, a source instance (a machine's or a probe's of that world's own host,
    /// with the fault that leaves it dark in parentheses), a fallback colour's source past the depth, <c>text</c>, or
    /// <c>none</c>; then <c>text-fault</c> and why, when the world's font catalog does not resolve.</summary>
    /// <returns>The description.</returns>
    public string DescribeNesting() {
        EnsureFeeds();

        var builder = new System.Text.StringBuilder();

        _ = builder.Append(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"depth {m_nestingDepth}"
        );

        foreach (var screens in m_routedScreens.Values) {
            _ = builder.Append(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $" | {screens.Head} depth 0 world {m_nestedOwners[screens].Instance}"
            );
            AppendScreens(
                builder: builder,
                screens: screens,
                textFault: m_nestedOwners[screens].Scene?.TextFault
            );
        }

        foreach (var feed in m_feeds) {
            var via = ((RoutedWindowOf(feed: feed) is { } window)
                ? $"endpoint:{window.Scene.Endpoint.Identity}"
                : ((feed.FrameSource is null)
                    ? "unregistered"
                    : "own"));

            _ = builder.Append(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $" | {feed.RegistrationName} depth {feed.Depth} destination {feed.Destination} via {via}"
            );

            if (feed.Nested is { } screens) {
                AppendScreens(
                    builder: builder,
                    screens: screens,
                    textFault: (RoutedWindowOf(feed: feed)?.Scene.TextFault ?? feed.Emitter?.TextFault)
                );
            }
        }

        return builder.ToString();
    }

    private void AppendScreens(System.Text.StringBuilder builder, WorldNestedScreens<SessionFeed> screens, string? textFault) {
        _ = builder.Append(value: " screens");

        if (screens.Rows.Count == 0) {
            _ = builder.Append(value: " none");
        }

        foreach (var row in screens.Rows) {
            var instance = screens.InstanceOf(screen: row.Index);
            var shown = (instance ?? ((row.Source is WorldScreenSource.Text)
                ? "text"
                : "none"));

            _ = builder.Append(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $" {row.Index}:{shown}"
            );

            if (
                (instance is not null) &&
                (SourceFault(instance: instance) is { } fault)
            ) {
                _ = builder.Append(
                    provider: System.Globalization.CultureInfo.InvariantCulture,
                    handler: $" ({fault})"
                );
            }
        }

        if (textFault is not null) {
            _ = builder.Append(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $" text-fault {textFault}"
            );
        }
    }

    /// <inheritdoc/>
    /// <remarks>A session reports its destination's screens as it shows them; a world view whose seat is presented in
    /// another world, that world's; a camera view of another world, the screens of the level that films it.</remarks>
    public bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements) {
        ArgumentNullException.ThrowIfNull(argument: view);

        if (m_infinityOutputs.TryGetValue(view, out var infinity)) {
            placements = (infinity.Entry.Screens?.Mappings.Mappings ?? []);
            return true;
        }

        if (SessionFeedOf(name: view) is { } feed) {
            placements = ((feed.Nested is { } screens)
                ? screens.Mappings.Mappings
                : []);

            return true;
        }

        if (IsRouted(instance: view)) {
            placements = ((RoutedScreensOf(instance: view) is { } routed)
                ? routed.Mappings.Mappings
                : []);

            return true;
        }

        if (NestedCameraLevel(name: view) is { } filmed) {
            placements = filmed.Mappings.Mappings;

            return true;
        }

        placements = [];

        return false;
    }
    /// <inheritdoc/>
    /// <remarks>A session's screen stands in its parent's destination, a routed world's in that world, one a camera view
    /// of another world films in the world of the level that films it, and the boot world's in the world every other
    /// world view and every boot camera view renders.</remarks>
    public WorldPortalGlass PortalGlass(string consumer, string producer, out WorldScreen? glass) {
        glass = null;

        if (!m_feedsByName.TryGetValue(
            key: producer,
            value: out var shown
        )) {
            return WorldPortalGlass.None;
        }

        WorldNestedScreens<SessionFeed>? world;

        if (m_infinityOutputs.TryGetValue(consumer, out var infinity)) {
            world = infinity.Entry.Screens;
        } else if (m_feedsByName.TryGetValue(
            key: consumer,
            value: out var parent
        )) {
            world = parent.Nested;
        } else if (NestedCameraLevel(name: consumer) is { } filmed) {
            world = filmed;
        } else if (IsRouted(instance: consumer)) {
            world = RoutedScreensOf(instance: consumer);
        } else if (
            string.Equals(
                a: consumer,
                b: WorldViewGraphs.WorldInstance,
                comparisonType: StringComparison.Ordinal
            ) ||
            (WorldViewNames.ViewOf(instance: consumer) is not null) ||
            m_cameraViews.ContainsKey(key: consumer)
        ) {
            if (
                (shown.ParentFeed is not null) ||
                (shown.RootScene is not null) ||
                (shown.ParentInfinity is not null)
            ) {
                return WorldPortalGlass.Elsewhere;
            }

            glass = RowOf(screen: shown.ScreenIndex);

            return ((glass is null)
                ? WorldPortalGlass.None
                : WorldPortalGlass.Found);
        } else {
            return WorldPortalGlass.None;
        }

        if (
            (world is null) ||
            !(world.Children.TryGetValue(
                key: shown.ScreenIndex,
                value: out var child
            ) && ReferenceEquals(
                objA: child,
                objB: shown
            ))
        ) {
            return WorldPortalGlass.Elsewhere;
        }

        glass = world.RowOf(screen: shown.ScreenIndex);

        return ((glass is null)
            ? WorldPortalGlass.None
            : WorldPortalGlass.Found);
    }

    // What one level's screens present: the instance whose screen sessions they show, the world their glass stands in,
    // and the eye a window among them fits to, with the feed or routed scene the level belongs to.
    private sealed record NestedOwner(string Instance, Func<WorldDefinition?> Local, Func<Vector3?> Eye, SessionFeed? Feed, WorldRoutedScene? Scene) {
        public InfinityEntry? Infinity { get; init; }
        // The extents of the source instances the level's screens show, which its mappings publish at.
        public NestedImages? Images { get; set; }
    }
    // The extent of the image a level's screen shows: its machine output's framebuffer, from the level's world's own
    // host, or its source instance's running image.
    private sealed class NestedImages(WorldScreenBinder binder, WorldNestedScreens<SessionFeed> screens) : IWorldScreenImages {
        /// <inheritdoc/>
        public bool TryExtent(int screen, out int width, out int height) {
            (width, height) = (0, 0);

            if (screens.RowOf(screen: screen) is { Source: WorldScreenSource.Machine machine }) {
                if (binder.MachinesOf(world: screens.World)?.VideoOutput(
                    instance: machine.Instance,
                    output: machine.Output
                ) is { } output) {
                    (width, height) = (output.Width, output.Height);
                }
            } else if (
                (screens.Mappings.InstanceOf(screen: screen) is { } instance) &&
                (binder.FeedOf(instance: instance) is { } source)
            ) {
                (width, height) = (((int)source.Descriptor.Width), ((int)source.Descriptor.Height));
            }

            return ((width > 0) && (height > 0));
        }
    }

    private static bool Emits(WorldNestedScreens<SessionFeed>? screens, int screen) {
        if (screens is null) { return false; }
        return screens.RowOf(screen)?.Source switch {
            WorldScreenSource.Machine or WorldScreenSource.Producer or WorldScreenSource.Probe => true,
            WorldScreenSource.Session => screens.Children.TryGetValue(screen, out var child) && child.InstanceName != screens.World,
            _ => false,
        };
    }
    // Publishes every level's mappings for this frame at the extents their images now have.
    private void PublishNestedMappings() {
        foreach (var (screens, owner) in m_nestedOwners) {
            screens.Mappings.Publish(images: (owner.Images ??= new NestedImages(
                binder: this,
                screens: screens
            )));
        }
    }

    // Opens a nested session feed over the session the authority holds for a screen of a level's world, keeps it while the
    // authority holds that same session, and closes it, with every level beneath it, when the screen moves on.
    private sealed class NestedSessions(WorldScreenBinder binder) : IWorldNestedSessions<SessionFeed> {
        // The session the authority holds for a level's screen, when it holds one for this very source.
        private WorldObservationSession? Hosted(WorldNestedScreens<SessionFeed> screens, int screen, WorldScreenSource.Session source) => ((
            binder.m_nestedOwners.TryGetValue(
                key: screens,
                value: out var owner
            ) &&
            ((owner.Feed is null) || (owner.Feed.Observation is { Ended: false })) &&
            ((owner.Infinity?.Hosted is null) || (owner.Infinity.Hosted.Observation is { Ended: false })) &&
            (binder.m_instanceHost.ScreenSession(
                instanceName: owner.Instance,
                screenIndex: screen
            ) is { } hosted) &&
            (hosted.Source == source) &&
            (hosted.Observation is { Ended: false }) &&
            (hosted.InstanceName is not null)
        )
            ? hosted
            : null);

        /// <inheritdoc/>
        public SessionFeed? Open(WorldNestedScreens<SessionFeed> screens, int screen, WorldScreenSource.Session source, string name) {
            if (Hosted(
                screen: screen,
                screens: screens,
                source: source
            ) is not { } hosted) {
                return null;
            }

            var owner = binder.m_nestedOwners[screens];
            var feed = new SessionFeed(
                depth: (screens.Depth + 1),
                destination: source.Destination,
                effectiveCamera: source.CameraName,
                generationId: hosted.GenerationId,
                hosted: hosted,
                instanceName: hosted.InstanceName!,
                projection: source.Projection,
                registrationName: name,
                requestedCamera: source.CameraName,
                resolution: source.Resolution,
                screenIndex: screen
            ) {
                Eye = owner.Eye,
                Local = owner.Local,
                ParentFeed = owner.Feed,
                ParentInfinity = owner.Infinity,
                RootScene = owner.Scene,
                Row = () => screens.RowOf(screen: screen),
            };

            if (binder.m_viewPipelines is not null) {
                binder.RegisterSessionView(feed: feed);
            }

            binder.m_feedsMoved = true;

            return feed;
        }
        /// <inheritdoc/>
        public bool Holds(SessionFeed child, WorldNestedScreens<SessionFeed> screens, int screen, WorldScreenSource.Session source) => ReferenceEquals(
            objA: child.Hosted,
            objB: Hosted(
                screen: screen,
                screens: screens,
                source: source
            )
        );
        /// <inheritdoc/>
        public void Close(SessionFeed child) {
            binder.ReleaseSession(
                feed: child,
                index: child.ScreenIndex,
                reason: "its world's screen no longer shows it"
            );
        }
    }
    // What each screen of a session feed's own residency shows, in every view of it, the session's and its destination's
    // cameras' alike: its destination's screens as the feed shows them.
    private sealed class FeedScreenSources(SessionFeed feed) : ISdfScreenSources {
        /// <inheritdoc/>
        public IReadOnlyList<int> Screens => AllScreens;

        /// <inheritdoc/>
        public bool Emits(int screen) => WorldScreenBinder.Emits(
            screen: screen,
            screens: feed.Nested
        );
        /// <inheritdoc/>
        public SourceMapping? MappingOf(int screen) => (((feed.Nested is { } screens) && screens.Mappings.TryGet(
            mapping: out var mapping,
            screen: screen
        ))
            ? mapping
            : null);
        /// <inheritdoc/>
        public string? ReadOf(int view, int screen) => feed.Nested?.InstanceOf(screen: screen);
    }
    // What each screen of a routed scene's residency shows in each of its views: a seat's view shows the world's own
    // screens, a window's its feed's level, a camera's the level that films it. A screen draws from the first mapping a
    // level publishes for it, which every level publishes alike for one row, and lights the room from the routed world's
    // own level, every level's being the same world.
    private sealed class RoutedScreenSources(WorldScreenBinder binder, WorldRoutedScene scene) : ISdfScreenSources {
        /// <inheritdoc/>
        public IReadOnlyList<int> Screens => AllScreens;

        /// <inheritdoc/>
        public bool Emits(int screen) => WorldScreenBinder.Emits(
            screen: screen,
            screens: binder.m_routedScreens.GetValueOrDefault(key: scene)
        );
        /// <inheritdoc/>
        public SourceMapping? MappingOf(int screen) {
            if (
                binder.m_routedScreens.TryGetValue(
                    key: scene,
                    value: out var routed
                ) &&
                routed.Mappings.TryGet(
                    mapping: out var mapping,
                    screen: screen
                )
            ) {
                return mapping;
            }

            foreach (var feed in binder.m_feeds) {
                if (
                    (feed.WindowRoute.Window is { } window) &&
                    ReferenceEquals(
                        objA: window.Scene,
                        objB: scene
                    ) &&
                    (feed.Nested is { } screens) &&
                    screens.Mappings.TryGet(
                        mapping: out mapping,
                        screen: screen
                    )
                ) {
                    return mapping;
                }
            }

            return null;
        }
        /// <inheritdoc/>
        public string? ReadOf(int view, int screen) => binder.ScreensOf(
            scene: scene,
            view: view
        )?.InstanceOf(screen: screen);
    }
}
