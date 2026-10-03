using Xunit;

namespace Puck.World.Tests;

/// <summary>The path-free narration convention as one assertion: a line a player reads names a document by what its
/// author wrote or by its file name, and never by a path the host resolved. The paths the test's environment created are
/// what the line must not carry, each in both separator spellings, rather than a guess at what a path looks like.</summary>
internal static class NarrationPaths {
    /// <summary>Asserts that <paramref name="text"/> names neither <paramref name="root"/> nor any of
    /// <paramref name="hostPaths"/>.</summary>
    /// <param name="text">The narration or refusal text.</param>
    /// <param name="root">The absolute directory the test created, which the text must not name.</param>
    /// <param name="hostPaths">Any other absolute path the test's environment made the host resolve.</param>
    public static void AssertNone(string text, string root, params string[] hostPaths) {
        foreach (var path in hostPaths.Prepend(element: root)) {
            var trimmed = path.TrimEnd(
                '/',
                '\\'
            );

            foreach (var separator in new[] { '/', '\\' }) {
                Assert.DoesNotContain(
                    actualString: text,
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    expectedSubstring: trimmed.Replace(newChar: separator, oldChar: '\\').Replace(newChar: separator, oldChar: '/')
                );
            }
        }
    }
}
