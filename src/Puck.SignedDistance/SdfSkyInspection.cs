namespace Puck.SignedDistance;

/// <summary>An immutable sky inspection selection captured by one view. A solo ignores stored mutes until it is
/// cleared. The host owns seat and world routing; nothing here changes the shared authored sky.</summary>
public sealed class SdfSkyInspection {
    private readonly string[] m_muted;

    private SdfSkyInspection(string? solo, string[] muted) {
        Solo = solo;
        m_muted = muted;
    }

    /// <summary>The inert selection, shared by seats that have no overrides.</summary>
    public static SdfSkyInspection None { get; } = new(muted: [], solo: null);
    /// <summary>The sole admitted layer, or null when the stored mutes apply.</summary>
    public string? Solo { get; }
    /// <summary>The muted layer names in ordinal order, including mutes temporarily hidden by a solo.</summary>
    public ReadOnlySpan<string> Muted => m_muted;

    /// <summary>Tests whether inspection admits an authored layer; authored opacity, tier and visibility still apply.</summary>
    /// <param name="layer">The prepared layer name.</param>
    /// <returns>Whether this inspection selection allows the layer.</returns>
    public bool Allows(string layer) => ((Solo is { } solo)
        ? string.Equals(a: solo, b: layer, comparisonType: StringComparison.Ordinal)
        : (Array.BinarySearch(m_muted, layer, StringComparer.Ordinal) < 0));
    /// <summary>Captures a new solo while retaining the stored mutes.</summary>
    /// <param name="layer">The soloed layer, or null to restore the mute set.</param>
    /// <returns>This instance when unchanged; otherwise a new immutable selection.</returns>
    /// <exception cref="ArgumentException">The layer name is empty.</exception>
    public SdfSkyInspection WithSolo(string? layer) {
        if (layer is not null) { ArgumentException.ThrowIfNullOrEmpty(layer); }
        if (string.Equals(a: Solo, b: layer, comparisonType: StringComparison.Ordinal)) { return this; }
        return (((layer is null) && (m_muted.Length == 0)) ? None : new(muted: m_muted, solo: layer));
    }
    /// <summary>Captures one changed mute without altering the solo.</summary>
    /// <param name="layer">The authored layer name.</param>
    /// <param name="muted">Whether to retain this layer in the mute set.</param>
    /// <returns>This instance when unchanged; otherwise a new immutable selection.</returns>
    /// <exception cref="ArgumentException">The layer name is empty.</exception>
    public SdfSkyInspection WithMute(string layer, bool muted) {
        ArgumentException.ThrowIfNullOrEmpty(layer);
        var found = Array.BinarySearch(m_muted, layer, StringComparer.Ordinal);

        if ((found >= 0) == muted) { return this; }
        var next = new string[(m_muted.Length + (muted ? 1 : -1))];
        var index = (muted ? ~found : found);

        m_muted.AsSpan(length: index, start: 0).CopyTo(destination: next);
        if (muted) {
            next[index] = layer;
            m_muted.AsSpan(start: index).CopyTo(destination: next.AsSpan(start: (index + 1)));
        } else {
            m_muted.AsSpan(start: (index + 1)).CopyTo(destination: next.AsSpan(start: index));
        }
        return (((Solo is null) && (next.Length == 0)) ? None : new(Solo, next));
    }
}
