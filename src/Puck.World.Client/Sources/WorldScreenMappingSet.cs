using System.Diagnostics.CodeAnalysis;
using Puck.Commands;
using Puck.SdfVm.Views;

namespace Puck.World.Client;

/// <summary>What a screen shows at run time, as <see cref="WorldScreenMappingSet"/> reads it: the extent of an image only
/// the running producer knows.</summary>
public interface IWorldScreenImages {
    /// <summary>Finds the extent of the image a producer, machine or probe source shows on a screen.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <param name="width">The image's width, in pixels, when this returns <see langword="true"/>.</param>
    /// <param name="height">The image's height, in pixels, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the image's extent is known and positive.</returns>
    bool TryExtent(int screen, out int width, out int height);
}
/// <summary>
/// The mapping every screen publishes (<see cref="WorldScreenMappings.Of"/>, with the screen glass's bezel as its warp)
/// for the source it shows — its row's, or the source a live presentation verb bound over the row — named by the handle
/// of the instance that source is: a producer, machine or probe source by its source instance
/// (<see cref="WorldSourceInstances"/>, <c>source$&lt;producer&gt;$&lt;digest&gt;</c>), a view by its camera's
/// registration (<see cref="WorldSeatAnchors.RegistrationName"/>) and a session by its screen's session view
/// (<see cref="WorldViewNames.Session"/>). A view's and a session's extent is document data; a source instance's is the
/// running image's (<see cref="IWorldScreenImages.TryExtent"/>). A screen showing nothing, text, or an image of unknown
/// extent publishes no mapping, and <see cref="Describe"/> says why.
/// <para><see cref="Reconcile"/> runs when the rows, the live binds or the cameras change and allocates;
/// <see cref="Publish"/> runs every frame and, while every row's handle and extent hold, publishes the mappings it
/// published before without allocating.</para>
/// </summary>
public sealed class WorldScreenMappingSet {
    private const string NoExtent = "the image's extent is not known yet";
    private const string NoImage = "no image source";
    private const string Unpublished = "not published";

    private readonly List<SourceMapping> m_published = [];
    private Row[] m_rows = [];

    /// <summary>Gets the mapping of every screen that publishes one, in row order, as <see cref="Publish"/> last
    /// published them. The set rewrites the list in place.</summary>
    public IReadOnlyList<SourceMapping> Mappings => m_published;

    /// <summary>Gets the screen rows the set last reconciled, in order.</summary>
    public IReadOnlyList<WorldScreen> Screens { get; private set; } = [];
    /// <summary>Gets the source instances the screens show, a new value on every <see cref="Reconcile"/>: the render graph
    /// runs them, and each screen showing one samples its image.</summary>
    public WorldSourceInstances Sources { get; private set; } = WorldSourceInstances.Of(shown: []);

    // The row a screen's source names: its handle and document extent, or why it names none.
    private static Row RowOf(WorldScreen screen, int position, WorldSourceInstances sources, IReadOnlyList<WorldCamera> cameras) {
        switch (screen.Source) {
            case WorldScreenSource.View view:
                foreach (var camera in cameras) {
                    if (string.Equals(
                        a: camera.Name,
                        b: view.CameraName,
                        comparisonType: StringComparison.Ordinal
                    )) {
                        return new Row(
                            extent: (((int)camera.RenderWidth), ((int)camera.RenderHeight)),
                            handle: SourceHandle.Instance(name: WorldSeatAnchors.RegistrationName(
                                camera: camera,
                                seat: 1
                            )),
                            refusal: null,
                            screen: screen
                        );
                    }
                }

                return new Row(
                    extent: null,
                    handle: null,
                    refusal: $"camera '{view.CameraName}' not declared",
                    screen: screen
                );
            case WorldScreenSource.Session session:
                return new Row(
                    extent: ((session.Resolution is { } resolution)
                        ? (resolution.Width, resolution.Height)
                        : (((int)WorldSessionView.DefaultWidth), ((int)WorldSessionView.DefaultHeight))),
                    handle: SourceHandle.Instance(name: WorldViewNames.Session(screen: screen.Index)),
                    refusal: null,
                    screen: screen
                );
            default:
                var handle = sources.HandleOf(screen: position);

                return new Row(
                    extent: null,
                    handle: handle,
                    refusal: ((handle is null)
                        ? NoImage
                        : null),
                    screen: screen
                );
        }
    }

