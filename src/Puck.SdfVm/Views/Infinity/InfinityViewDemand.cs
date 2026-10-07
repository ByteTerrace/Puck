namespace Puck.SdfVm.Views;

/// <summary>
/// Which infinity views the next frame renders. A view renders only while some view's previous frame showed it on an
/// uncovered pixel: the viewer's <c>composite</c> counts the texels it read of the view's image (or of its fallback
/// colour before an image exists), the host reports each count when the counters read back, and a view whose latest
/// report is zero renders nothing and keeps its last image. A view nobody has reported is not demanded; the first frame
/// that shows it reports its texels, and the next renders it, so a view the viewer turns toward appears one frame late
/// and one it turns from stops on the next frame.
/// </summary>
public sealed class InfinityViewDemand {
    private readonly Dictionary<string, long> m_shown = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets how many views are demanded.</summary>
    public int Count { get; private set; }

    /// <summary>Records the texels every viewer's latest frame read of a view, replacing the last report.</summary>
    /// <param name="view">The view's instance name.</param>
    /// <param name="texels">The texels read, summed over the viewers; zero when no uncovered pixel shows the view.</param>
    /// <exception cref="ArgumentException"><paramref name="view"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="texels"/> is negative.</exception>
    public void Report(string view, long texels) {
        ArgumentException.ThrowIfNullOrEmpty(argument: view);
        ArgumentOutOfRangeException.ThrowIfNegative(value: texels);

        var wasDemanded = (m_shown.TryGetValue(key: view, value: out var before) && (before > 0L));

        m_shown[view] = texels;

        if (wasDemanded != (texels > 0L)) {
            Count += (wasDemanded ? -1 : 1);
        }
    }
    /// <summary>Returns whether a view renders on the next frame.</summary>
    /// <param name="view">The view's instance name.</param>
    /// <returns><see langword="true"/> when its latest report is positive.</returns>
    public bool IsDemanded(string view) => (m_shown.TryGetValue(key: view, value: out var texels) && (texels > 0L));
    /// <summary>Forgets a view that left the world, so a view later added under its name starts undemanded.</summary>
    /// <param name="view">The view's instance name.</param>
    public void Forget(string view) {
        if (m_shown.Remove(key: view, value: out var texels) && (texels > 0L)) {
            Count--;
        }
    }
}
