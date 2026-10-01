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
/// depth draws its fallback colour instead (<see cref="WorldPortalFallback"/>): one rule for every face past it. A
/// producer whose content is a pure function of its settings shows the shared source instance of that content, as the
/// boot world's screens do; every other source (a machine, a probe, a camera view, text, and a producer of the local
/// device's content such as a camera or a desktop capture, which a world shown through a screen never opens) shows
/// nothing.</para>
/// <para><see cref="Reconcile"/> runs once per produced frame and allocates only when the world's definition, the
/// nesting depth or the sessions its screens show change; the reads and source instances it publishes keep their
/// identity until then.</para>
/// </summary>
/// <typeparam name="TChild">What a session view is to the presentation.</typeparam>
public sealed class WorldNestedScreens<TChild> where TChild : class {
    private readonly Func<WorldDefinition?> m_definition;
    private readonly Func<string, bool> m_shares;

    private readonly Dictionary<int, TChild> m_children = [];
    // The views in screen order, and their screens, rebuilt whenever one opens or closes, so a frame walks them without
    // allocating.
    private readonly List<TChild> m_views = [];
    private readonly List<int> m_viewScreens = [];
    private readonly Dictionary<int, string> m_names = [];
    private readonly List<int> m_closing = [];

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
    /// <exception cref="ArgumentException"><paramref name="head"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="shares"/> is
    /// <see langword="null"/>.</exception>
    public WorldNestedScreens(string head, int depth, Func<WorldDefinition?> definition, Func<string, bool> shares) {
        ArgumentException.ThrowIfNullOrEmpty(argument: head);
        ArgumentOutOfRangeException.ThrowIfNegative(value: depth);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: shares);

        Head = head;
        Depth = depth;
        m_definition = definition;
        m_shares = shares;
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

    /// <summary>Gets each screen's mapping, named by what it shows: a session view, a shared source instance, or its
    /// fallback colour's.</summary>
    public WorldScreenMappingSet Mappings { get; } = new();
    /// <summary>Gets the instances a view of the presented world reads within the frame so its screens show them: the
    /// session views one level deeper, then the source instances, a list kept until either changes.</summary>
    public IReadOnlyList<string> Reads { get; private set; } = [];

    /// <summary>Gets the screen rows as they were last reconciled: the world's screens, then its derived faces.</summary>
    public IReadOnlyList<WorldScreen> Rows => m_rows;
    /// <summary>Gets the source instances the screens show.</summary>
    public IReadOnlyList<RenderGraphInstance> Sources => Mappings.Sources.Instances;

    // What a screen shows: a session (its view within the depth, its fallback colour at it), a shared producer, and
    // nothing else.
    private WorldScreen Shown(WorldScreen row) => row.Source switch {
        WorldScreenSource.Session => row,
        WorldScreenSource.Producer producer when m_shares(arg: producer.Id) => row,
        WorldScreenSource.None => row,
        _ => (row with { Source = new WorldScreenSource.None() }),
    };

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
    /// <summary>Returns the instance a screen reads: the session view it shows, or the source instance of its producer or
    /// fallback colour.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows nothing.</returns>
    public string? InstanceOf(int screen) => (m_children.ContainsKey(key: screen)
        ? m_names[screen]
        : Mappings.InstanceOf(screen: screen));
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

        Mappings.Reconcile(
            cameras: [],
            screens: [.. m_rows.Select(selector: Shown)],
            sessionView: screen => (m_children.ContainsKey(key: screen)
                ? m_names[screen]
                : null),
            sessionsPastDepth: !within
        );
        Reads = [
            .. m_viewScreens.Select(selector: screen => m_names[screen]),
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
        m_views.Clear();
        m_viewScreens.Clear();
    }
}
