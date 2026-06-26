namespace Puck.HumbleGamingBrick.Conformance;

/// <summary>Discovers and classifies the GB/GBC test ROMs under the corpus root. The root comes from the
/// <c>PUCK_GB_TESTROMS</c> environment variable, falling back to the known bundle location on this machine; when
/// neither exists the catalog is empty and every test skips. ROMs are never committed to the repository.</summary>
public static class RomCatalog {
    /// <summary>The Game Boy master clock cycles in one LCD frame (the unit of <c>SystemBus.ElapsedDots</c>).</summary>
    public const long CyclesPerFrame = 70224L;

    private const long MooneyeCycleCap = 60L * CyclesPerFrame * 8L; // ~3.8M; mooneye tests settle well within.
    private const long BlarggCycleCap = 300_000_000L;               // generous: the longest cpu_instrs sub-tests.
    private const long ScreenshotCycleCap = 60L * CyclesPerFrame;   // mealybug/acid2 reach LD B,B within a frame or two.

    private static readonly string[] RomExtensions = ["*.gb"];

    // Materialized once per process: the corpus does not change during a run, and per-test Find() would otherwise
    // re-walk the filesystem for every case. Lazy is thread-safe for the parallel xUnit theory runner.
    private static readonly Lazy<IReadOnlyList<RomCase>> AllCases = new(valueFactory: BuildAll);

    /// <summary>Gets the corpus root directory, or <see langword="null"/> when no corpus is available.</summary>
    public static string? Root { get; } = ResolveRoot();

    /// <summary>Gets whether a test corpus is available to run against.</summary>
    public static bool IsAvailable =>
        (Root is not null);

    private static string? ResolveRoot() {
        var fromEnvironment = Environment.GetEnvironmentVariable(variable: "PUCK_GB_TESTROMS");

        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(path: fromEnvironment)) {
            return fromEnvironment;
        }

        const string fallback = @"D:\Source\ByteTerrace\Temp\GBC Test Suites";

