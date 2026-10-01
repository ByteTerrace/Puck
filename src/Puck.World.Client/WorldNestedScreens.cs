using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>Opens, keeps and closes the sessions a world's screens show one level deeper, for
/// <see cref="WorldNestedScreens{TChild}"/>. A presentation implements it over the sessions its authority holds.</summary>
/// <typeparam name="TChild">What a session view is to the presentation.</typeparam>
public interface IWorldNestedSessions<TChild> where TChild : class {
    /// <summary>Opens the view of the session a screen shows, when the authority holds one for it.</summary>
    /// <param name="screens">The screens the session's screen stands among.</param>
    /// <param name="screen">The screen's index.</param>
    /// <param name="source">The screen's authored session.</param>
    /// <param name="name">The view's instance name (<see cref="WorldViewNames.Nested"/>).</param>
    /// <returns>The view, or <see langword="null"/> while the authority holds no session for the screen.</returns>
    TChild? Open(WorldNestedScreens<TChild> screens, int screen, WorldScreenSource.Session source, string name);
    /// <summary>Returns whether an open view still shows the session the authority holds for its screen now.</summary>
    /// <param name="child">The view.</param>
    /// <param name="screens">The screens its screen stands among.</param>
    /// <param name="screen">The screen's index.</param>
    /// <param name="source">The screen's authored session now.</param>
    /// <returns><see langword="true"/> when the view is still current.</returns>
    bool Holds(TChild child, WorldNestedScreens<TChild> screens, int screen, WorldScreenSource.Session source);
    /// <summary>Closes a view: the screen no longer shows it, or shows another session.</summary>
    /// <param name="child">The view.</param>
    void Close(TChild child);
}
/// <summary>
/// One world's screens as one view of that world shows them, where the world is not the boot world: a world seats are
/// presented in, or a session's destination. The rows are the world's own screens and the faces its creations seat
/// (<see cref="WorldPrototypeFacets.Seated"/>), drawn by the world's own emitter (<see cref="WorldSessionSceneEmitter"/>).
/// <para>The presented world is <see cref="Depth"/> screens deep. A session screen within the nesting depth, one whose
/// world is shallower than the depth, shows a session view one level deeper (<see cref="Children"/>), named from this
/// view's head and the screen (<see cref="WorldViewNames.Nested"/>), whose own screens are another level, so a portal seen
/// through a portal renders recursively and two portals facing each other end at the depth. A session screen at the
/// depth draws its fallback colour instead (<see cref="WorldPortalFallback"/>): one rule for every face past it. Every
/// other screen shows what the boot world's would, from the presented world's own sources (<see cref="World"/>): a
/// producer whose content is a pure function of its settings shows the shared source instance of that content; a
/// machine or a probe a source instance of the world's own host; a camera view a view of the world through its own
/// camera (<see cref="Cameras"/>, named under this level by <see cref="WorldViewNames.NestedCamera"/>); and text its
/// own lines, which the world's own session emitter draws through its own fonts
/// (<see cref="WorldSessionSceneEmitter.ScreenDecals"/>). A producer of the local device's content, such as a camera or a
/// desktop capture, is never opened for a world shown through a screen, and shows nothing.</para>
/// <para><see cref="Reconcile"/> runs once per produced frame and allocates only when the world's definition, the
/// nesting depth or the sessions its screens show change; the reads and source instances it publishes keep their
/// identity until then.</para>
/// </summary>
/// <typeparam name="TChild">What a session view is to the presentation.</typeparam>
public sealed class WorldNestedScreens<TChild> where TChild : class {
    private readonly Func<WorldDefinition?> m_definition;
    private readonly Func<string, bool> m_shares;

    private readonly Dictionary<int, TChild> m_children = [];
    private readonly Dictionary<int, WorldScreenSource.Session> m_childSources = [];
    // The views in screen order, and their screens, rebuilt whenever one opens or closes, so a frame walks them without
    // allocating.
    private readonly List<TChild> m_views = [];
    private readonly List<int> m_viewScreens = [];
    private readonly Dictionary<int, string> m_names = [];
    private readonly List<int> m_closing = [];
    // The view name of each camera the screens show, by camera name, kept across reconciles, and the camera view each
    // view screen shows, by screen index, as last reconciled.
    private readonly Dictionary<string, string> m_cameraNames = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<int, string> m_cameraViews = [];

