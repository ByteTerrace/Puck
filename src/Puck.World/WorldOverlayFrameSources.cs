using Puck.Overlays;

namespace Puck.World;

/// <summary>
/// The unified overlay's <see cref="IOverlayFrameSources"/> implementation for Puck.World — adapts the binder's
/// <see cref="WorldFrameSource"/> vocabulary (camera/view/probe/capture) to the opaque integer keys a HUD
/// <c>Frame</c> element's overlay slot addresses its source by. <see cref="WorldHudFeed"/> assigns a key to every
/// declared <c>Frame</c> element's source through <see cref="KeyFor"/> on the structure rebuild; the compositor's
/// per-produced-frame slot table calls <see cref="TryAcquire"/> to resolve that key's live lease. A single-threaded
/// class: both calls run on the render thread, the same discipline every other binder-owned feed already keeps.
/// </summary>
internal sealed class WorldOverlayFrameSources(WorldScreenBinder binder) : IOverlayFrameSources {
    private readonly WorldScreenBinder m_binder = binder;
    // The stable key table: a WorldFrameSource never changes key once assigned, and record equality means two HUD
    // elements naming the identical source (same $type and fields) share one key, one binder-owned feed, and one
    // overlay slot.
    private readonly Dictionary<WorldFrameSource, int> m_keys = new();
    private readonly List<WorldFrameSource> m_sources = [];

    /// <summary>Resolves the stable key for a <see cref="WorldFrameSource"/>, declaring it with the binder on first
    /// sight so its feed opens even when no <c>screens</c> row names it.</summary>
    /// <param name="source">The frame source a HUD element names.</param>
    /// <returns>The key <see cref="TryAcquire"/> resolves.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public int KeyFor(WorldFrameSource source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (m_keys.TryGetValue(
            key: source,
            value: out var existing
        )) {
            return existing;
        }

        m_binder.DeclareFrameSource(source: source);

        var key = m_sources.Count;

        m_sources.Add(item: source);
        m_keys[source] = key;

        return key;
    }
    /// <inheritdoc/>
    public bool TryAcquire(int key, out OverlayFrameLease lease) {
        if (
            (key < 0) ||
            (key >= m_sources.Count) ||
            !m_binder.TryAcquireFrame(
                source: m_sources[key],
                frame: out var frame
            )
        ) {
            lease = default;

            return false;
        }

        lease = new OverlayFrameLease(
            ImageViewHandle: frame.ImageViewHandle,
            Release: frame.Release,
            ReleaseToken: frame.ReleaseToken
        );

        return true;
    }
}
