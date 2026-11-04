using System.Globalization;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    /// <inheritdoc/>
    public bool IsAvailable(string view) {
        if (SessionFeedOf(view) is { } feed) { return Available(feed); }
        if (m_infinityOutputs.TryGetValue(view, out var infinity)) { return Available(infinity.Entry); }
        if (NestedCameraLevel(view) is { } level && m_nestedOwners.TryGetValue(level, out var owner)) {
            return (owner.Feed is null || Available(owner.Feed)) && (owner.Infinity is null || Available(owner.Infinity));
        }
        // Historical package entries can outlive the view roster. A retired generated observation is not a new
        // unnamed host view. Recognize the minted path grammar, not arbitrary prefixes (for example its environment
        // buffer is a different instance and is not an observation image).
        return !ObservationName(view);
    }
    private static bool ObservationName(string name) {
        ReadOnlySpan<char> path;
        if (GeneratedName.TryStripHead(name, WorldViewNames.SessionHead, out var session)) {
            path = session;
            if (!TakeOrdinal(ref path)) { return false; }
        } else if (GeneratedName.TryStripHead(name, WorldViewNames.RoutedHead, out var routed)) {
            path = routed;
            var split = path.IndexOf(GeneratedName.Joiner);
            if (split != 16 || !ulong.TryParse(path[..split], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) { return false; }
            path = path[(split + 1)..];
        } else if (GeneratedName.TryStripHead(name, WorldViewNames.SkyPart, out var sky)) {
            path = sky;
            var split = path.IndexOf(GeneratedName.Joiner);
            if (split < 0) { return !path.IsEmpty; }
            if (split == 0) { return false; }
            path = path[(split + 1)..];
        } else { return false; }
        while (!path.IsEmpty) {
            var split = path.IndexOf(GeneratedName.Joiner);
            if (split > 0 && path[..split].SequenceEqual(WorldViewNames.CameraPart)) {
                var leaf = path[(split + 1)..];
                return !leaf.IsEmpty && !leaf.Contains(GeneratedName.Joiner);
            }
            if (split > 0 && path[..split].SequenceEqual(WorldViewNames.SkyPart)) {
                path = path[(split + 1)..];
                split = path.IndexOf(GeneratedName.Joiner);
                if (split < 0) { return !path.IsEmpty; }
                if (split == 0) { return false; }
                path = path[(split + 1)..];
                continue;
            }
            if (!TakeOrdinal(ref path)) { return false; }
        }
        return true;
    }
    private static bool TakeOrdinal(ref ReadOnlySpan<char> path) {
        var split = path.IndexOf(GeneratedName.Joiner);
        var ordinal = ((split < 0) ? path : path[..split]);
        if (ordinal.IsEmpty || (ordinal.Length > 1 && ordinal[0] == '0') || !int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0) { return false; }
        path = ((split < 0) ? [] : path[(split + 1)..]);
        return split < 0 || !path.IsEmpty;
    }
    private static bool Available(InfinityEntry infinity) => (infinity.Hosted is null || infinity.Hosted.Observation is { CanObserve: true });
    private static bool Available(SessionFeed feed) {
        for (var current = feed; current is not null; current = current.ParentFeed) {
            if (current.Observation is not { CanObserve: true } || (current.ParentInfinity is { } infinity && !Available(infinity))) { return false; }
        }
        return true;
    }
}
