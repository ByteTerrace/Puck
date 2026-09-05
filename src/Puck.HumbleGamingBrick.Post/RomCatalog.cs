namespace Puck.HumbleGamingBrick.Post;

/// <summary>Discovers reference-ROM test cases under the corpus root (resolved by <c>Program</c> from <c>--roms</c> or
/// the <c>PUCK_GB_TESTROMS</c> environment variable, with a known development-machine fallback). ROMs are never
/// committed to the repository, so an absent corpus yields no cases and the Tier-B stages skip rather than fail.</summary>
internal static class RomCatalog {
    // Generous ceilings; a passing ROM reports (over serial or at $A000) and exits well before these, so they bound only
    // a ROM that never reports — a genuine failure to detect — not the common path.
    private const int ConformanceFrameCap = 5_000;
    private const int AcceptanceFrameCap = 600;
    // A family tag that names no revision runs on that family's target revision — the stepping the accuracy work holds
    // to the whole suite. An untagged ROM runs on both targets.
    private const ConsoleModel CgbTarget = ConsoleModel.CgbE;
    private const ConsoleModel DmgTarget = ConsoleModel.DmgC;

    /// <summary>Enumerates the conformance ROMs directly inside a sub-path of the corpus's on-disk <c>blargg</c> directory.</summary>
    /// <param name="root">The resolved corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <param name="group">The group name to tag the cases with.</param>
    /// <param name="subPath">The path under <c>blargg/</c> to enumerate (forward slashes; a folder yields every ROM directly inside it).</param>
    /// <param name="model">The console model the ROMs run on.</param>
    /// <returns>The classified cases, ordered by path; empty when the corpus or directory is absent.</returns>
    public static IReadOnlyList<RomCase> ConformanceRoms(string? root, string group, string subPath, ConsoleModel model) {
        if (root is null) {
            return [];
        }

        var directory = Path.Combine(
            path1: Path.Combine(
                path1: root,
                path2: "blargg"
            ),
            path2: subPath.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        );

        if (!Directory.Exists(path: directory)) {
            return [];
        }

        return Directory
            .EnumerateFiles(
            path: directory,
            searchOption: SearchOption.TopDirectoryOnly,
            searchPattern: "*.gb"
        )
            .OrderBy(
            keySelector: static path => path,
            comparer: StringComparer.OrdinalIgnoreCase
        )
            .Select(selector: path => new RomCase(
            Group: group,
            Name: Path.GetFileNameWithoutExtension(path: path),
            FullPath: path,
            Model: model,
            FrameCap: ConformanceFrameCap
        ))
            .ToArray();
    }
    /// <summary>Enumerates the acceptance ROMs under a relative directory, one case per (ROM, eligible revision). The
    /// eligibility comes from the file-name tag: an untagged ROM runs on both family targets, a tag naming a family
    /// runs on that family's target, and a tag naming explicit revisions runs on each of those we model.</summary>
    /// <param name="root">The resolved corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <param name="group">The group name to tag the cases with.</param>
    /// <param name="relativeDirectory">The path under the corpus's on-disk <c>mooneye-test-suite/acceptance/</c> (empty for the root).</param>
    /// <param name="recurse">Whether to descend into sub-directories (false yields only the directory's own ROMs).</param>
    /// <returns>The classified cases, ordered by path; empty when the corpus or directory is absent.</returns>
    public static IReadOnlyList<RomCase> AcceptanceRoms(string? root, string group, string relativeDirectory, bool recurse) {
        if (root is null) {
            return [];
        }

        var acceptance = Path.Combine(
            path1: Path.Combine(
                path1: root,
                path2: "mooneye-test-suite"
            ),
            path2: "acceptance"
        );
        var directory = ((relativeDirectory.Length == 0)
            ? acceptance
            : Path.Combine(
            path1: acceptance,
            path2: relativeDirectory
        ));

        if (!Directory.Exists(path: directory)) {
            return [];
        }

        var option = (recurse
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly);
        var cases = new List<RomCase>();

        foreach (var file in Directory.EnumerateFiles(
            path: directory,
            searchOption: option,
            searchPattern: "*.gb"
        ).OrderBy(
            keySelector: static path => path,
            comparer: StringComparer.OrdinalIgnoreCase
        )) {
            var name = Path.GetFileNameWithoutExtension(path: file);

            foreach (var model in ParseEligibleModels(tag: ModelTag(fileName: name))) {
                cases.Add(item: new RomCase(
                    FrameCap: AcceptanceFrameCap,
                    FullPath: file,
                    Group: group,
                    Model: model,
                    Name: name
                ));
            }
        }

        return cases;
    }

