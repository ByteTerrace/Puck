using System.Text.RegularExpressions;

namespace Puck.World.Server;

/// <summary>Keeps a machine-local path out of a line a player reads: a path under the owned-world catalog is named
/// relative to the catalog's root, and any other rooted path is reduced to its file name. A loader's refusal names the
/// files it composed (the document, its basis chain, a probe beside it), and each is a path the player's state directory
/// would otherwise reach the console through.</summary>
internal static partial class WorldNarratedPaths {
    // A drive-letter path, a UNC path, or a POSIX path of two or more segments standing at the start of a word.
    [GeneratedRegex(pattern: @"([A-Za-z]:[\\/][^\s'""()<>|]*)|(\\\\[^\s'""()<>|]+)|((?<=^|[\s'""(])/(?:[^\s'""/()<>|]+/)+[^\s'""/()<>|]*)")]
    private static partial Regex Rooted();

    /// <summary>Returns <paramref name="text"/> with every path made relative to <paramref name="root"/>, and every other
    /// rooted path reduced to its file name.</summary>
    /// <param name="text">The refusal or narration text.</param>
    /// <param name="root">The catalog's root directory.</param>
    /// <returns>The path-free text.</returns>
    public static string Relative(string text, string root) {
        var trimmed = root.TrimEnd(
            '/',
            '\\'
        );

        foreach (var separator in new[] { '\\', '/' }) {
            text = text.Replace(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                newValue: string.Empty,
                oldValue: $"{trimmed.Replace(newChar: separator, oldChar: '/').Replace(newChar: separator, oldChar: '\\')}{separator}"
            );
        }

        return Rooted().Replace(
            evaluator: static match => Path.GetFileName(path: match.Value.Replace(newChar: '/', oldChar: '\\')),
            input: text
        );
    }
}
