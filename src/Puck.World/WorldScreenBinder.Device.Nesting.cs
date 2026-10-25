using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;

namespace Puck.World;

// The levels only a presentation shows: the worlds its seats are presented in and its infinity layers, with the
// screens of every level beneath them.
public sealed partial class WorldScreenBinder {
    // Follows every level the presentation shows beside the boot world's sessions: each world a seat is presented in
    // and every level beneath it, and each infinity layer's screens.
    private bool ReconcilePresentedLevels(NestedSessions sessions) {
        var moved = false;

        if (Presenter is { } presenter) {
            foreach (var scene in presenter.RoutedScenes) {
                if (
                    (scene.ViewCount != 0) &&
                    !m_routedScreens.ContainsKey(key: scene)
                ) {
                    AddRoutedScreens(scene: scene);
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
            if (entry.Screens is { } screens) { moved |= ReconcileLevel(screens: screens, sessions: sessions); }
        }

        return moved;
    }
    // Follows a world a seat is presented in, apart from the reconcile, since the closures over the scene are made on
    // entry to the scope holding them, and nearly every frame follows no new world.
    private void AddRoutedScreens(WorldRoutedScene scene) {
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
    }
    // Lists the feeds of the levels the presentation shows beside the boot world's sessions.
    private void AddPresentedFeeds() {
        foreach (var screens in m_routedScreens.Values) {
            for (var index = 0; (index < screens.Views.Count); index++) {
                AddFeed(feed: screens.Views[index]);
            }
        }
        foreach (var entry in InfinityEntries()) {
            if (entry.Screens is not { } screens) { continue; }
            foreach (var feed in screens.Views) { AddFeed(feed: feed); }
        }
    }
    // The source instances the screens of the worlds seats are presented in show.
    private List<RenderGraphInstance> RoutedSources() {
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

        return routed;
    }
    // The screens of every infinity layer that has them.
    private IEnumerable<WorldNestedScreens<SessionFeed>> InfinityLevels() => InfinityEntries().Select(selector: static entry => entry.Screens).OfType<WorldNestedScreens<SessionFeed>>();    // The level an infinity output or a world view routed to another world shows, and whether the view is one of them.
    private (bool Known, WorldNestedScreens<SessionFeed>? Level) PresentedLevelOf(string view) {
        if (m_infinityOutputs.TryGetValue(key: view, value: out var infinity)) {
            return (true, infinity.Entry.Screens);
        }

        return (IsRouted(instance: view)
            ? (true, RoutedScreensOf(instance: view))
            : (false, null));
    }
    // Describes each world seats are presented in, at depth 0.
    private void AppendRoutedLevels(System.Text.StringBuilder builder) {
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
    }
    // The eye a window among a feed's destination's screens fits to: the camera the feed renders that world with.
    private Func<Vector3?> EyeOf(SessionFeed feed) => () => CameraOf(feed: feed).Position;
    // The world a feed's destination's glass stands in, read as its view dresses.
    private static Func<WorldDefinition?> DestinationOf(SessionFeed feed) => () => feed.Mirror.Definition;
    // Every screen index a program may declare, which a residency of another world binds.
    private static readonly int[] AllScreens = [.. Enumerable.Range(
        count: SdfProgramBuilder.MaxScreenSurfaces,
        start: 0
    )];
    // The camera a feed renders its destination with this frame: a window's fit, or its ordinary projection while the fit
    // has no answer and for a camera projection.
    private CameraSnapshot CameraOf(SessionFeed feed) {
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
                (feed.Route.Window is { } window) &&
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
    private static bool Emits(WorldNestedScreens<SessionFeed>? screens, int screen) {
        if (screens is null) { return false; }
        return screens.RowOf(screen: screen)?.Source switch {
            WorldScreenSource.Machine or WorldScreenSource.Producer or WorldScreenSource.Probe => true,
            WorldScreenSource.Session => (screens.Children.TryGetValue(key: screen, value: out var child) && (child.InstanceName != screens.World)),
            _ => false,
        };
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
        public float LuminanceOf(int screen) => WorldScreenBinder.LuminanceOf(row: feed.Nested?.RowOf(screen: screen));
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
        public float LuminanceOf(int screen) => WorldScreenBinder.LuminanceOf(row: binder.m_routedScreens.GetValueOrDefault(key: scene)?.RowOf(screen: screen));
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
                    (feed.Route.Window is { } window) &&
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
    // The glass a window among a level's screens stands in: its screen's row in that level, read as its view dresses.
    private static Func<WorldScreen?> LevelRowOf(WorldNestedScreens<SessionFeed> screens, int screen) => () => screens.RowOf(screen: screen);}