    // The model tag is the substring after the final '-' in the file name (the acceptance-corpus file-name convention).
    private static string ModelTag(string fileName) {
        var dash = fileName.LastIndexOf(value: '-');

        return ((dash >= 0)
            ? fileName[(dash + 1)..]
            : string.Empty);
    }
    // Maps an acceptance-corpus model tag to the revisions the case is expected to pass on. The corpus names hardware
    // three ways:
    //
    //   no tag         the suite's baseline, i.e. both family targets
    //   group letters  G = dmg+mgb, S = sgb+sgb2, C = cgb+agb+ags, A = agb+ags, concatenated (GS, C)
    //   model names    dmg / mgb / sgb / sgb2 / cgb / agb / ags, each optionally followed by its revision run
    //                  (dmg0, dmgABC, cgbABCDE), and several may be concatenated (dmgABCmgb)
    //
    // A model name with no revision run names every revision of that family, which resolves to the family target; a
    // revision run names exactly the revisions it lists.
    private static IReadOnlyList<ConsoleModel> ParseEligibleModels(string tag) {
        if (tag.Length == 0) {
            return [DmgTarget, CgbTarget];
        }

        var models = new List<ConsoleModel>(capacity: 4);

        if ((tag.Length <= 4) && tag.All(predicate: static character => (character is 'G' or 'S' or 'C' or 'A'))) {
            AddGroups(
                models: models,
                tag: tag
            );

            return models;
        }

        AddNamedModels(
            models: models,
            tag: tag
        );

        return ((models.Count == 0)
            ? [DmgTarget, CgbTarget]
            : models);
    }
    private static void AddGroups(string tag, List<ConsoleModel> models) {
        if (tag.Contains(value: 'G')) {
            Add(
                model: DmgTarget,
                models: models
            );
            Add(
                model: ConsoleModel.Mgb,
                models: models
            );
        }

        if (tag.Contains(value: 'S')) {
            Add(
                model: ConsoleModel.Sgb,
                models: models
            );
            Add(
                model: ConsoleModel.Sgb2,
                models: models
            );
        }

        if (tag.Contains(value: 'C')) {
            Add(
                model: CgbTarget,
                models: models
            );
        }

        if (
            tag.Contains(value: 'A') ||
            tag.Contains(value: 'C')
        ) {
            Add(
                model: ConsoleModel.Agb,
                models: models
            );
            Add(
                model: ConsoleModel.Ags,
                models: models
            );
        }
    }
    // The family names a tag may concatenate, longest first so "sgb2" is never read as "sgb" with a stray revision
    // digit. Each carries the target revision a bare name resolves to and the revisions its trailing run can name.
    private static readonly (string Name, ConsoleModel Target, ConsoleModel[] Revisions)[] FamilyNames = [
        ("sgb2", ConsoleModel.Sgb2, []),
        ("dmg", DmgTarget, [ConsoleModel.Dmg0, ConsoleModel.DmgB, ConsoleModel.DmgC]),
        ("mgb", ConsoleModel.Mgb, []),
        ("sgb", ConsoleModel.Sgb, []),
        ("cgb", CgbTarget, [ConsoleModel.Cgb0, ConsoleModel.CgbA, ConsoleModel.CgbB, ConsoleModel.CgbC, ConsoleModel.CgbD, ConsoleModel.CgbE]),
        ("agb", ConsoleModel.Agb, []),
        ("ags", ConsoleModel.Ags, []),
    ];