    private WorldDefinition? m_reconciled;

    private int m_reconciledDepth = -1;
    private IReadOnlyList<WorldScreen> m_rows = [];

    /// <summary>Initializes a new instance of the <see cref="WorldNestedScreens{TChild}"/> class.</summary>
    /// <param name="head">The name the sessions the screens show are named under (<see cref="WorldViewNames.Nested"/>).</param>
    /// <param name="depth">How many screens deep the presented world is: 0 for a world a seat is presented in, a
    /// session's own depth for its destination.</param>
    /// <param name="definition">Reads the presented world's definition, or <see langword="null"/> while it has
    /// none.</param>
    /// <param name="shares">Whether a producer, by id, shows content a pure function of its settings, which every world
    /// shares.</param>
    /// <param name="world">The name of the presented world's instance, whose own hosts run the machines and probes its
    /// screens show.</param>
    /// <exception cref="ArgumentException"><paramref name="head"/> or <paramref name="world"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="shares"/> is
    /// <see langword="null"/>.</exception>
    public WorldNestedScreens(string head, int depth, Func<WorldDefinition?> definition, Func<string, bool> shares, string world) {
        ArgumentException.ThrowIfNullOrEmpty(argument: head);
        ArgumentOutOfRangeException.ThrowIfNegative(value: depth);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: shares);
        ArgumentException.ThrowIfNullOrEmpty(argument: world);

