using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Cameras;

namespace Puck.World.Client;

/// <summary>Where a camera view of a world shown through a screen rendered at its residency's last dress: the residency's
/// owner, the view's index in that frame, the camera it filmed from, and the level whose screens show it.</summary>
/// <typeparam name="TLevel">What a level of nesting is to the presentation.</typeparam>
public sealed class WorldFilmedView<TLevel> where TLevel : class {
    internal WorldFilmedView() { }

    /// <summary>Gets the camera the view filmed from, in its world's space.</summary>
    public CameraSnapshot Camera { get; internal set; }
    /// <summary>Gets the view's index in its residency's frame.</summary>
    public int Index { get; internal set; }
    /// <summary>Gets the level whose screens show the view.</summary>
    public TLevel Level { get; internal set; } = null!;
    /// <summary>Gets the owner of the residency the view rendered from: the routed scene or the session it dressed in.</summary>
    public object Target { get; internal set; } = null!;

    // Whether the dress being recorded filmed the view.
    internal bool Seen { get; set; }
}
/// <summary>
/// Every camera view of a world shown through a screen as its residencies last filmed them, by view name. A dress of one
/// residency (<see cref="Begin"/>, <see cref="Record"/>, <see cref="End"/>) keeps the views it films and drops every view
/// of that residency it no longer films, and <see cref="Retain"/> drops every view of a residency that dresses no more,
/// so the table holds no level, residency owner or camera once nothing shows it. A dress that films the views it filmed
/// before allocates nothing.
/// </summary>
/// <typeparam name="TLevel">What a level of nesting is to the presentation.</typeparam>
public sealed class WorldFilmedViews<TLevel> where TLevel : class {
    private readonly Dictionary<string, WorldFilmedView<TLevel>> m_films = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_dropped = [];

    /// <summary>Gets how many views the table holds.</summary>
    public int Count => m_films.Count;

    /// <summary>Starts recording one residency's dress: every view it filmed before is unseen until filmed again.</summary>
    /// <param name="target">The residency's owner.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    public void Begin(object target) {
        ArgumentNullException.ThrowIfNull(argument: target);

        foreach (var film in m_films.Values) {
            if (ReferenceEquals(
                objA: film.Target,
                objB: target
            )) {
                film.Seen = false;
            }
        }
    }
    /// <summary>Records a view one residency's dress filmed.</summary>
    /// <param name="name">The view's instance name.</param>
    /// <param name="target">The residency's owner.</param>
    /// <param name="index">The view's index in the dressed frame.</param>
    /// <param name="camera">The camera the view filmed from.</param>
    /// <param name="level">The level whose screens show the view.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>, <paramref name="target"/> or
    /// <paramref name="level"/> is <see langword="null"/>.</exception>
    public void Record(string name, object target, int index, in CameraSnapshot camera, TLevel level) {
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: target);
        ArgumentNullException.ThrowIfNull(argument: level);

        if (!m_films.TryGetValue(
            key: name,
            value: out var film
        )) {
            film = new WorldFilmedView<TLevel>();
            m_films.Add(
                key: name,
                value: film
            );
        }

        film.Camera = camera;
        film.Index = index;
        film.Level = level;
        film.Seen = true;
        film.Target = target;
    }
    /// <summary>Ends recording one residency's dress: every view of it the dress did not film is dropped.</summary>
    /// <param name="target">The residency's owner.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    public void End(object target) {
        ArgumentNullException.ThrowIfNull(argument: target);

        m_dropped.Clear();

        foreach (var (name, film) in m_films) {
            if (
                ReferenceEquals(
                    objA: film.Target,
                    objB: target
                ) &&
                !film.Seen
            ) {
                m_dropped.Add(item: name);
            }
        }

        foreach (var name in m_dropped) {
            _ = m_films.Remove(key: name);
        }

        m_dropped.Clear();
    }
    /// <summary>Drops every view no live level shows any more, whichever residency filmed it.</summary>
    /// <param name="shows">Whether a live level shows a view, by its name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shows"/> is <see langword="null"/>.</exception>
    public void Retain(Func<string, bool> shows) {
        ArgumentNullException.ThrowIfNull(argument: shows);

        m_dropped.Clear();

        foreach (var name in m_films.Keys) {
            if (!shows(arg: name)) {
                m_dropped.Add(item: name);
            }
        }

        foreach (var name in m_dropped) {
            _ = m_films.Remove(key: name);
        }

        m_dropped.Clear();
    }
    /// <summary>Finds a view by its name.</summary>
    /// <param name="name">The view's instance name.</param>
    /// <param name="film">The view when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the table holds the view.</returns>
    public bool TryGet(string name, [NotNullWhen(returnValue: true)] out WorldFilmedView<TLevel>? film) => m_films.TryGetValue(
        key: name,
        value: out film
    );
    /// <summary>Finds the level whose view a residency's last dress filmed at an index.</summary>
    /// <param name="target">The residency's owner.</param>
    /// <param name="index">The view's index in the dressed frame.</param>
    /// <returns>The level, or <see langword="null"/> when that dress filmed no camera view there.</returns>
    public TLevel? LevelAt(object target, int index) {
        foreach (var film in m_films.Values) {
            if (
                (film.Index == index) &&
                ReferenceEquals(
                    objA: film.Target,
                    objB: target
                )
            ) {
                return film.Level;
            }
        }

        return null;
    }

}