    // Scans the tag left to right as a run of family names, each optionally followed by its revision letters. A
    // revision run ends where the next family name begins, so "dmgABCmgb" reads as dmg{A,B,C} then mgb.
    private static void AddNamedModels(string tag, List<ConsoleModel> models) {
        var cursor = 0;

        while (cursor < tag.Length) {
            var family = MatchFamily(
                cursor: cursor,
                tag: tag
            );

            if (family is null) {
                ++cursor;

                continue;
            }

            var (name, target, revisions) = family.Value;

            cursor += name.Length;

            var matched = false;

            while (
                (cursor < tag.Length) &&
                (MatchFamily(
                cursor: cursor,
                tag: tag
            ) is null)
            ) {
                var stepping = SteppingOf(character: tag[cursor]);

                if (stepping < 0) {
                    break;
                }

                matched = true;
                ++cursor;

                foreach (var revision in revisions) {
                    if (revision.Stepping() == stepping) {
                        Add(
                            model: revision,
                            models: models
                        );
                    }
                }
            }

            if (!matched) {
                Add(
                    model: target,
                    models: models
                );
            }
        }
    }
    private static (string Name, ConsoleModel Target, ConsoleModel[] Revisions)? MatchFamily(string tag, int cursor) {
        foreach (var candidate in FamilyNames) {
            if (string.Compare(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                indexA: cursor,
                indexB: 0,
                length: candidate.Name.Length,
                strA: tag,
                strB: candidate.Name
            ) == 0) {
                return candidate;
            }
        }

        return null;
    }
    // A revision letter's stepping number: '0'-'9' literally, 'A'-'Z' as its alphabet position. Anything else is not a
    // revision character.
    private static int SteppingOf(char character) =>
        char.ToUpperInvariant(c: character) switch {
            (>= '0' and <= '9') => (character - '0'),
            (>= 'A' and <= 'Z') => ((char.ToUpperInvariant(c: character) - 'A') + 1),
            _ => -1,
        };
    private static void Add(List<ConsoleModel> models, ConsoleModel model) {
        if (!models.Contains(item: model)) {
            models.Add(item: model);
        }
    }
    /// <summary>Enumerates the ROMs directly under an arbitrary directory using the acceptance suite's own "-tag"
    /// model-eligibility convention (<see cref="ModelTag"/> and <see cref="ParseEligibleModels"/>) — the same rule
    /// <see cref="AcceptanceRoms"/> applies under <c>mooneye-test-suite/acceptance</c>, generalized to the mooneye
    /// family's other directories (<c>misc/</c>, <c>emulator-only/</c>) and to the wilbertpol fork's mirrors of them.</summary>
    /// <param name="root">The resolved corpus root, or <see langword="null"/> when no corpus is available.</param>
    /// <param name="group">The group name to tag the cases with.</param>
    /// <param name="suiteRelativeDirectory">The path under <paramref name="root"/> to enumerate (forward slashes).</param>
    /// <param name="recurse">Whether to descend into sub-directories.</param>
    /// <param name="frameCap">The frame ceiling before a case is declared inconclusive.</param>
    /// <returns>The classified cases, ordered by path; empty when the corpus or directory is absent.</returns>
    public static IReadOnlyList<RomCase> TaggedRoms(string? root, string group, string suiteRelativeDirectory, bool recurse, int frameCap) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDirectory(
            relative: suiteRelativeDirectory,
            root: root
        );

        if (!Directory.Exists(path: directory)) {
            return [];
        }

        var cases = new List<RomCase>();

        foreach (var file in EnumerateGbFiles(
            directory: directory,
            recurse: recurse
        )) {
            var name = Path.GetFileNameWithoutExtension(path: file);

            foreach (var model in ParseEligibleModels(tag: ModelTag(fileName: name))) {
                cases.Add(item: new RomCase(
                    FrameCap: frameCap,
                    FullPath: file,
                    Group: group,
                    Model: model,
                    Name: name
                ));
            }
        }

        return cases;
    }
    private static IReadOnlyList<string> EnumerateGbFiles(string directory, bool recurse) =>
        Directory
            .EnumerateFiles(
            path: directory,
            searchOption: (recurse
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly),
            searchPattern: "*.gb"
        )
            .OrderBy(
            keySelector: static path => path,
            comparer: StringComparer.OrdinalIgnoreCase
        )
            .ToArray();
    private static string SuiteDirectory(string root, string relative) =>
        Path.Combine(
        path1: root,
        path2: relative.Replace(
            newChar: Path.DirectorySeparatorChar,
            oldChar: '/'
        )
    );
}