        return Directory.Exists(path: fallback) ? fallback : null;
    }

    /// <summary>Enumerates every classified ROM run in the v1 focused cycle-accuracy set, one entry per
    /// (ROM, eligible model). Returns nothing when no corpus is available. The result is materialized once.</summary>
    /// <returns>The classified test cases.</returns>
    public static IEnumerable<RomCase> Enumerate() =>
        AllCases.Value;

    private static IReadOnlyList<RomCase> BuildAll() {
        var cases = new List<RomCase>();

        if (Root is null) {
            return cases;
        }

        cases.AddRange(collection: EnumerateMooneye());
        cases.AddRange(collection: EnumerateSameSuite());
        cases.AddRange(collection: EnumerateBlargg());
        cases.AddRange(collection: EnumerateGbMicrotest());
        cases.AddRange(collection: EnumerateMealybug());
        cases.AddRange(collection: EnumerateAcid2());
        cases.AddRange(collection: EnumerateScribbl());

        return cases;
    }

    /// <summary>Enumerates the cases for a single suite by name (used by the per-suite xUnit theories).</summary>
    /// <param name="suite">The suite name: "mooneye", "same-suite", "blargg", or "gbmicrotest".</param>
    /// <returns>The classified test cases for that suite.</returns>
    public static IEnumerable<RomCase> EnumerateSuite(string suite) =>
        Enumerate().Where(predicate: romCase => string.Equals(a: romCase.Suite, b: suite, comparisonType: StringComparison.OrdinalIgnoreCase));

    /// <summary>Reconstructs a single case from its suite and relative path (used to rehydrate an xUnit theory row).</summary>
    /// <param name="suite">The suite name.</param>
    /// <param name="relativePath">The ROM path relative to the corpus root.</param>
    /// <param name="model">The model the row was generated for.</param>
    /// <returns>The matching case, or <see langword="null"/> when not found.</returns>
    public static RomCase? Find(string suite, string relativePath, ConsoleModel model) =>
        EnumerateSuite(suite: suite).FirstOrDefault(predicate: romCase =>
            string.Equals(a: romCase.RelativePath, b: relativePath, comparisonType: StringComparison.OrdinalIgnoreCase)
            && (romCase.Model == model));

    private static IEnumerable<RomCase> EnumerateMooneye() {
        var suiteRoot = Path.Combine(Root!, "mooneye-test-suite");
        string[] directories = ["acceptance", "misc", "emulator-only"];

        foreach (var directory in directories) {
            var directoryPath = Path.Combine(suiteRoot, directory);

            if (!Directory.Exists(path: directoryPath)) {
                continue;
            }

            foreach (var file in EnumerateRoms(directory: directoryPath)) {
                var fileName = Path.GetFileNameWithoutExtension(path: file);
                var (tier, subsystem) = ClassifyMooneye(relativePath: RelativePath(file), directory: directory);

                foreach (var model in ParseEligibleModels(tag: ModelTag(fileName: fileName))) {
                    yield return MakeCase(
                        suite: "mooneye",
                        file: file,
                        model: model,
                        protocol: ResultProtocol.Mooneye,
                        tier: tier,
                        subsystem: subsystem,
                        cycleLimit: MooneyeCycleCap
                    );
                }
            }
        }
    }

    private static IEnumerable<RomCase> EnumerateSameSuite() {
        var suiteRoot = Path.Combine(Root!, "same-suite");

        if (!Directory.Exists(path: suiteRoot)) {
            yield break;
        }

        foreach (var file in EnumerateRoms(directory: suiteRoot, recurse: true)) {
            var relative = RelativePath(file);

            // SameSuite's SGB tests target hardware we do not emulate.
            if (relative.Contains("sgb", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var subsystem = relative switch {
                _ when relative.Contains("apu", StringComparison.OrdinalIgnoreCase) => TestSubsystem.Apu,
                _ when relative.Contains("dma", StringComparison.OrdinalIgnoreCase) => TestSubsystem.OamDma,
                _ when relative.Contains("interrupt", StringComparison.OrdinalIgnoreCase) => TestSubsystem.Interrupts,
                _ when relative.Contains("ppu", StringComparison.OrdinalIgnoreCase) => TestSubsystem.PpuTiming,
                _ => TestSubsystem.Other,
            };

            // SameSuite is developed against the CGB; default there unless the file name names a DMG model.
            var fileName = Path.GetFileNameWithoutExtension(path: file);

            foreach (var model in ParseEligibleModels(tag: ModelTag(fileName: fileName), fallback: ConsoleModel.Cgb)) {
                yield return MakeCase(
                    suite: "same-suite",
                    file: file,
                    model: model,
                    protocol: ResultProtocol.Mooneye,
                    tier: TestTier.Timing,
                    subsystem: subsystem,
                    cycleLimit: MooneyeCycleCap
                );
            }
        }
    }

    private static IEnumerable<RomCase> EnumerateBlargg() {
        // (sub-path, model, tier, subsystem). A folder yields every ROM directly inside it.
        (string Path, ConsoleModel Model, TestTier Tier, TestSubsystem Subsystem)[] specs = [
            ("cpu_instrs/individual", ConsoleModel.Dmg, TestTier.FunctionalBaseline, TestSubsystem.Cpu),
            ("instr_timing", ConsoleModel.Dmg, TestTier.Timing, TestSubsystem.CpuTiming),
            ("mem_timing/individual", ConsoleModel.Dmg, TestTier.Timing, TestSubsystem.CpuTiming),
            ("mem_timing-2/rom_singles", ConsoleModel.Dmg, TestTier.Timing, TestSubsystem.CpuTiming),
            ("interrupt_time", ConsoleModel.Dmg, TestTier.Timing, TestSubsystem.Interrupts),
            ("oam_bug/rom_singles", ConsoleModel.Dmg, TestTier.Timing, TestSubsystem.PpuTiming),
            ("dmg_sound/rom_singles", ConsoleModel.Dmg, TestTier.Timing, TestSubsystem.Apu),
            ("cgb_sound/rom_singles", ConsoleModel.Cgb, TestTier.CgbSpecific, TestSubsystem.Apu),
        ];

        var blarggRoot = Path.Combine(Root!, "blargg");

        foreach (var spec in specs) {
            var directoryPath = Path.Combine(blarggRoot, spec.Path.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));

            if (!Directory.Exists(path: directoryPath)) {
                continue;
            }

            foreach (var file in EnumerateRoms(directory: directoryPath)) {
                yield return MakeCase(
                    suite: "blargg",
                    file: file,
                    model: spec.Model,
                    protocol: ResultProtocol.Blargg,
                    tier: spec.Tier,
                    subsystem: spec.Subsystem,
                    cycleLimit: BlarggCycleCap
                );
            }
        }

        // The standalone HALT-bug ROM sits at the blargg root.
        var haltBug = Path.Combine(blarggRoot, "halt_bug.gb");

        if (File.Exists(path: haltBug)) {
            yield return MakeCase(
                suite: "blargg",
                file: haltBug,
                model: ConsoleModel.Dmg,
                protocol: ResultProtocol.Blargg,
                tier: TestTier.Timing,
                subsystem: TestSubsystem.Cpu,
                cycleLimit: BlarggCycleCap
            );
        }
    }

    private static IEnumerable<RomCase> EnumerateGbMicrotest() {
        var suiteRoot = Path.Combine(Root!, "gbmicrotest");

        if (!Directory.Exists(path: suiteRoot)) {
            yield break;
        }

        foreach (var file in EnumerateRoms(directory: suiteRoot)) {
            var fileName = Path.GetFileName(path: file);

            // This one needs ~380 ms of emulated time; everything else completes within two frames.
            var frames = string.Equals(a: fileName, b: "is_if_set_during_ime0.gb", comparisonType: StringComparison.OrdinalIgnoreCase)
                ? 400
                : 2;

            yield return MakeCase(
                suite: "gbmicrotest",
                file: file,
                model: ConsoleModel.Dmg,
                protocol: ResultProtocol.GbMicrotest,
                tier: TestTier.Timing,
                subsystem: ClassifyGbMicrotest(fileName: fileName),
                cycleLimit: frames * CyclesPerFrame,
                frameLimit: frames
            );
        }
    }

    private static IEnumerable<RomCase> EnumerateMealybug() {
        var suiteRoot = Path.Combine(Root!, "mealybug-tearoom-tests");

        if (!Directory.Exists(path: suiteRoot)) {
            yield break;
        }

        foreach (var file in EnumerateRoms(directory: suiteRoot, recurse: true)) {
            var directory = Path.GetDirectoryName(path: file)!;
            var baseName = Path.GetFileNameWithoutExtension(path: file);
            var dmgReference = Path.Combine(directory, baseName + "_dmg_blob.png");

            if (File.Exists(path: dmgReference)) {
                yield return ScreenshotCase(suite: "mealybug", file: file, model: ConsoleModel.Dmg, referenceImagePath: dmgReference, tier: TestTier.Timing, subsystem: TestSubsystem.PpuTiming);
            }

            // CPU CGB E behaves like CGB D for these PPU tests; prefer the _cgb_d image, fall back to _cgb_c.
            var cgbReference = FirstExisting(Path.Combine(directory, baseName + "_cgb_d.png"), Path.Combine(directory, baseName + "_cgb_c.png"));

            if (cgbReference is not null) {
                yield return ScreenshotCase(suite: "mealybug", file: file, model: ConsoleModel.Cgb, referenceImagePath: cgbReference, tier: TestTier.Timing, subsystem: TestSubsystem.PpuTiming);
            }
        }
    }

    private static IEnumerable<RomCase> EnumerateAcid2() {
        (string Rom, string Reference, ConsoleModel Model, TestTier Tier)[] specs = [
            (Path.Combine("dmg-acid2", "dmg-acid2.gb"), Path.Combine("dmg-acid2", "dmg-acid2-dmg.png"), ConsoleModel.Dmg, TestTier.FunctionalBaseline),
            (Path.Combine("cgb-acid2", "cgb-acid2.gbc"), Path.Combine("cgb-acid2", "cgb-acid2.png"), ConsoleModel.Cgb, TestTier.FunctionalBaseline),
            (Path.Combine("cgb-acid-hell", "cgb-acid-hell.gbc"), Path.Combine("cgb-acid-hell", "cgb-acid-hell.png"), ConsoleModel.Cgb, TestTier.Timing),
        ];

        foreach (var spec in specs) {
            var rom = Path.Combine(Root!, spec.Rom);
            var reference = Path.Combine(Root!, spec.Reference);

            if (File.Exists(path: rom) && File.Exists(path: reference)) {
                yield return ScreenshotCase(suite: "acid2", file: rom, model: spec.Model, referenceImagePath: reference, tier: spec.Tier, subsystem: TestSubsystem.PpuTiming);
            }
        }
    }

    private static IEnumerable<RomCase> EnumerateScribbl() {
        var suiteRoot = Path.Combine(Root!, "scribbltests");

        if (!Directory.Exists(path: suiteRoot)) {
            yield break;
        }

        foreach (var file in EnumerateRoms(directory: suiteRoot, recurse: true)) {
            var directory = Path.GetDirectoryName(path: file)!;
            var baseName = Path.GetFileNameWithoutExtension(path: file);

            // statcount-auto needs ~270 frames to settle; the rest stabilize within ~10.
            var frames = (baseName.Contains("statcount", StringComparison.OrdinalIgnoreCase) && baseName.Contains("auto", StringComparison.OrdinalIgnoreCase)) ? 270 : 10;

            var dmgReference = FindScribblReference(directory: directory, baseName: baseName, model: "dmg");

            if (dmgReference is not null) {
                yield return ScreenshotCase(suite: "scribbltests", file: file, model: ConsoleModel.Dmg, referenceImagePath: dmgReference, tier: TestTier.Timing, subsystem: TestSubsystem.PpuTiming, frameLimit: frames);
            }

            var cgbReference = FindScribblReference(directory: directory, baseName: baseName, model: "cgb");

            if (cgbReference is not null) {
                yield return ScreenshotCase(suite: "scribbltests", file: file, model: ConsoleModel.Cgb, referenceImagePath: cgbReference, tier: TestTier.Timing, subsystem: TestSubsystem.PpuTiming, frameLimit: frames);
            }
        }
    }

    // scribbltests reference names vary: <base>-dmg.png / <base>-cgb.png / <base>-cgb-dmg.png, with '_' and '-'
    // used interchangeably. Match a PNG whose trailing tokens after the ROM base are model qualifiers only.
    private static string? FindScribblReference(string directory, string baseName, string model) {
        var wanted = baseName.Replace(oldChar: '_', newChar: '-');

        foreach (var png in Directory.EnumerateFiles(path: directory, searchPattern: "*.png")) {
            var name = Path.GetFileNameWithoutExtension(path: png).Replace(oldChar: '_', newChar: '-');

            if (!name.StartsWith(value: wanted + "-", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var tokens = name[(wanted.Length + 1)..].Split(separator: '-');
            var modelsOnly = tokens.All(predicate: static t => t.Equals("dmg", StringComparison.OrdinalIgnoreCase) || t.Equals("cgb", StringComparison.OrdinalIgnoreCase));
            var namesModel = tokens.Any(predicate: t => t.Equals(model, StringComparison.OrdinalIgnoreCase));

            if (modelsOnly && namesModel) {
                return png;
            }
        }

        return null;
    }

    private static string? FirstExisting(params string[] paths) =>
        paths.FirstOrDefault(predicate: File.Exists);

    private static RomCase ScreenshotCase(string suite, string file, ConsoleModel model, string referenceImagePath, TestTier tier, TestSubsystem subsystem, int frameLimit = 0) =>
        new(
            Suite: suite,
            RelativePath: RelativePath(fullPath: file),
            FullPath: file,
            Model: model,
            Protocol: ResultProtocol.Screenshot,
            Tier: tier,
            Subsystem: subsystem,
            ReferenceImagePath: referenceImagePath,
            FrameLimit: frameLimit,
            CycleLimit: ScreenshotCycleCap
        );

    private static (TestTier Tier, TestSubsystem Subsystem) ClassifyMooneye(string relativePath, string directory) {
        var path = relativePath.Replace(oldChar: '\\', newChar: '/');

        if (path.Contains("/timer/", StringComparison.OrdinalIgnoreCase) || path.Contains("div_timing", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.Timing, TestSubsystem.TimerDiv);
        }

        if (path.Contains("/ppu/", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.Timing, TestSubsystem.PpuTiming);
        }

        if (path.Contains("oam_dma", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.Timing, TestSubsystem.OamDma);
        }

        if (path.Contains("/serial/", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.Timing, TestSubsystem.Serial);
        }

        if (path.Contains("/interrupts/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("intr_timing", StringComparison.OrdinalIgnoreCase)
            || path.Contains("ei_", StringComparison.OrdinalIgnoreCase)
            || path.Contains("di_timing", StringComparison.OrdinalIgnoreCase)
            || path.Contains("rapid_di_ei", StringComparison.OrdinalIgnoreCase)
            || path.Contains("if_ie", StringComparison.OrdinalIgnoreCase)
            || path.Contains("reti", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.Timing, TestSubsystem.Interrupts);
        }

        if (path.Contains("halt", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.Timing, TestSubsystem.Cpu);
        }

        if (path.Contains("_timing", StringComparison.OrdinalIgnoreCase) || path.Contains("add_sp", StringComparison.OrdinalIgnoreCase) || path.Contains("ld_hl_sp", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.Timing, TestSubsystem.CpuTiming);
        }

        if (path.Contains("/bits/", StringComparison.OrdinalIgnoreCase) || path.Contains("/instr/", StringComparison.OrdinalIgnoreCase) || path.Contains("boot_", StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.FunctionalBaseline, TestSubsystem.Cpu);
        }

        if (string.Equals(a: directory, b: "emulator-only", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.FunctionalBaseline, TestSubsystem.Other);
        }

        if (string.Equals(a: directory, b: "misc", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return (TestTier.CgbSpecific, TestSubsystem.Cgb);
        }

        return (TestTier.Timing, TestSubsystem.Cpu);
    }

    private static TestSubsystem ClassifyGbMicrotest(string fileName) =>
        fileName switch {
            _ when fileName.Contains("dma", StringComparison.OrdinalIgnoreCase) => TestSubsystem.OamDma,
            _ when fileName.Contains("tima", StringComparison.OrdinalIgnoreCase) || fileName.Contains("div", StringComparison.OrdinalIgnoreCase) => TestSubsystem.TimerDiv,
            _ when fileName.Contains("ppu", StringComparison.OrdinalIgnoreCase) || fileName.Contains("scx", StringComparison.OrdinalIgnoreCase) || fileName.Contains("vram", StringComparison.OrdinalIgnoreCase) || fileName.Contains("oam", StringComparison.OrdinalIgnoreCase) || fileName.Contains("lcd", StringComparison.OrdinalIgnoreCase) || fileName.Contains("stat", StringComparison.OrdinalIgnoreCase) => TestSubsystem.PpuTiming,
            _ when fileName.Contains("int", StringComparison.OrdinalIgnoreCase) || fileName.Contains("_if", StringComparison.OrdinalIgnoreCase) || fileName.Contains("ime", StringComparison.OrdinalIgnoreCase) => TestSubsystem.Interrupts,
            _ when fileName.Contains("halt", StringComparison.OrdinalIgnoreCase) => TestSubsystem.Cpu,
            _ when fileName.Contains("audio", StringComparison.OrdinalIgnoreCase) || fileName.Contains("sound", StringComparison.OrdinalIgnoreCase) => TestSubsystem.Apu,
            _ => TestSubsystem.CpuTiming,
        };

    // The model tag is the substring after the final '-' in the file name (mooneye/age convention).
    private static string ModelTag(string fileName) {
        var dash = fileName.LastIndexOf(value: '-');

        return (dash >= 0) ? fileName[(dash + 1)..] : string.Empty;
    }

    // We target exactly two SoC revisions: the latest original Game Boy (DMG-CPU C) and the latest Game Boy Color
    // (CPU CGB E). Revision-specific tests for any other revision are skipped rather than counted as failures.
    private const char DmgTargetRevision = 'C';
    private const char CgbTargetRevision = 'E';

    /// <summary>Maps a mooneye/age model tag to the emulated models it is expected to pass on, narrowed to the two
    /// target revisions (DMG-CPU C and CPU CGB E). An untagged test runs on the <paramref name="fallback"/> (or both
    /// models when none is given); tags for off-target revisions (e.g. dmg0, mgb, cgb0, agb) yield nothing.</summary>
    private static IReadOnlyList<ConsoleModel> ParseEligibleModels(string tag, ConsoleModel? fallback = null) {
        var isGroupTag = (tag.Length > 0) && (tag.Length <= 4) && tag.All(predicate: static c => c is 'G' or 'S' or 'C' or 'A');
        var hasModelName = ContainsAny(value: tag, "dmg", "mgb", "sgb", "cgb", "agb", "ags");

        if (!isGroupTag && !hasModelName) {
            return (fallback is { } single) ? [single] : [ConsoleModel.Dmg, ConsoleModel.Cgb];
        }

        var dmg = false;
        var cgb = false;

        if (isGroupTag) {
            // G = dmg+mgb (includes DMG-CPU C); C = cgb+agb+ags (includes CPU CGB E); A = agb+ags only (no CGB).
            dmg = tag.Contains(value: 'G');
            cgb = tag.Contains(value: 'C');
        }
        else {
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

    // True when the tag names a model family and our target revision is in scope: either no revision letters follow
    // the prefix (the test applies to all revisions of that family) or the explicit revision run contains the target.
    private static bool RevisionMatches(string tag, string prefix, char revision) {
        var index = tag.IndexOf(value: prefix, comparisonType: StringComparison.OrdinalIgnoreCase);

        if (index < 0) {
            return false;
        }

        var sawLetters = false;
        var matched = false;

        for (var cursor = (index + prefix.Length); cursor < tag.Length; cursor += 1) {
            var character = char.ToUpperInvariant(c: tag[cursor]);

            if (character is (>= 'A' and <= 'E') or (>= '0' and <= '9')) {
                sawLetters = true;
                matched |= (character == revision);
            }
            else {
                break;
            }
        }

        return !sawLetters || matched;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(predicate: needle => value.Contains(value: needle, comparisonType: StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateRoms(string directory, bool recurse = false) {
        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        return RomExtensions
            .SelectMany(selector: pattern => Directory.EnumerateFiles(path: directory, searchPattern: pattern, searchOption: option))
            .OrderBy(keySelector: static path => path, comparer: StringComparer.OrdinalIgnoreCase);
    }

    private static string RelativePath(string fullPath) =>
        Path.GetRelativePath(relativeTo: Root!, path: fullPath);

    private static RomCase MakeCase(
        string suite,
        string file,
        ConsoleModel model,
        ResultProtocol protocol,
        TestTier tier,
        TestSubsystem subsystem,
        long cycleLimit,
        int frameLimit = 0,
        string? referenceImagePath = null
    ) =>
        new(
            Suite: suite,
            RelativePath: RelativePath(fullPath: file),
            FullPath: file,
            Model: model,
            Protocol: protocol,
            Tier: tier,
            Subsystem: subsystem,
            ReferenceImagePath: referenceImagePath,
            FrameLimit: frameLimit,
            CycleLimit: cycleLimit
        );
}
