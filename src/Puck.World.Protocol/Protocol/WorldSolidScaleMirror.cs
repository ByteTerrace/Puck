using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The contact-sampled size endpoints a presentation interpolates beside its body poses.</summary>
public sealed class WorldSolidScaleMirror {
    private readonly Dictionary<string, (float Previous, float Current)> m_sizes = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> m_seen = new(comparer: StringComparer.Ordinal);
    private readonly List<string> m_removed = [];

    /// <summary>Copies a complete tick's sampled sizes, retaining the preceding endpoint for surviving roots.</summary>
    public void Deliver(ReadOnlySpan<WorldSolidScale> sizes) {
        m_seen.Clear();
        foreach (var sample in sizes) {
            if (!m_seen.Add(item: sample.Placement)) { continue; }
            var value = ((float)sample.Scale);

            m_sizes[sample.Placement] = ((m_sizes.TryGetValue(key: sample.Placement, value: out var prior) ? prior.Current : value), value);
        }
        m_removed.Clear();
        foreach (var id in m_sizes.Keys) { if (!m_seen.Contains(item: id)) { m_removed.Add(item: id); } }
        foreach (var id in m_removed) { m_sizes.Remove(key: id); }
    }
    /// <summary>Reads the sampled root at the same interpolation fraction as its supporting bodies.</summary>
    public float? Read(string placementId, float alpha) => (m_sizes.TryGetValue(key: placementId, value: out var size)
        ? ((size.Previous == size.Current) ? size.Current : float.Lerp(amount: alpha, value1: size.Previous, value2: size.Current)) : null);
}