    /// <summary>Describes a screen's mapping on one line: <see cref="SourceMapping.Describe"/> of the mapping it
    /// publishes, or <c>none (&lt;reason&gt;)</c>.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The description, or <see langword="null"/> when no reconciled row has that index.</returns>
    public string? Describe(int screen) {
        foreach (var row in m_rows) {
            if (row.Screen.Index == screen) {
                return ((row.Mapping is { } mapping)
                    ? mapping.Describe()
                    : $"none ({row.Refusal})");
            }
        }

        return null;
    }
    /// <summary>Publishes every row's mapping for this frame, reading the running images' extents. A row whose handle
    /// and extent hold keeps the mapping it published before.</summary>
    /// <param name="images">What each screen shows at run time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> is <see langword="null"/>.</exception>
    public void Publish(IWorldScreenImages images) {
        ArgumentNullException.ThrowIfNull(argument: images);

        m_published.Clear();

        foreach (var row in m_rows) {
            row.Publish(images: images);

            if (row.Mapping is { } mapping) {
                m_published.Add(item: mapping);
            }
        }
    }
    /// <summary>Reconciles the set with the sources the screens now show and the cameras they name: derives each screen's
    /// source instance and handle, and drops every mapping, which the next <see cref="Publish"/> rebuilds. A screen shows
    /// the source a live presentation verb bound over its row, or else its row's.</summary>
    /// <param name="screens">The screen rows, in order.</param>
    /// <param name="cameras">The cameras a view source names.</param>
    /// <param name="live">The source a live presentation verb shows over a row, by screen index, or
    /// <see langword="null"/> for none; the set reads it only while it reconciles.</param>
    /// <exception cref="ArgumentNullException"><paramref name="screens"/> or <paramref name="cameras"/> is
    /// <see langword="null"/>.</exception>
    public void Reconcile(IReadOnlyList<WorldScreen> screens, IReadOnlyList<WorldCamera> cameras, IReadOnlyDictionary<int, WorldScreenSource>? live = null) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: cameras);

        var shown = new WorldScreen[screens.Count];

        for (var position = 0; (position < screens.Count); position++) {
            var screen = screens[position];

            shown[position] = (((live is not null) && live.TryGetValue(
                key: screen.Index,
                value: out var bound
            ))
                ? (screen with { Source = bound })
                : screen);
        }

        var sources = WorldSourceInstances.Of(shown: [.. shown.Select(selector: static screen => screen.Source)]);
        var rows = new Row[shown.Length];

        for (var position = 0; (position < shown.Length); position++) {
            rows[position] = RowOf(
                cameras: cameras,
                position: position,
                screen: shown[position],
                sources: sources
            );
        }

        m_rows = rows;
        m_published.Clear();
        Screens = screens;
        Sources = sources;
    }
    /// <summary>Returns the name of the source instance a screen shows, without allocating.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when no reconciled row has that index or its source is
    /// no source instance.</returns>
    public string? InstanceOf(int screen) {
        for (var position = 0; (position < m_rows.Length); position++) {
            if (m_rows[position].Screen.Index == screen) {
                return Sources.InstanceOf(screen: position);
            }
        }

        return null;
    }
    /// <summary>Finds the mapping a screen last published.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <param name="mapping">The mapping when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the screen published a mapping.</returns>
    public bool TryGet(int screen, [NotNullWhen(returnValue: true)] out SourceMapping? mapping) {
        foreach (var row in m_rows) {
            if (
                (row.Screen.Index == screen) &&
                (row.Mapping is { } published)
            ) {
                mapping = published;

                return true;
            }
        }

        mapping = null;

        return false;
    }

    // One screen row: the handle its source names, the extent the document gives it (a view or a session), and the
    // mapping it last published.
    private sealed class Row(WorldScreen screen, SourceHandle? handle, (int Width, int Height)? extent, string? refusal) {
        private readonly (int Width, int Height)? m_extent = extent;
        private readonly SourceHandle? m_handle = handle;
        private readonly string? m_refusal = refusal;

        public SourceMapping? Mapping { get; private set; }

        public string? Refusal { get; private set; } = (refusal ?? Unpublished);
        public WorldScreen Screen { get; } = screen;

        public void Publish(IWorldScreenImages images) {
            if (m_handle is not { } handle) {
                Mapping = null;
                Refusal = m_refusal;

                return;
            }
            var width = 0;
            var height = 0;

            if (m_extent is { } known) {
                (width, height) = known;
            } else if (!images.TryExtent(
                height: out height,
                screen: Screen.Index,
                width: out width
            )) {
                Mapping = null;
                Refusal = NoExtent;

                return;
            }
            if (
                (Mapping is { } published) &&
                (published.SourceWidth == width) &&
                (published.SourceHeight == height)
            ) {
                return;
            }

            var mapping = WorldScreenMappings.Of(
                screen: Screen,
                source: handle,
                sourceHeight: height,
                sourceWidth: width
            );

            if (mapping.TryValidate(refusal: out var invalid)) {
                Mapping = mapping;
                Refusal = null;
            } else {
                Mapping = null;
                Refusal = invalid;
            }
        }
    }
}
