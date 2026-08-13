namespace Puck.Hosting;

/// <summary>The terminal's seat-indexed console-session control surface.</summary>
public interface IConsoleSessions {
    /// <summary>Gets the number of local seat sessions.</summary>
    int Count { get; }

    /// <summary>Reads one seat session's open state.</summary>
    bool TryGetVisible(int slot, out bool visible);

    /// <summary>Sets or toggles one seat session's open state.</summary>
    /// <param name="slot">The zero-based local seat.</param>
    /// <param name="visible">The requested side, or <see langword="null"/> to toggle.</param>
    /// <param name="resolved">The resulting side when the seat exists.</param>
    /// <returns><see langword="true"/> when <paramref name="slot"/> names a session.</returns>
    bool TrySetVisible(int slot, bool? visible, out bool resolved);
}
