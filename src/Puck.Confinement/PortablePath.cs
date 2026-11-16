namespace Puck.Confinement;

/// <summary>The one rule for whether a path is spelled to name the same relative file on every platform: what a name that
/// lands on a client's disk, or a path a document authors, must satisfy whichever host validates it. It sits in this
/// BCL-only leaf so every project that writes or checks a path, the storage layer and the launcher included, shares it.</summary>
public static class PortablePath {
    /// <summary>Tests whether <paramref name="path"/> is spelled so that it names the same relative file on every platform
    /// and can land under a directory on any of them, whichever host validates it: forward slashes only, no root, drive
    /// or scheme, no empty or <c>.</c> segment, no control character or one of <c>&lt;&gt;"|?*</c>, no segment that begins
    /// or ends with a space or ends with a dot, and no Windows device name (<c>CON</c>, <c>PRN</c>, <c>AUX</c>,
    /// <c>NUL</c>, <c>COM1</c> to <c>COM9</c>, <c>LPT1</c> to <c>LPT9</c>, with or without an extension). It rules on
    /// spelling, never on what the path resolves to on this machine, so a Unix host refuses what Windows would
    /// misread: <see cref="System.IO.Path.IsPathRooted(string)"/> alone admits a drive or UNC path there, and <c>:</c> admits an
    /// NTFS alternate stream.</summary>
    /// <param name="path">The candidate path.</param>
    /// <param name="allowDirectories"><see langword="true"/> when the path may have several segments;
    /// <see langword="false"/> when it must be a single name.</param>
    /// <param name="allowParentSegments"><see langword="true"/> when a <c>..</c> segment is admitted, as a document's
    /// reference to a file beside a parent directory is; <see langword="false"/> when the path must stay beneath the
    /// directory it lands under, as a name that lands on a client's disk must.</param>
    /// <returns><see langword="true"/> when the path has the portable relative spelling.</returns>
    public static bool IsRelative(string? path, bool allowDirectories = true, bool allowParentSegments = false) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            return false;
        }

        foreach (var character in path) {
            if ((character < ' ') || (character is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')) {
                return false;
            }
        }

        var segments = path.Split(separator: '/');

        if (!allowDirectories && (segments.Length != 1)) {
            return false;
        }

        foreach (var segment in segments) {
            if (allowParentSegments && (segment == "..")) {
                continue;
            }

            if ((segment.Length == 0) || segment.StartsWith(value: ' ') || segment.EndsWith(value: '.') || segment.EndsWith(value: ' ')) {
                return false;
            }

            if (IsDeviceName(segment: segment)) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Returns the name before its first dot, so <c>paddleball.world.puck</c> has the stem <c>paddleball</c>; a
    /// name with no dot is its own stem, and one that begins with a dot has the empty stem. Every compound extension is
    /// dropped, which <see cref="System.IO.Path.GetFileNameWithoutExtension(string)"/> (the last extension only) does not.</summary>
    /// <param name="name">A file or directory name, with no directory part.</param>
    /// <returns>The part of <paramref name="name"/> before its first dot, or all of it when it has none.</returns>
    public static string StemOf(string name) {
        var dot = name.IndexOf(value: '.');

        return ((dot < 0)
            ? name
            : name[..dot]
        );
    }
    /// <summary>Tests whether a path segment names a Windows device, which Windows resolves to the device whatever
    /// directory it sits in and whatever extension follows it: <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>,
    /// <c>COM1</c> to <c>COM9</c> and <c>LPT1</c> to <c>LPT9</c> (the superscript digits too), in any case.</summary>
    /// <param name="segment">One path segment, with or without an extension.</param>
    /// <returns><see langword="true"/> when the part of the segment before its first dot names a device.</returns>
    public static bool IsDeviceName(string segment) {
        var stem = StemOf(name: segment);

        return (stem.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "CON") ||
            stem.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "PRN") ||
            stem.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "AUX") ||
            stem.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "NUL") ||
            ((stem.Length == 4) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³') &&
            (stem.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "COM") ||
            stem.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "LPT"))));
    }
}
