using System.Text.RegularExpressions;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The path-free narration convention as one assertion: a line a player reads names a file by its name under
/// the catalog, or by its world id, and never by a machine-local rooted path.</summary>
internal static partial class NarrationPaths {
    // A drive-letter or UNC root, or a POSIX root standing at the start of a word. Relative names such as
    // 'unloadable/' or 'x.world.json' never match.
    [GeneratedRegex(pattern: @"([A-Za-z]:[\\/])|(\\\\)|((^|[\s'""(\[])/[^\s/])")]
    private static partial Regex Rooted();

    /// <summary>Asserts that <paramref name="text"/> carries no rooted path and does not contain
    /// <paramref name="root"/>.</summary>
    /// <param name="text">The narration or refusal text.</param>
    /// <param name="root">The absolute directory the text must not name.</param>
    public static void AssertNone(string text, string root) {
        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: root);
        Assert.False(condition: Rooted().IsMatch(input: text), userMessage: $"narration carries a rooted path: {text}");
    }
}
