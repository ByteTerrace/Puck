namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Discovers reference-ROM test cases under a corpus root (resolved by <c>Program</c> from a command-line flag or
/// the manifest's cached archive). ROMs are never committed to the repository, so an absent corpus (or an absent file
/// within it) yields no cases and the owning Tier-B stage skips rather than fails.</summary>
internal static class RomCatalog {
    /// <summary>Resolves a fixed set of relative ROM paths under the corpus root into cases, keeping only the ones that
    /// exist on disk.</summary>
    /// <param name="root">The resolved corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <param name="group">The group name to tag the cases with.</param>
    /// <param name="cases">The (relative-path, display-name) pairs to resolve (forward slashes in the relative path).</param>
    /// <returns>A case per existing file, in the given order; empty when the corpus or every file is absent.</returns>
    public static IReadOnlyList<RomCase> Resolve(string? root, string group, IReadOnlyList<(string RelativePath, string Name)> cases) {
        if (root is null) {
            return [];
        }

        var resolved = new List<RomCase>(capacity: cases.Count);

        foreach (var (relativePath, name) in cases) {
            var fullPath = Path.Combine(
                path1: root,
                path2: relativePath
            );

            if (File.Exists(path: fullPath)) {
                resolved.Add(item: new RomCase(
                    FullPath: fullPath,
                    Group: group,
                    Name: name
                ));
            }
        }

        return resolved;
    }
    /// <summary>Enumerates the ARM/Thumb fuzz-corpus ROMs under the fuzz-corpus root.</summary>
    /// <param name="root">The resolved fuzz-corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <returns>A case per existing fuzz-corpus ROM; empty when the root or every ROM is absent.</returns>
    public static IReadOnlyList<RomCase> ArmFuzz(string? root) {
        (string RelativePath, string Name)[] known = [
            ("ARM_Any.gba", "ARM_Any"),
            ("THUMB_Any.gba", "THUMB_Any"),
            ("ARM_DataProcessing.gba", "ARM_DataProcessing"),
            ("THUMB_DataProcessing.gba", "THUMB_DataProcessing"),
            ("FuzzARM.gba", "FuzzARM"),
        ];

        return Resolve(
            cases: known,
            group: "arm-fuzz",
            root: root
        );
    }
}
