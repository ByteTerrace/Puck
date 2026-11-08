namespace Puck.Analyzers;

// Compiler source paths can be repository-relative, absolute, or mapped by CI. Match only a complete path suffix,
// under an exact assembly identity; this is an allowlist identity, not host filesystem containment.
internal static class AnalyzerAllowlistPath {
    public static bool Matches(string assembly, string path, string expectedAssembly, string expectedPath) {
        var normalized = path.Replace(newChar: '/', oldChar: '\\');

        return (string.Equals(a: assembly, b: expectedAssembly, comparisonType: StringComparison.Ordinal) &&
            (string.Equals(a: normalized, b: expectedPath, comparisonType: StringComparison.Ordinal) ||
             normalized.EndsWith(comparisonType: StringComparison.Ordinal, value: ("/" + expectedPath))));
    }
}
