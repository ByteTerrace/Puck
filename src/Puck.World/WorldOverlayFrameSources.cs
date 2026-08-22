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
    // The stable key table, keyed by (source, seat) rather than the source alone: a bare Camera source with no
    // authored Seat (record-equal across every seat's identity panel) still needs a DISTINCT key per seat — the
    // enclosing seat scope KeyFor's caller passes is part of the source's identity here, not a resolve-time detail.
    private readonly Dictionary<(WorldFrameSource Source, int Seat), int> m_keys = new();
    private readonly List<(WorldFrameSource Source, int Seat)> m_sources = [];

    /// <summary>Resolves the stable key for a <see cref="WorldFrameSource"/> in a seat scope, declaring it with the
    /// binder on first sight so its feed opens even when no <c>screens</c> row names it.</summary>
    /// <param name="source">The frame source a HUD element names.</param>
    /// <param name="seat">The 1-based enclosing seat scope — the owning identity panel's slot+1 for a player-scope
    /// element, or 1 for a world-scope element. A camera source's own authored <c>Seat</c>, when present, still
    /// wins; this is only the fallback and the disambiguator between two seats' otherwise-identical bare sources.</param>
    /// <returns>The key <see cref="TryAcquire"/> resolves.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public int KeyFor(WorldFrameSource source, int seat) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var entry = (source, seat);

        if (m_keys.TryGetValue(
            key: entry,
            value: out var existing
        )) {
            return existing;
        }

        m_binder.DeclareFrameSource(source: source, seat: seat);

        var key = m_sources.Count;

        m_sources.Add(item: entry);
        m_keys[entry] = key;

        return key;
    }
    /// <inheritdoc/>
    public bool TryAcquire(int key, out OverlayFrameLease lease) {
        if (
            (key < 0) ||
            (key >= m_sources.Count) ||
            !m_binder.TryAcquireFrame(
                source: m_sources[key].Source,
                seat: m_sources[key].Seat,
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
