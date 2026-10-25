using System.Numerics;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Hosting;
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
public sealed partial class WorldScreenBinder {
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

        moved |= ReconcilePresentedLevels(sessions: sessions);

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
            screens = NestFeed(feed: feed);
            moved = true;
        }

        return moved | ReconcileLevel(
            screens: screens,
            sessions: sessions
        );
    }
    // Opens a feed's level of screens. Apart from ReconcileFeed, whose every call would otherwise allocate the closure
    // the definition reader captures.
    private WorldNestedScreens<SessionFeed> NestFeed(SessionFeed feed) {
        var screens = new WorldNestedScreens<SessionFeed>(
            definition: () => feed.Mirror.Definition,
            depth: feed.Depth,
            head: feed.RegistrationName,
            shares: SharesProducer,
            world: feed.InstanceName
        );

        feed.Nested = screens;
        m_nestedOwners[screens] = new NestedOwner(
            Eye: EyeOf(feed: feed),
            Feed: feed,
            Instance: feed.InstanceName,
            Local: DestinationOf(feed: feed),
            Scene: null
        );

        return screens;
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

        AddPresentedFeeds();

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
        Mappings.ReconcileRouted(sources: RoutedSources());

        var nested = new List<RenderGraphInstance>();

        foreach (var screens in m_feeds.Select(selector: static feed => feed.Nested).OfType<WorldNestedScreens<SessionFeed>>().Concat(second: InfinityLevels())) {
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
    /// <summary>Describes every level the presentation nests, one segment each: the nesting depth, then each world seats
    /// are presented in (<c>routed$&lt;digest&gt; depth 0 world &lt;authority&gt;</c>) and each session view, at every
    /// depth, by name — its
    /// depth, destination, the residency it renders through (<c>world</c>, the World's own, for a session of the boot
    /// world; <c>endpoint:&lt;authority&gt;</c>, shared with every seat and window presenting that world; or <c>own</c>), and what each of its world's screens shows: a session view one
    /// level deeper, a camera view of that world, a source instance (a machine's or a probe's of that world's own host,
    /// with the fault that leaves it dark in parentheses), a fallback colour's source past the depth, <c>text</c>, or
    /// <c>none</c>; then <c>text-fault</c> and why, when the world's font catalog does not resolve.</summary>
    /// <returns>The description.</returns>
    public string DescribeNesting() {
        EnsureFeeds();

        var builder = new System.Text.StringBuilder();

        _ = builder.Append(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"depth {BootNestingDepth()}"
        );

        AppendRoutedLevels(builder: builder);

        foreach (var feed in m_feeds) {
            var via = (feed.Home
                ? "world"
                : ((RoutedWindowOf(feed: feed) is { } window)
                    ? $"endpoint:{window.Scene.Endpoint.Identity}"
                    : (feed.Registered
                        ? "own"
                        : "unregistered")));

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
        _ = builder.Append(value: ((screens.Rows.Count == 0)
            ? " screens none"
            : " screens"));

        foreach (var row in screens.Rows) {
            var instance = screens.InstanceOf(screen: row.Index);
            var shown = (instance ?? ((row.Source is WorldScreenSource.Text)
                ? "text"
                : "none"));
            var fault = (((instance is not null) && (SourceFault(instance: instance) is { } reason))
                ? $" ({reason})"
                : "");

            _ = builder.Append(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $" {row.Index}:{shown}{fault}"
            );
        }

        _ = builder.Append(value: ((textFault is null)
            ? ""
            : $" text-fault {textFault}"));
    }

    /// <inheritdoc/>
    /// <remarks>A session reports its destination's screens as it shows them; a world view whose seat is presented in
    /// another world, that world's; a camera view of another world, the screens of the level that films it.</remarks>
    public bool TryPlacements(string view, out IReadOnlyList<SourceMapping> placements) {
        ArgumentNullException.ThrowIfNull(argument: view);

        var (known, level) = PresentedLevelOf(view: view);

        if (!known && (SessionFeedOf(name: view) is { } feed)) {
            (known, level) = (true, feed.Nested);
        }

        if (!known && (NestedCameraLevel(name: view) is { } filmed)) {
            (known, level) = (true, filmed);
        }

        placements = (level?.Mappings.Mappings ?? []);

        return known;
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

        var (known, world) = PresentedLevelOf(view: consumer);

        if (!known && m_feedsByName.TryGetValue(
            key: consumer,
            value: out var parent
        )) {
            (known, world) = (true, parent.Nested);
        }

        if (!known && (NestedCameraLevel(name: consumer) is { } filmed)) {
            (known, world) = (true, filmed);
        }

        // A consumer that is no level renders the boot world, when it is one of its views or a camera view of it.
        if (!known) {
            return ((string.Equals(
                a: consumer,
                b: WorldViewGraphs.WorldInstance,
                comparisonType: StringComparison.Ordinal
            ) || (WorldViewNames.ViewOf(instance: consumer) is not null) || m_cameraViews.ContainsKey(key: consumer))
                ? BootGlass(
                    glass: out glass,
                    shown: shown
                )
                : WorldPortalGlass.None);
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
    // Where a boot world's view shows a session's glass: the boot world's own row for one of its screens, elsewhere for a
    // screen of another level.
    private WorldPortalGlass BootGlass(SessionFeed shown, out WorldScreen? glass) {
        glass = null;

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
    }    /// <inheritdoc/>
    /// <remarks>Reads the residency the session already renders, the World's own, its endpoint scene's or its own, and
    /// creates none.</remarks>
    public bool Bakes(string view) {
        if (!m_feedsByName.TryGetValue(
            key: view,
            value: out var feed
        )) {
            return false;
        }

        var residency = (feed.Home
            ? ViewHost
            : ((feed.Route.Window is { } window)
                ? m_routedResidencies.GetValueOrDefault(key: window.Scene)
                : m_viewResidencies.GetValueOrDefault(key: feed.RegistrationName)?.Residency));

        return (residency?.MeshPathBake.Pending ?? false);
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
            (width, height) = ((screens.RowOf(screen: screen) is { Source: WorldScreenSource.Machine machine })
                ? ExtentOf(output: binder.MachinesOf(world: screens.World)?.VideoOutput(
                    instance: machine.Instance,
                    output: machine.Output
                ))
                : binder.FeedExtentOf(instance: screens.Mappings.InstanceOf(screen: screen)));

            return ((width > 0) && (height > 0));
        }
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
                Row = LevelRowOf(
                    screen: screen,
                    screens: screens
                ),
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
                reason: null
            );
        }
    }
    // The level whose screens show a camera view of another world, by the view's name, or null.
    private WorldNestedScreens<SessionFeed>? NestedCameraLevel(string name) => (PresentedCameraLevel(name: name) ?? FeedCameraLevel(name: name));
    // The level a session's destination's screens make up that shows a camera view of that world, or null.
    private WorldNestedScreens<SessionFeed>? FeedCameraLevel(string name) {
        EnsureFeeds();

        foreach (var feed in m_feeds) {
            if (
                (feed.Nested is { } level) &&
                level.TryCamera(
                    camera: out _,
                    name: name
                )
            ) {
                return level;
            }
        }

        return null;
    }
}
