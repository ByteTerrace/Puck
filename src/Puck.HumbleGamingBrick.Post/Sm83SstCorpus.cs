namespace Puck.HumbleGamingBrick.Post;

/// <summary>Discovers SingleStepTests/sm83 opcode-family files under the corpus root (resolved by <c>Program</c> from
/// the <c>PUCK_GB_SST</c> environment variable, with a known development-machine fallback). The corpus is never
/// committed to the repository, so an absent root yields no families and the stage skips rather than fails.</summary>
internal static class Sm83SstCorpus {
    /// <summary>Enumerates the opcode-family JSON files under the corpus's <c>v1</c> directory (500 files: the 256
    /// unprefixed opcodes minus the 11 illegal encodings and the bare <c>0xCB</c> prefix byte, plus the 256
    /// CB-prefixed opcodes named <c>"cb xx.json"</c>).</summary>
    /// <param name="root">The resolved corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <returns>The family file paths, ordered by name; empty when the corpus is absent.</returns>
    public static IReadOnlyList<string> Families(string? root) {
        if (root is null) {
            return [];
        }

        var directory = Path.Combine(path1: root, path2: "v1");

        if (!Directory.Exists(path: directory)) {
            return [];
        }

        return Directory
            .EnumerateFiles(path: directory, searchPattern: "*.json", searchOption: SearchOption.TopDirectoryOnly)
            .OrderBy(keySelector: static path => path, comparer: StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