        Head = head;
        Depth = depth;
        m_definition = definition;
        m_shares = shares;
        World = world;
        Mappings = new WorldScreenMappingSet(world: world);
    }

    /// <summary>Gets the session views the screens show one level deeper, by screen index.</summary>
    public IReadOnlyDictionary<int, TChild> Children => m_children;
    /// <summary>Gets the session views the screens show one level deeper, in screen order; a frame indexes it without
    /// allocating.</summary>
    public IReadOnlyList<TChild> Views => m_views;
    /// <summary>Gets how many screens deep the presented world is.</summary>
    public int Depth { get; }
    /// <summary>Gets the name the sessions the screens show are named under.</summary>
    public string Head { get; }
    /// <summary>Gets the name of the presented world's instance, whose own hosts run the machines and probes its screens
    /// show.</summary>
    public string World { get; }
    /// <summary>Gets each screen's mapping, named by what it shows: a session view, a camera view, a source instance, or
    /// its fallback colour's.</summary>
    public WorldScreenMappingSet Mappings { get; }

    /// <summary>Gets the cameras of the presented world its screens show, each a view of the world named under this level,
    /// in the order of the first screen showing each.</summary>
    public IReadOnlyList<WorldNestedCamera> Cameras { get; private set; } = [];
    /// <summary>Gets the instances the level's view of the presented world reads within the frame so its screens show
    /// them: the session views one level deeper, the camera views, then the source instances, a list kept until any
    /// changes.</summary>
    public IReadOnlyList<string> Reads { get; private set; } = [];
    /// <summary>Gets the instances a camera view of the presented world reads within the frame: what
    /// <see cref="Reads"/> names but the camera views, which a camera view reads at their previous frame, so a camera
    /// filming a screen that shows a camera never reads it within one frame.</summary>
    public IReadOnlyList<string> FilmReads { get; private set; } = [];
    /// <summary>Gets the names of the camera views (<see cref="Cameras"/>), a list kept until they change: what a camera
    /// view of the presented world reads at its previous frame.</summary>
    public IReadOnlyList<string> CameraReads { get; private set; } = [];

    /// <summary>Gets the screen rows as they were last reconciled: the world's screens, then its derived faces.</summary>
    public IReadOnlyList<WorldScreen> Rows => m_rows;
    /// <summary>Gets the source instances the screens show.</summary>
    public IReadOnlyList<RenderGraphInstance> Sources => Mappings.Sources.Instances;

    // What a screen shows: its own source, but a producer of the local device's content, which shows nothing.
    private WorldScreen Shown(WorldScreen row) => (((row.Source is WorldScreenSource.Producer producer) && !m_shares(arg: producer.Id))
        ? (row with { Source = new WorldScreenSource.None() })
        : row);
    // The view name a camera of the presented world renders under at this level.
    private string CameraView(WorldCamera camera) {
        if (!m_cameraNames.TryGetValue(
            key: camera.Name,
            value: out var name
        )) {
            name = WorldViewNames.NestedCamera(
                camera: camera.Name,
                view: Head
            );
            m_cameraNames.Add(
                key: camera.Name,
                value: name
            );
        }

        return name;
    }

    /// <summary>Finds the row of a screen.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The row, or <see langword="null"/> when no reconciled row has that index.</returns>
    public WorldScreen? RowOf(int screen) {
        for (var index = 0; (index < m_rows.Count); index++) {
            if (m_rows[index].Index == screen) {
                return m_rows[index];
            }
        }

        return null;
    }
    /// <summary>Finds a camera view of this level by its view name.</summary>
    /// <param name="name">The view name (<see cref="WorldViewNames.NestedCamera"/>).</param>
    /// <param name="camera">The camera view when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the screens show the camera view.</returns>
    public bool TryCamera(string name, out WorldNestedCamera camera) {
        foreach (var candidate in Cameras) {
            if (string.Equals(
                a: candidate.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                camera = candidate;

                return true;
            }
        }

        camera = default;

        return false;
    }
    /// <summary>Returns the instance a screen reads: the session view or camera view it shows, or the source instance of
    /// its producer, machine, probe or fallback colour.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows nothing.</returns>
    public string? InstanceOf(int screen) => (m_children.ContainsKey(key: screen)
        ? m_names[screen]
        : (m_cameraViews.TryGetValue(
            key: screen,
            value: out var camera
        )
            ? camera
            : Mappings.InstanceOf(screen: screen)));
    /// <summary>Follows the presented world's definition and the sessions its screens show for this frame: rows and
    /// mappings move when the definition or the nesting depth does, and each session screen within the depth keeps its
    /// view while it still shows the session the authority holds, opens one when it holds one, and closes one it no longer
    /// shows.</summary>
    /// <param name="nestingDepth">The presentation's nesting depth.</param>
    /// <param name="sessions">Opens, keeps and closes the session views.</param>
    /// <returns><see langword="true"/> when the rows, the views or the reads changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is <see langword="null"/>.</exception>
    public bool Reconcile(int nestingDepth, IWorldNestedSessions<TChild> sessions) {
        ArgumentNullException.ThrowIfNull(argument: sessions);

        var definition = m_definition();
        var within = (Depth < nestingDepth);
        var rowsMoved = (
            !ReferenceEquals(
                objA: definition,
                objB: m_reconciled
            ) ||
            ((m_reconciledDepth < 0) || (within != (Depth < m_reconciledDepth)))
        );

        if (rowsMoved) {
            m_reconciled = definition;
            m_reconciledDepth = nestingDepth;
            m_rows = ((definition is null)
                ? []
                : [
                    .. definition.Screens,
                    .. WorldPrototypeFacets.Seated(definition: definition),
                ]);
        }

        var childrenMoved = false;

        m_closing.Clear();

        for (var index = 0; (index < m_views.Count); index++) {
            var screen = m_viewScreens[index];
            var child = m_views[index];

            if (
                !within ||
                (RowOf(screen: screen) is not { Source: WorldScreenSource.Session session }) ||
                (m_childSources[screen] != session) ||
                !sessions.Holds(
                    child: child,
                    screen: screen,
                    screens: this,
                    source: session
                )
            ) {
                m_closing.Add(item: screen);
            }
        }

        foreach (var screen in m_closing) {
            sessions.Close(child: m_children[screen]);
            _ = m_children.Remove(key: screen);
            _ = m_childSources.Remove(key: screen);
            childrenMoved = true;
        }

        if (within) {
            for (var position = 0; (position < m_rows.Count); position++) {
                var row = m_rows[position];

                if (
                    (row.Source is not WorldScreenSource.Session session) ||
                    m_children.ContainsKey(key: row.Index)
                ) {
                    continue;
                }

                if (!m_names.TryGetValue(
                    key: row.Index,
                    value: out var name
                )) {
                    name = WorldViewNames.Nested(
                        screen: row.Index,
                        view: Head
                    );
                    m_names.Add(
                        key: row.Index,
                        value: name
                    );
                }

                if (sessions.Open(
                    name: name,
                    screen: row.Index,
                    screens: this,
                    source: session
                ) is { } opened) {
                    m_children.Add(
                        key: row.Index,
                        value: opened
                    );
                    m_childSources.Add(
                        key: row.Index,
                        value: session
                    );
                    childrenMoved = true;
                }
            }
        }

        if (!(rowsMoved || childrenMoved)) {
            return false;
        }

        if (childrenMoved) {
            m_viewScreens.Clear();
            m_viewScreens.AddRange(collection: m_children.Keys.Order());
            m_views.Clear();
            m_views.AddRange(collection: m_viewScreens.Select(selector: screen => m_children[screen]));
        }

        var shown = m_rows.Select(selector: Shown).ToArray();
        var cameras = (definition?.Cameras ?? []);

        Mappings.Reconcile(
            cameraView: CameraView,
            cameras: cameras,
            screens: shown,
            sessionView: screen => (m_children.ContainsKey(key: screen)
                ? m_names[screen]
                : null),
            sessionsPastDepth: !within
        );

        var filmed = new List<WorldNestedCamera>();

        m_cameraViews.Clear();

        foreach (var row in shown) {
            if (
                (row.Source is WorldScreenSource.View view) &&
                (cameras.FirstOrDefault(predicate: camera => string.Equals(
                    a: camera.Name,
                    b: view.CameraName,
                    comparisonType: StringComparison.Ordinal
                )) is { } camera)
            ) {
                var name = CameraView(camera: camera);

                m_cameraViews[row.Index] = name;

                if (!filmed.Exists(match: other => string.Equals(
                    a: other.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                ))) {
                    filmed.Add(item: new WorldNestedCamera(
                        Camera: camera,
                        Name: name
                    ));
                }
            }
        }

        if (!filmed.Select(selector: static camera => camera.Name).SequenceEqual(second: CameraReads)) {
            CameraReads = [.. filmed.Select(selector: static camera => camera.Name)];
        }

        Cameras = filmed;
        FilmReads = [
            .. m_viewScreens.Select(selector: screen => m_names[screen]),
            .. Mappings.Sources.Instances.Select(selector: static source => source.Name),
        ];
        Reads = [
            .. m_viewScreens.Select(selector: screen => m_names[screen]),
            .. filmed.Select(selector: static camera => camera.Name),
            .. Mappings.Sources.Instances.Select(selector: static source => source.Name),
        ];

        return true;
    }
    /// <summary>Closes every session view the screens show, as the view of the presented world closes.</summary>
    /// <param name="sessions">Closes the views.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is <see langword="null"/>.</exception>
    public void Close(IWorldNestedSessions<TChild> sessions) {
        ArgumentNullException.ThrowIfNull(argument: sessions);

        foreach (var child in m_views) {
            sessions.Close(child: child);
        }

        m_children.Clear();
        m_childSources.Clear();
        m_views.Clear();
        m_viewScreens.Clear();
    }
}
/// <summary>A camera of a world shown through a screen, as one level of nesting films it: a view of that world named under
/// the level (<see cref="WorldViewNames.NestedCamera"/>).</summary>
/// <param name="Name">The view's instance name.</param>
/// <param name="Camera">The presented world's camera row.</param>
public readonly record struct WorldNestedCamera(string Name, WorldCamera Camera);
