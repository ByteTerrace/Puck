namespace Puck.HumbleGamingBrick.Post;

/// <summary>Discovers reference-ROM test cases under the corpus root (resolved by <c>Program</c> from <c>--roms</c> or
/// the <c>PUCK_GB_TESTROMS</c> environment variable, with a known development-machine fallback). ROMs are never
/// committed to the repository, so an absent corpus yields no cases and the Tier-B stages skip rather than fail.</summary>
internal static class RomCatalog {
    // Generous ceilings; a passing ROM reports (over serial or at $A000) and exits well before these, so they bound only
    // a ROM that never reports — a genuine failure to detect — not the common path.
    private const int BlarggFrameCap = 5_000;
    private const int MooneyeFrameCap = 600;

    // We target exactly two SoC revisions: the latest original Game Boy (DMG-CPU C) and the latest Game Boy Color
    // (CPU CGB E). A mooneye/age tag naming any other revision is skipped rather than counted as a failure.
    private const char DmgTargetRevision = 'C';
    private const char CgbTargetRevision = 'E';

    /// <summary>Enumerates the blargg ROMs directly inside a sub-path of the corpus's <c>blargg</c> directory.</summary>
    /// <param name="root">The resolved corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <param name="group">The group name to tag the cases with.</param>
    /// <param name="subPath">The path under <c>blargg/</c> to enumerate (forward slashes; a folder yields every ROM directly inside it).</param>
    /// <param name="model">The console model the ROMs run on.</param>
    /// <returns>The classified cases, ordered by path; empty when the corpus or directory is absent.</returns>
    public static IReadOnlyList<RomCase> Blargg(string? root, string group, string subPath, ConsoleModel model) {
        if (root is null) {
            return [];
        }

        var directory = Path.Combine(path1: Path.Combine(path1: root, path2: "blargg"), path2: subPath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));

        if (!Directory.Exists(path: directory)) {
            return [];
        }

        return Directory
            .EnumerateFiles(path: directory, searchPattern: "*.gb", searchOption: SearchOption.TopDirectoryOnly)
            .OrderBy(keySelector: static path => path, comparer: StringComparer.OrdinalIgnoreCase)
            .Select(selector: path => new RomCase(
                Group: group,
                Name: Path.GetFileNameWithoutExtension(path: path),
                FullPath: path,
                Model: model,
                FrameCap: BlarggFrameCap
            ))
            .ToArray();
    }

    /// <summary>Enumerates the mooneye acceptance ROMs under a relative directory, one case per (ROM, eligible model).
    /// The model eligibility comes from the file-name tag, narrowed to the two target revisions; off-target revisions
    /// (dmg0, mgb, cgb0, agb, ags, sgb) yield nothing and are skipped rather than failed.</summary>
    /// <param name="root">The resolved corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <param name="group">The group name to tag the cases with.</param>
    /// <param name="relativeDirectory">The path under <c>mooneye-test-suite/acceptance/</c> (empty for the root).</param>
    /// <param name="recurse">Whether to descend into sub-directories (false yields only the directory's own ROMs).</param>
    /// <returns>The classified cases, ordered by path; empty when the corpus or directory is absent.</returns>
    public static IReadOnlyList<RomCase> Mooneye(string? root, string group, string relativeDirectory, bool recurse) {
        if (root is null) {
            return [];
        }

        var acceptance = Path.Combine(path1: Path.Combine(path1: root, path2: "mooneye-test-suite"), path2: "acceptance");
        var directory = (relativeDirectory.Length == 0) ? acceptance : Path.Combine(path1: acceptance, path2: relativeDirectory);

        if (!Directory.Exists(path: directory)) {
            return [];
        }

        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var cases = new List<RomCase>();

        foreach (var file in Directory.EnumerateFiles(path: directory, searchPattern: "*.gb", searchOption: option).OrderBy(keySelector: static path => path, comparer: StringComparer.OrdinalIgnoreCase)) {
            var name = Path.GetFileNameWithoutExtension(path: file);

            foreach (var model in ParseEligibleModels(tag: ModelTag(fileName: name))) {
                cases.Add(item: new RomCase(Group: group, Name: name, FullPath: file, Model: model, FrameCap: MooneyeFrameCap));
            }
        }

        return cases;
    }

    // The model tag is the substring after the final '-' in the file name (the mooneye/age convention).
    private static string ModelTag(string fileName) {
        var dash = fileName.LastIndexOf(value: '-');

        return (dash >= 0) ? fileName[(dash + 1)..] : string.Empty;
    }

    // Maps a mooneye/age model tag to the emulated models it is expected to pass on, narrowed to the two target
    // revisions. An untagged test runs on both models; a group tag (letters G/S/C/A) expands to its families; a named
    // tag (dmg/cgb + revision letters) matches only when our target revision is in range; off-target tags yield nothing.
    private static IReadOnlyList<ConsoleModel> ParseEligibleModels(string tag) {
        var isGroupTag = (tag.Length > 0) && (tag.Length <= 4) && tag.All(predicate: static c => c is 'G' or 'S' or 'C' or 'A');
        var hasModelName = ContainsAny(value: tag, "dmg", "mgb", "sgb", "cgb", "agb", "ags");

        if (!isGroupTag && !hasModelName) {
            return [ConsoleModel.Dmg, ConsoleModel.Cgb];
        }

        bool dmg;
        bool cgb;

        if (isGroupTag) {
            // G = dmg+mgb (includes DMG-CPU C); C = cgb+agb+ags (includes CPU CGB E).
            dmg = tag.Contains(value: 'G');
            cgb = tag.Contains(value: 'C');
        } else {
            // mgb-only and agb/ags-only tags name hardware we do not target, so they match nothing here.
            dmg = RevisionMatches(tag: tag, prefix: "dmg", revision: DmgTargetRevision);
            cgb = RevisionMatches(tag: tag, prefix: "cgb", revision: CgbTargetRevision);
        }

        var models = new List<ConsoleModel>(capacity: 2);

        if (dmg) {
            models.Add(item: ConsoleModel.Dmg);
        }

        if (cgb) {
            models.Add(item: ConsoleModel.Cgb);
        }

        return models;
    }

    // True when the tag names a model family and our target revision is in scope: either no revision letters follow the
    // prefix (the test applies to every revision of that family) or the explicit revision run contains the target.
    private static bool RevisionMatches(string tag, string prefix, char revision) {
        var index = tag.IndexOf(value: prefix, comparisonType: StringComparison.OrdinalIgnoreCase);

        if (index < 0) {
            return false;
        }

        var sawLetters = false;
        var matched = false;

        for (var cursor = (index + prefix.Length); (cursor < tag.Length); ++cursor) {
            var character = char.ToUpperInvariant(c: tag[cursor]);

            if (character is (>= 'A' and <= 'E') or (>= '0' and <= '9')) {
                sawLetters = true;
                matched |= (character == revision);
            } else {
                break;
            }
        }

        return (!sawLetters || matched);
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(predicate: needle => value.Contains(value: needle, comparisonType: StringComparison.OrdinalIgnoreCase));
}
