using Puck.Abstractions;

namespace Puck.World.Server;

/// <summary>How a checkpoint names a file or directory: relative to a root the restoring host supplies, never as a
/// machine-local path, because a checkpoint is durable and crosses machines.</summary>
internal static class WorldCheckpointPaths {
    /// <summary>Returns a path's spelling under a root: forward-slashed, empty for the root itself.</summary>
    /// <param name="path">The file or directory to name.</param>
    /// <param name="root">The root it is named under.</param>
    /// <returns>The relative spelling, or <see langword="null"/> when the path lies outside the root.</returns>
    public static string? RelativeUnder(string path, string root) {
        var full = PuckPaths.Normalize(path: path).TrimEnd(trimChar: '/');
        var anchor = PuckPaths.Normalize(path: root).TrimEnd(trimChar: '/');

        if (string.Equals(a: full, b: anchor, comparisonType: PuckPaths.Comparison)) {
            return string.Empty;
        }

        return (full.StartsWith(value: (anchor + "/"), comparisonType: PuckPaths.Comparison)
            ? full[(anchor.Length + 1)..]
            : null);
    }
    /// <summary>Returns the full path a relative spelling names under a root.</summary>
    /// <param name="root">The root the restoring host supplies.</param>
    /// <param name="relative">The spelling a checkpoint carried, already proved not to climb out of its root.</param>
    /// <returns>The normalized full path.</returns>
    public static string Resolve(string root, string relative) => PuckPaths.Normalize(path: Path.Join(
        path1: root,
        path2: relative
    ));
    /// <summary>Returns whether a spelling a checkpoint carries is relative and stays under its root: forward-slashed,
    /// not rooted on any platform, and with no empty, <c>.</c> or <c>..</c> segment. Empty names the root itself.</summary>
    /// <param name="path">The spelling to check.</param>
    /// <returns><see langword="true"/> when the spelling names something under a root.</returns>
    public static bool IsRelativeUnderRoot(string path) {
        if (path.Length == 0) {
            return true;
        }

        if (
            path.Contains(value: '\\') ||
            path.Contains(value: ':') ||
            path.StartsWith(value: '/')
        ) {
            return false;
        }

        foreach (var segment in path.Split(separator: '/')) {
            if (segment is "" or "." or "..") {
                return false;
            }
        }

        return true;
    }
}
