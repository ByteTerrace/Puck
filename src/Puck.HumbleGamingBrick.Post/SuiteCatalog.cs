namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Discovers <see cref="LedgerCase"/>s for every corpus suite beyond the original blargg/mooneye-acceptance pair —
/// one method per suite, each reading that suite's own <c>game-boy-test-roms-howto.md</c> exit condition and
/// success/failure convention. A suite whose howto makes a case impossible to check mechanically (button input, an
/// undecoded result convention, no shipped expected image) is still discovered, just recorded
/// <see cref="CaseDisposition.Unrunnable"/> with the reason — never silently dropped, so the ledger's <c>--record</c>
/// pass shows exactly what is and is not covered.
/// </summary>
internal static class SuiteCatalog {
    // mealybug, the three acid tests, and the mooneye/wilbertpol manual screenshot land after the same "0x40 LD B,B"
    // or "0xED" spin every register-signature case does; running further frames only wastes time once the picture is
    // settled.
    private const int AcceptanceFrameCap = 600;
    private const int AcidScreenshotFrameCap = 180;
    private const int AgeRegisterFrameCap = 600;
    private const int AgeScreenshotFrameCap = 180;
    private const int BlarggVisualFrameCap = 600;
    private const int BullyFrameCap = 30;
    // 15 GB frames (~252 ms), per testrunner.cpp: runTestRom().
    private const int GambatteFrameCap = 15;
    private const int GbMicrotestFrameCap = 2;
    // ~380 ms for is_if_set_during_ime0, the one GBMicrotest case the howto calls out as needing more than two frames.
    private const int GbMicrotestLongFrameCap = 23;
    private const int LittleThingsFrameCap = 30;
    private const int MealybugScreenshotFrameCap = 180;
    private const int ScreenshotFrameCapDefault = 180;
    private const int ScribbleDefaultFrameCap = 10;
    // statcount_auto: ~270 frames (4.5 s), the one scribbltests case the howto calls out as needing longer.
    private const int ScribbleStatcountAutoFrameCap = 270;
    private const int SameSuiteFrameCap = 600;
    private const int StrikethroughFrameCap = 30;
    private const int TurtleFrameCap = 30;
    private const int WilbertpolFrameCap = 600;

    /// <summary>Wraps <see cref="RomCatalog.ConformanceRoms"/> as ledger cases, keyed relative to the corpus's
    /// on-disk <c>blargg/</c> directory.</summary>
    public static IReadOnlyList<LedgerCase> ConformanceLedgerCases(string? root, string group, string subPath, ConsoleModel model) {
        if (root is null) {
            return [];
        }

        return FromRomCases(
            cases: RomCatalog.ConformanceRoms(
                root: root,
                group: group,
                subPath: subPath,
                model: model
            ),
            probe: ProbeKind.ConformanceSerial,
            suiteRoot: SuiteDir(
                relative: "blargg",
                root: root
            )
        );
    }
    /// <summary>Wraps <see cref="RomCatalog.AcceptanceRoms"/> as ledger cases, keyed relative to the corpus's on-disk
    /// <c>mooneye-test-suite/acceptance/</c> directory.</summary>
    public static IReadOnlyList<LedgerCase> AcceptanceLedgerCases(string? root, string group, string relativeDirectory, bool recurse) {
        if (root is null) {
            return [];
        }

        return FromRomCases(
            cases: RomCatalog.AcceptanceRoms(
                group: group,
                recurse: recurse,
                relativeDirectory: relativeDirectory,
                root: root
            ),
            probe: ProbeKind.AcceptanceFibonacci,
            suiteRoot: SuiteDir(
                relative: "mooneye-test-suite/acceptance",
                root: root
            )
        );
    }
    /// <summary>The mooneye <c>emulator-only/</c> cartridge-controller ROMs (mbc1/mbc2/mbc5) — untagged, so both
    /// target models run every case — read through the same serial Fibonacci signature as <c>acceptance/</c>.</summary>
    public static IReadOnlyList<LedgerCase> MooneyeEmulatorOnlyRoms(string? root) =>
        TaggedLedgerCases(
        frameCap: AcceptanceFrameCap,
        probe: ProbeKind.AcceptanceFibonacci,
        recurse: true,
        root: root,
        suite: "mooneye-emulator-only",
        suiteRelativeDirectory: "mooneye-test-suite/emulator-only"
    );
    /// <summary>The mooneye <c>misc/</c> boot-state and I/O ROMs — built with the same harness as <c>acceptance/</c>,
    /// so the same serial Fibonacci signature and revision-tag convention apply.</summary>
    public static IReadOnlyList<LedgerCase> MooneyeMiscRoms(string? root) =>
        TaggedLedgerCases(
        frameCap: AcceptanceFrameCap,
        probe: ProbeKind.AcceptanceFibonacci,
        recurse: true,
        root: root,
        suite: "mooneye-misc",
        suiteRelativeDirectory: "mooneye-test-suite/misc"
    );
    /// <summary>The mooneye <c>manual-only/sprite_priority.gb</c> screenshot case, on both target models, against the
    /// howto's own replacement "common palette" images.</summary>
    public static IReadOnlyList<LedgerCase> MooneyeManualRoms(string? root) =>
        ManualSpritePriorityRoms(
        root: root,
        suite: "mooneye-manual",
        suiteRelativeDirectory: "mooneye-test-suite/manual-only"
    );
    /// <summary>The wilbertpol fork's <c>acceptance/</c> ROMs — same revision-tag convention as mooneye's own, but
    /// this fork never emits its Fibonacci-or-<c>0x42</c> signature over serial, so <see cref="RegisterSignatureProbe"/>
    /// reads it straight from the register file after the fork's <c>0xED</c> lockup trap.</summary>
    public static IReadOnlyList<LedgerCase> WilbertpolAcceptanceRoms(string? root) =>
        TaggedLedgerCases(
        frameCap: WilbertpolFrameCap,
        probe: ProbeKind.RegisterSignature,
        recurse: true,
        root: root,
        suite: "wilbertpol-acceptance",
        suiteRelativeDirectory: "mooneye-test-suite-wilbertpol/acceptance"
    );
    /// <summary>The wilbertpol fork's <c>emulator-only/</c> ROMs, register-signature read.</summary>
    public static IReadOnlyList<LedgerCase> WilbertpolEmulatorOnlyRoms(string? root) =>
        TaggedLedgerCases(
        frameCap: WilbertpolFrameCap,
        probe: ProbeKind.RegisterSignature,
        recurse: true,
        root: root,
        suite: "wilbertpol-emulator-only",
        suiteRelativeDirectory: "mooneye-test-suite-wilbertpol/emulator-only"
    );
    /// <summary>The wilbertpol fork's <c>misc/</c> ROMs, register-signature read.</summary>
    public static IReadOnlyList<LedgerCase> WilbertpolMiscRoms(string? root) =>
        TaggedLedgerCases(
        frameCap: WilbertpolFrameCap,
        probe: ProbeKind.RegisterSignature,
        recurse: true,
        root: root,
        suite: "wilbertpol-misc",
        suiteRelativeDirectory: "mooneye-test-suite-wilbertpol/misc"
    );
    /// <summary>The wilbertpol fork's <c>manual-only/sprite_priority.gb</c> screenshot case (visual, so the fork's
    /// no-serial rule does not apply).</summary>
    public static IReadOnlyList<LedgerCase> WilbertpolManualRoms(string? root) =>
        ManualSpritePriorityRoms(
        root: root,
        suite: "wilbertpol-manual",
        suiteRelativeDirectory: "mooneye-test-suite-wilbertpol/manual-only"
    );
    /// <summary>
    /// SameSuite's ROMs never emit their Fibonacci-or-<c>0x42</c> signature over serial either, so this reads it from
    /// the register file the same way as the wilbertpol fork. Its own <c>apu/README.md</c> reports that CPU-CGB-E is
    /// the only target revision to pass the whole APU sub-suite; pre-CGB devices pass only <c>div_write_trigger</c>
    /// and <c>div_write_trigger_10</c>, which rely on hardware every revision shares — so every other <c>apu/</c> case
    /// runs on <see cref="ConsoleModel.CgbE"/> only, while every other sub-suite runs on both target models.
    /// </summary>
    public static IReadOnlyList<LedgerCase> SameSuiteRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "same-suite",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();

        foreach (var rom in EnumerateFiles(
            directory: suiteRoot,
            pattern: "*.gb",
            recurse: true
        )) {
            var relativePath = Rel(
                fullPath: rom,
                root: suiteRoot
            );
            var name = Path.GetFileNameWithoutExtension(path: rom);
            var isCgbOnlyApuCase = (
                relativePath.StartsWith(
                    value: "apu/",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ) &&
                !name.Contains(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: "div_write_trigger"
                )
            );

            foreach (var model in (isCgbOnlyApuCase
                ? ((ConsoleModel[])[ConsoleModel.CgbE])
                : ((ConsoleModel[])[ConsoleModel.DmgC, ConsoleModel.CgbE]))) {
                cases.Add(item: new LedgerCase(
                    FrameCap: SameSuiteFrameCap,
                    FullPath: rom,
                    Model: model,
                    Probe: ProbeKind.RegisterSignature,
                    RelativePath: relativePath,
                    Suite: "same-suite"
                ));
            }
        }

        return cases;
    }
    /// <summary>GBMicrotest is DMG-only per its howto (verified on a DMG-CPU-08); every case reads
    /// <c>$FF80</c>-<c>$FF82</c>, with <c>is_if_set_during_ime0</c> alone needing the longer frame budget the howto calls out.</summary>
    public static IReadOnlyList<LedgerCase> GbMicrotestRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "gbmicrotest",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();

        foreach (var rom in EnumerateFiles(
            directory: suiteRoot,
            pattern: "*.gb",
            recurse: false
        )) {
            var name = Path.GetFileNameWithoutExtension(path: rom);

            cases.Add(item: new LedgerCase(
                FrameCap: (string.Equals(
                    a: name,
                    b: "is_if_set_during_ime0",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
                    ? GbMicrotestLongFrameCap
                    : GbMicrotestFrameCap),
                FullPath: rom,
                Model: ConsoleModel.DmgC,
                Probe: ProbeKind.GbMicrotest,
                RelativePath: Rel(
                    fullPath: rom,
                    root: suiteRoot
                ),
                Suite: "gbmicrotest"
            ));
        }

        return cases;
    }
    /// <summary>
    /// AGE's exit condition is uniform (<c>0x40 LD B,B</c>, then the Fibonacci-or-<c>0x42</c> register signature), but
    /// the howto is explicit that some ROMs cannot be checked automatically and instead ship a per-device screenshot.
    /// This routes each ROM by what its own leaf folder actually ships: a ROM with a same-stem sibling PNG carrying a
    /// device marker (<c>dmg</c> for <see cref="ConsoleModel.DmgC"/>; <c>cgb</c> or <c>ncm</c> — non-CGB-mode, a
    /// CGB-hardware DMG-compatibility run — for <see cref="ConsoleModel.CgbE"/>) is a screenshot case; otherwise it is
    /// a register-signature case. ROM stems are matched longest-first and each PNG is claimed by at most one ROM, so
    /// e.g. <c>m3-bg-lcdc-ds.gb</c>'s own <c>-ds-cgbBCE.png</c> is not also offered to the shorter <c>m3-bg-lcdc.gb</c>.
    /// </summary>
    public static IReadOnlyList<LedgerCase> AgeRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "age-test-roms",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();
        var leafDirectories = Directory
            .EnumerateDirectories(
            path: suiteRoot,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )
            .Prepend(element: suiteRoot);

        foreach (var directory in leafDirectories) {
            var roms = Directory
                .EnumerateFiles(
                path: directory,
                searchOption: SearchOption.TopDirectoryOnly,
                searchPattern: "*.gb"
            )
                .OrderByDescending(keySelector: static path => Path.GetFileNameWithoutExtension(path: path).Length)
                .ToArray();
            var images = Directory.EnumerateFiles(
                path: directory,
                searchOption: SearchOption.TopDirectoryOnly,
                searchPattern: "*.png"
            ).ToArray();
            var claimed = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

            foreach (var rom in roms) {
                var stem = Path.GetFileNameWithoutExtension(path: rom);
                var relativePath = Rel(
                    fullPath: rom,
                    root: suiteRoot
                );
                var ownImages = images.Where(predicate: image =>
                    !claimed.Contains(item: image) &&
                    Path.GetFileName(path: image).StartsWith(
                        comparisonType: StringComparison.OrdinalIgnoreCase,
                        value: (stem + "-")
                    )
                ).ToArray();

                foreach (var image in ownImages) {
                    _ = claimed.Add(item: image);
                }

                AddModelCase(
                    cases: cases,
                    markers: ["dmg"],
                    model: ConsoleModel.DmgC,
                    ownImages: ownImages,
                    relativePath: relativePath,
                    rom: rom
                );
                AddModelCase(
                    cases: cases,
                    markers: ["cgb", "ncm"],
                    model: ConsoleModel.CgbE,
                    ownImages: ownImages,
                    relativePath: relativePath,
                    rom: rom
                );
            }
        }

        return cases;

        static void AddModelCase(List<LedgerCase> cases, string relativePath, string rom, ConsoleModel model, string[] ownImages, string[] markers) {
            var candidates = ownImages
                .Where(predicate: image => markers.Any(predicate: marker => Path.GetFileName(path: image).Contains(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: marker
            )))
                .OrderBy(
                keySelector: static path => path,
                comparer: StringComparer.OrdinalIgnoreCase
            )
                .ToArray();

            cases.Add(item: ((candidates.Length > 0)
                ? new LedgerCase(
                    ExpectedImageCandidates: candidates,
                    FrameCap: AgeScreenshotFrameCap,
                    FullPath: rom,
                    Model: model,
                    Probe: ProbeKind.Screenshot,
                    RelativePath: relativePath,
                    Suite: "age"
                )
                : new LedgerCase(
                    FrameCap: AgeRegisterFrameCap,
                    FullPath: rom,
                    Model: model,
                    Probe: ProbeKind.RegisterSignature,
                    RelativePath: relativePath,
                    Suite: "age"
                )));
        }
    }
    /// <summary>dmg-acid2 runs its one ROM on both target models against their respective expected images.</summary>
    public static IReadOnlyList<LedgerCase> DmgAcid2Roms(string? root) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDir(
            relative: "dmg-acid2",
            root: root
        );
        var rom = Path.Combine(
            path1: directory,
            path2: "dmg-acid2.gb"
        );

        if (!File.Exists(path: rom)) {
            return [];
        }

        return [
            new LedgerCase(
                ExpectedImageCandidates: [Path.Combine(
                path1: directory,
                path2: "dmg-acid2-dmg.png"
            )],
                FrameCap: AcidScreenshotFrameCap,
                FullPath: rom,
                Model: ConsoleModel.DmgC,
                Probe: ProbeKind.Screenshot,
                RelativePath: "dmg-acid2.gb",
                Suite: "dmg-acid2"
            ),
            new LedgerCase(
                ExpectedImageCandidates: [Path.Combine(
                path1: directory,
                path2: "dmg-acid2-cgb.png"
            )],
                FrameCap: AcidScreenshotFrameCap,
                FullPath: rom,
                Model: ConsoleModel.CgbE,
                Probe: ProbeKind.Screenshot,
                RelativePath: "dmg-acid2.gb",
                Suite: "dmg-acid2"
            ),
        ];
    }
    /// <summary>cgb-acid2 ships a single CGB-only <c>.gbc</c> cartridge and image.</summary>
    public static IReadOnlyList<LedgerCase> CgbAcid2Roms(string? root) =>
        SingleCgbAcidCase(
        fileStem: "cgb-acid2",
        root: root,
        suite: "cgb-acid2",
        suiteRelativeDirectory: "cgb-acid2"
    );
    /// <summary>cgb-acid-hell ships a single CGB-only <c>.gbc</c> cartridge and image.</summary>
    public static IReadOnlyList<LedgerCase> CgbAcidHellRoms(string? root) =>
        SingleCgbAcidCase(
        fileStem: "cgb-acid-hell",
        root: root,
        suite: "cgb-acid-hell",
        suiteRelativeDirectory: "cgb-acid-hell"
    );
    /// <summary>
    /// mealybug's <c>ppu/</c>, <c>dma/</c>, and <c>mbc/</c> ROMs, screenshot-compared against the shipped per-device
    /// image, per model, with the primary/fallback device tag the deliverable specifies
    /// (<c>_dmg_blob</c>/<c>_dmg_b</c>, <c>_cgb_c</c>/<c>_cgb_d</c>). A ROM with neither of a model's images shipped
    /// (every <c>dma/</c> and <c>mbc/</c> case today) is recorded unrunnable for that model rather than skipped.
    /// </summary>
    public static IReadOnlyList<LedgerCase> MealybugRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "mealybug-tearoom-tests",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();

        foreach (var subfolder in (string[])["ppu", "dma", "mbc"]) {
            var directory = Path.Combine(
                path1: suiteRoot,
                path2: subfolder
            );

            if (!Directory.Exists(path: directory)) {
                continue;
            }

            foreach (var rom in EnumerateFiles(
                directory: directory,
                pattern: "*.gb",
                recurse: false
            )) {
                var stem = Path.GetFileNameWithoutExtension(path: rom);
                var relativePath = Rel(
                    fullPath: rom,
                    root: suiteRoot
                );

                AddCase(
                    cases: cases,
                    directory: directory,
                    fallback: "_dmg_b.png",
                    model: ConsoleModel.DmgC,
                    primary: "_dmg_blob.png",
                    relativePath: relativePath,
                    rom: rom,
                    stem: stem
                );
                AddCase(
                    cases: cases,
                    directory: directory,
                    fallback: "_cgb_d.png",
                    model: ConsoleModel.CgbE,
                    primary: "_cgb_c.png",
                    relativePath: relativePath,
                    rom: rom,
                    stem: stem
                );
            }
        }

        return cases;

        static void AddCase(List<LedgerCase> cases, string relativePath, string rom, ConsoleModel model, string directory, string stem, string primary, string fallback) {
            var candidates = new[] {
                Path.Combine(path1: directory, path2: (stem + primary)),
                Path.Combine(path1: directory, path2: (stem + fallback)),
            };

            cases.Add(item: (candidates.Any(predicate: File.Exists)
                ? new LedgerCase(
                    ExpectedImageCandidates: candidates,
                    FrameCap: MealybugScreenshotFrameCap,
                    FullPath: rom,
                    Model: model,
                    Probe: ProbeKind.Screenshot,
                    RelativePath: relativePath,
                    Suite: "mealybug"
                )
                : new LedgerCase(
                    Disposition: CaseDisposition.Unrunnable,
                    FrameCap: MealybugScreenshotFrameCap,
                    FullPath: rom,
                    Model: model,
                    Probe: ProbeKind.Screenshot,
                    RelativePath: relativePath,
                    Suite: "mealybug",
                    UnrunnableReason: $"no expected image shipped for {model} ({stem}{primary} / {stem}{fallback})"
                )));
        }
    }
    /// <summary>The blargg <c>oam_bug/rom_singles</c> ROMs, read the same way as every other conformance group.</summary>
    public static IReadOnlyList<LedgerCase> BlarggOamBugSinglesRoms(string? root) =>
        ConformanceLedgerCases(
        group: "oam-bug-singles",
        model: ConsoleModel.DmgC,
        root: root,
        subPath: "oam_bug/rom_singles"
    );
    /// <summary>The blargg <c>mem_timing-2/rom_singles</c> ROMs.</summary>
    public static IReadOnlyList<LedgerCase> BlarggMemTiming2SinglesRoms(string? root) =>
        ConformanceLedgerCases(
        group: "mem-timing-2-singles",
        model: ConsoleModel.DmgC,
        root: root,
        subPath: "mem_timing-2/rom_singles"
    );
    /// <summary>The blargg top-level ROMs that report by screen content rather than the <c>$A000</c> block: <c>halt_bug.gb</c>,
    /// <c>interrupt_time/interrupt_time.gb</c>, <c>oam_bug/oam_bug.gb</c>, and <c>mem_timing-2/mem_timing.gb</c>.</summary>
    public static IReadOnlyList<LedgerCase> BlarggVisualRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "blargg",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();

        AddVisual(
            cases: cases,
            cgbImageRelative: "halt_bug-dmg-cgb.png",
            dmgImageRelative: "halt_bug-dmg-cgb.png",
            romRelative: "halt_bug.gb",
            suiteRoot: suiteRoot
        );
        AddVisual(
            cases: cases,
            cgbImageRelative: "interrupt_time/interrupt_time-cgb.png",
            dmgImageRelative: "interrupt_time/interrupt_time-dmg.png",
            romRelative: "interrupt_time/interrupt_time.gb",
            suiteRoot: suiteRoot
        );
        AddVisual(
            cases: cases,
            cgbImageRelative: "oam_bug/oam_bug-cgb.png",
            dmgImageRelative: "oam_bug/oam_bug-dmg.png",
            romRelative: "oam_bug/oam_bug.gb",
            suiteRoot: suiteRoot
        );
        AddVisual(
            cases: cases,
            cgbImageRelative: "mem_timing-2/mem_timing-dmg-cgb.png",
            dmgImageRelative: "mem_timing-2/mem_timing-dmg-cgb.png",
            romRelative: "mem_timing-2/mem_timing.gb",
            suiteRoot: suiteRoot
        );

        return cases;

        static void AddVisual(List<LedgerCase> cases, string suiteRoot, string romRelative, string dmgImageRelative, string cgbImageRelative) {
            var rom = FromRelative(
                relative: romRelative,
                root: suiteRoot
            );

            if (!File.Exists(path: rom)) {
                return;
            }

            cases.Add(item: new LedgerCase(
                ExpectedImageCandidates: [FromRelative(
                relative: dmgImageRelative,
                root: suiteRoot
            )],
                FrameCap: BlarggVisualFrameCap,
                FullPath: rom,
                Model: ConsoleModel.DmgC,
                Probe: ProbeKind.Screenshot,
                RelativePath: romRelative,
                Suite: "blargg-visual"
            ));
            cases.Add(item: new LedgerCase(
                ExpectedImageCandidates: [FromRelative(
                relative: cgbImageRelative,
                root: suiteRoot
            )],
                FrameCap: BlarggVisualFrameCap,
                FullPath: rom,
                Model: ConsoleModel.CgbE,
                Probe: ProbeKind.Screenshot,
                RelativePath: romRelative,
                Suite: "blargg-visual"
            ));
        }
    }
    /// <summary>
    /// gambatte's own result convention, ported from <c>test/testrunner.cpp</c>'s <c>main()</c>: a ROM's file stem is
    /// scanned for <c>dmg08_cgb04c_out</c> (one shared expected value for both models), else <c>dmg08_out</c> (a DMG
    /// value, plus a separate <c>cgb04c_out</c> value if that also appears — the multi-model
    /// <c>_dmg08_out1_cgb04c_out0</c> shape), else a bare <c>_out</c> (a CGB-only value; DMG is not tested at all).
    /// Whatever follows the matched tag is either <c>audio0</c>/<c>audio1</c> (<see cref="ProbeKind.Audio"/>) or a run
    /// of hex digits (<see cref="ProbeKind.HexPattern"/>) — this is an exact substring search, so the corpus's own
    /// "excluded" <c>_xout</c>/<c>_xoutaudio</c> variants (which testrunner.cpp also never matches) fall through
    /// untested by design. A model with no out-tag instead gets a <see cref="ProbeKind.Screenshot"/> case when a
    /// sibling expected image ships (a DMG image is <c>_dmg08.png</c> or <c>_xdmg08.png</c>; a CGB image is
    /// <c>_cgb04c.png</c>, compared under <see cref="ScreenshotPalette.GambatteCgb"/> since gambatte renders its CGB
    /// screenshots under its own RGB mix rather than the shared common palette). A ROM matching neither convention on
    /// either model is recorded <see cref="CaseDisposition.Unrunnable"/> on its one applicable model — DMG, unless
    /// its extension is <c>.gbc</c>.
    /// </summary>
    public static IReadOnlyList<LedgerCase> GambatteRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "gambatte",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();
        var roms = EnumerateFiles(
            directory: suiteRoot,
            pattern: "*.gb",
            recurse: true
        ).Concat(second: EnumerateFiles(
            directory: suiteRoot,
            pattern: "*.gbc",
            recurse: true
        ));

        foreach (var rom in roms) {
            var directory = (Path.GetDirectoryName(path: rom) ?? suiteRoot);
            var stem = Path.GetFileNameWithoutExtension(path: rom);
            var relativePath = Rel(
                fullPath: rom,
                root: suiteRoot
            );
            var (dmgTag, cgbTag) = GambatteOutTags(stem: stem);
            var dmgCase = GambatteResultCase(
                model: ConsoleModel.DmgC,
                relativePath: relativePath,
                rom: rom,
                stem: stem,
                tag: dmgTag
            );
            var cgbCase = GambatteResultCase(
                model: ConsoleModel.CgbE,
                relativePath: relativePath,
                rom: rom,
                stem: stem,
                tag: cgbTag
            );
            var dmgImage = FirstExisting(
                candidates: [(stem + "_dmg08.png"), (stem + "_xdmg08.png")],
                directory: directory
            );
            var cgbImage = FirstExisting(
                candidates: [(stem + "_cgb04c.png")],
                directory: directory
            );
            var routed = false;

            if (dmgCase is not null) {
                cases.Add(item: dmgCase);
                routed = true;
            } else if (dmgImage is not null) {
                cases.Add(item: new LedgerCase(
                    ExpectedImageCandidates: [dmgImage],
                    FrameCap: GambatteFrameCap,
                    FullPath: rom,
                    Model: ConsoleModel.DmgC,
                    Probe: ProbeKind.Screenshot,
                    RelativePath: relativePath,
                    Suite: "gambatte"
                ));
                routed = true;
            }

            if (cgbCase is not null) {
                cases.Add(item: cgbCase);
                routed = true;
            } else if (cgbImage is not null) {
                cases.Add(item: new LedgerCase(
                    ExpectedImageCandidates: [cgbImage],
                    FrameCap: GambatteFrameCap,
                    FullPath: rom,
                    Model: ConsoleModel.CgbE,
                    Palette: ScreenshotPalette.GambatteCgb,
                    Probe: ProbeKind.Screenshot,
                    RelativePath: relativePath,
                    Suite: "gambatte"
                ));
                routed = true;
            }

            if (!routed) {
                var fallbackModel = (string.Equals(
                    a: Path.GetExtension(path: rom),
                    b: ".gbc",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
                    ? ConsoleModel.CgbE
                    : ConsoleModel.DmgC);

                cases.Add(item: new LedgerCase(
                    Disposition: CaseDisposition.Unrunnable,
                    FrameCap: GambatteFrameCap,
                    FullPath: rom,
                    Model: fallbackModel,
                    Probe: ProbeKind.Screenshot,
                    RelativePath: relativePath,
                    Suite: "gambatte",
                    UnrunnableReason: "no _out/_outaudio result marker decodes for this ROM and no expected screenshot ships beside it"
                ));
            }
        }

        return cases;
    }
    /// <summary>little-things-gb's <c>firstwhite</c> (screenshot, shared across models) and <c>tellinglys</c>
    /// (requires pressing every button in sequence — recorded unrunnable).</summary>
    public static IReadOnlyList<LedgerCase> LittleThingsRoms(string? root) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDir(
            relative: "little-things-gb",
            root: root
        );

        if (!Directory.Exists(path: directory)) {
            return [];
        }

        var cases = new List<LedgerCase>();
        var firstwhite = Path.Combine(
            path1: directory,
            path2: "firstwhite.gb"
        );

        if (File.Exists(path: firstwhite)) {
            var image = Path.Combine(
                path1: directory,
                path2: "firstwhite-dmg-cgb.png"
            );

            cases.Add(item: new LedgerCase(
                ExpectedImageCandidates: [image],
                FrameCap: LittleThingsFrameCap,
                FullPath: firstwhite,
                Model: ConsoleModel.DmgC,
                Probe: ProbeKind.Screenshot,
                RelativePath: "firstwhite.gb",
                Suite: "little-things-gb"
            ));
            cases.Add(item: new LedgerCase(
                ExpectedImageCandidates: [image],
                FrameCap: LittleThingsFrameCap,
                FullPath: firstwhite,
                Model: ConsoleModel.CgbE,
                Probe: ProbeKind.Screenshot,
                RelativePath: "firstwhite.gb",
                Suite: "little-things-gb"
            ));
        }

        var tellinglys = Path.Combine(
            path1: directory,
            path2: "tellinglys.gb"
        );

        if (File.Exists(path: tellinglys)) {
            const string reason = "requires pressing every Game Boy button in sequence and then waiting 5 emulated seconds; not mechanical without button-input orchestration";

            cases.Add(item: new LedgerCase(
                Disposition: CaseDisposition.Unrunnable,
                FrameCap: LittleThingsFrameCap,
                FullPath: tellinglys,
                Model: ConsoleModel.DmgC,
                Probe: ProbeKind.Screenshot,
                RelativePath: "tellinglys.gb",
                Suite: "little-things-gb",
                UnrunnableReason: reason
            ));
            cases.Add(item: new LedgerCase(
                Disposition: CaseDisposition.Unrunnable,
                FrameCap: LittleThingsFrameCap,
                FullPath: tellinglys,
                Model: ConsoleModel.CgbE,
                Probe: ProbeKind.Screenshot,
                RelativePath: "tellinglys.gb",
                Suite: "little-things-gb",
                UnrunnableReason: reason
            ));
        }

        return cases;
    }
    /// <summary>scribbltests routes each case per its own howto note: <c>lycscx</c>/<c>lycscy</c>/<c>statcount-auto</c>
    /// share one image across both models, <c>palettely</c>/<c>scxly</c> ship separate per-model images,
    /// <c>statcount.gb</c> (the non-auto variant) has no shipped image, and <c>fairylake</c>/<c>winpos</c> are called
    /// out by name as having none either.</summary>
    public static IReadOnlyList<LedgerCase> ScribbleTestsRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "scribbltests",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();

        AddShared(
            cases: cases,
            frameCap: ScribbleDefaultFrameCap,
            imageRelative: "lycscx/lycscx-cgb-dmg.png",
            romRelative: "lycscx/lycscx.gb",
            suiteRoot: suiteRoot
        );
        AddShared(
            cases: cases,
            frameCap: ScribbleDefaultFrameCap,
            imageRelative: "lycscy/lycscy-cgb-dmg.png",
            romRelative: "lycscy/lycscy.gb",
            suiteRoot: suiteRoot
        );
        AddSeparate(
            cases: cases,
            cgbImageRelative: "palettely/palettely-cgb.png",
            dmgImageRelative: "palettely/palettely-dmg.png",
            frameCap: ScribbleDefaultFrameCap,
            romRelative: "palettely/palettely.gb",
            suiteRoot: suiteRoot
        );
        AddSeparate(
            cases: cases,
            cgbImageRelative: "scxly/scxly-cgb.png",
            dmgImageRelative: "scxly/scxly-dmg.png",
            frameCap: ScribbleDefaultFrameCap,
            romRelative: "scxly/scxly.gb",
            suiteRoot: suiteRoot
        );
        AddShared(
            cases: cases,
            frameCap: ScribbleStatcountAutoFrameCap,
            imageRelative: "statcount/statcount_auto-cgb-dmg.png",
            romRelative: "statcount/statcount-auto.gb",
            suiteRoot: suiteRoot
        );
        AddUnrunnable(
            cases: cases,
            reason: "no screenshot shipped for the non-auto variant; the howto documents only statcount_auto",
            romRelative: "statcount/statcount.gb",
            suiteRoot: suiteRoot
        );
        AddUnrunnable(
            cases: cases,
            reason: "the howto notes no screenshot exists for fairylake",
            romRelative: "fairylake/fairylake.gb",
            suiteRoot: suiteRoot
        );
        AddUnrunnable(
            cases: cases,
            reason: "the howto notes no screenshot exists for winpos",
            romRelative: "winpos/winpos.gb",
            suiteRoot: suiteRoot
        );

        return cases;

        static void AddShared(List<LedgerCase> cases, string suiteRoot, string romRelative, string imageRelative, int frameCap) {
            var rom = FromRelative(
                relative: romRelative,
                root: suiteRoot
            );

            if (!File.Exists(path: rom)) {
                return;
            }

            var image = FromRelative(
                relative: imageRelative,
                root: suiteRoot
            );

            cases.Add(item: new LedgerCase(ExpectedImageCandidates: [image], FrameCap: frameCap, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: romRelative, Suite: "scribbltests"));
            cases.Add(item: new LedgerCase(ExpectedImageCandidates: [image], FrameCap: frameCap, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: romRelative, Suite: "scribbltests"));
        }
        static void AddSeparate(List<LedgerCase> cases, string suiteRoot, string romRelative, string dmgImageRelative, string cgbImageRelative, int frameCap) {
            var rom = FromRelative(
                relative: romRelative,
                root: suiteRoot
            );

            if (!File.Exists(path: rom)) {
                return;
            }

            cases.Add(item: new LedgerCase(ExpectedImageCandidates: [FromRelative(relative: dmgImageRelative, root: suiteRoot)], FrameCap: frameCap, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: romRelative, Suite: "scribbltests"));
            cases.Add(item: new LedgerCase(ExpectedImageCandidates: [FromRelative(relative: cgbImageRelative, root: suiteRoot)], FrameCap: frameCap, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: romRelative, Suite: "scribbltests"));
        }
        static void AddUnrunnable(List<LedgerCase> cases, string suiteRoot, string romRelative, string reason) {
            var rom = FromRelative(
                relative: romRelative,
                root: suiteRoot
            );

            if (!File.Exists(path: rom)) {
                return;
            }

            cases.Add(item: new LedgerCase(Disposition: CaseDisposition.Unrunnable, FrameCap: ScribbleDefaultFrameCap, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: romRelative, Suite: "scribbltests", UnrunnableReason: reason));
            cases.Add(item: new LedgerCase(Disposition: CaseDisposition.Unrunnable, FrameCap: ScribbleDefaultFrameCap, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: romRelative, Suite: "scribbltests", UnrunnableReason: reason));
        }
    }
    /// <summary>strikethrough ships one ROM and a separate per-model image.</summary>
    public static IReadOnlyList<LedgerCase> StrikethroughRoms(string? root) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDir(
            relative: "strikethrough",
            root: root
        );
        var rom = Path.Combine(
            path1: directory,
            path2: "strikethrough.gb"
        );

        if (!File.Exists(path: rom)) {
            return [];
        }

        return [
            new LedgerCase(ExpectedImageCandidates: [Path.Combine(path1: directory, path2: "strikethrough-dmg.png")], FrameCap: StrikethroughFrameCap, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: "strikethrough.gb", Suite: "strikethrough"),
            new LedgerCase(ExpectedImageCandidates: [Path.Combine(path1: directory, path2: "strikethrough-cgb.png")], FrameCap: StrikethroughFrameCap, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: "strikethrough.gb", Suite: "strikethrough"),
        ];
    }
    /// <summary>Each turtle-tests case ships one image shared across both models.</summary>
    public static IReadOnlyList<LedgerCase> TurtleTestsRoms(string? root) {
        if (root is null) {
            return [];
        }

        var suiteRoot = SuiteDir(
            relative: "turtle-tests",
            root: root
        );

        if (!Directory.Exists(path: suiteRoot)) {
            return [];
        }

        var cases = new List<LedgerCase>();

        foreach (var rom in EnumerateFiles(
            directory: suiteRoot,
            pattern: "*.gb",
            recurse: true
        )) {
            var directory = (Path.GetDirectoryName(path: rom) ?? suiteRoot);
            var stem = Path.GetFileNameWithoutExtension(path: rom);
            var image = Path.Combine(
                path1: directory,
                path2: (stem + ".png")
            );
            var relativePath = Rel(
                fullPath: rom,
                root: suiteRoot
            );

            if (!File.Exists(path: image)) {
                const string reason = "no expected screenshot shipped beside this ROM";

                cases.Add(item: new LedgerCase(Disposition: CaseDisposition.Unrunnable, FrameCap: TurtleFrameCap, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: relativePath, Suite: "turtle-tests", UnrunnableReason: reason));
                cases.Add(item: new LedgerCase(Disposition: CaseDisposition.Unrunnable, FrameCap: TurtleFrameCap, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: relativePath, Suite: "turtle-tests", UnrunnableReason: reason));

                continue;
            }

            cases.Add(item: new LedgerCase(ExpectedImageCandidates: [image], FrameCap: TurtleFrameCap, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: relativePath, Suite: "turtle-tests"));
            cases.Add(item: new LedgerCase(ExpectedImageCandidates: [image], FrameCap: TurtleFrameCap, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: relativePath, Suite: "turtle-tests"));
        }

        return cases;
    }
    /// <summary>BullyGB ships one ROM and one image shared across both models (the howto's own DMG-C failure is a
    /// recorded fact the ledger records, not a reason to skip the model).</summary>
    public static IReadOnlyList<LedgerCase> BullyRoms(string? root) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDir(
            relative: "bully",
            root: root
        );
        var rom = Path.Combine(
            path1: directory,
            path2: "bully.gb"
        );

        if (!File.Exists(path: rom)) {
            return [];
        }

        var image = Path.Combine(
            path1: directory,
            path2: "bully.png"
        );

        return [
            new LedgerCase(ExpectedImageCandidates: [image], FrameCap: BullyFrameCap, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: "bully.gb", Suite: "bully"),
            new LedgerCase(ExpectedImageCandidates: [image], FrameCap: BullyFrameCap, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: "bully.gb", Suite: "bully"),
        ];
    }
    /// <summary>rtc3test selects one of three subtests by pressing buttons at startup; recorded unrunnable on both
    /// models rather than skipped.</summary>
    public static IReadOnlyList<LedgerCase> Rtc3TestRoms(string? root) =>
        SingleUnrunnableCase(
        reason: "selecting a subtest requires pressing A / down+A / down+down+A at startup; not mechanical without button-input orchestration",
        romFileName: "rtc3test.gb",
        root: root,
        suite: "rtc3test",
        suiteRelativeDirectory: "rtc3test"
    );
    /// <summary>The MBC3 Bank Tester drives its bank display by button input its howto does not spell out
    /// mechanically; recorded unrunnable on both models rather than skipped.</summary>
    public static IReadOnlyList<LedgerCase> Mbc3TesterRoms(string? root) =>
        SingleUnrunnableCase(
        reason: "bank selection is driven by button input this Post battery does not orchestrate",
        romFileName: "mbc3-tester.gb",
        root: root,
        suite: "mbc3-tester",
        suiteRelativeDirectory: "mbc3-tester"
    );

    private static LedgerCase[] FromRomCases(IReadOnlyList<RomCase> cases, string suiteRoot, ProbeKind probe) =>
        cases.Select(selector: romCase => new LedgerCase(
        FrameCap: romCase.FrameCap,
        FullPath: romCase.FullPath,
        Model: romCase.Model,
        Probe: probe,
        RelativePath: Rel(
            fullPath: romCase.FullPath,
            root: suiteRoot
        ),
        Suite: romCase.Group
    )).ToArray();
    private static string FromRelative(string root, string relative) =>
        Path.Combine(
        path1: root,
        path2: relative.Replace(
            newChar: Path.DirectorySeparatorChar,
            oldChar: '/'
        )
    );
    private static string? FirstExisting(string directory, IEnumerable<string> candidates) =>
        candidates
            .Select(selector: candidate => Path.Combine(
            path1: directory,
            path2: candidate
        ))
            .FirstOrDefault(predicate: File.Exists);
    // Ports testrunner.cpp's main() branch that decides which of a ROM's two models get an out-tag at all, and where
    // each one's expected value starts. Ordinal, case-sensitive, exact-substring — matching testrunner.cpp exactly,
    // including that it never matches the corpus's own "excluded" _xout/_xoutaudio variants.
    private static (string? DmgTag, string? CgbTag) GambatteOutTags(string stem) {
        const string Combined = "dmg08_cgb04c_out";
        const string DmgOnly = "dmg08_out";
        const string CgbOnly = "cgb04c_out";
        const string Generic = "_out";

        if (stem.Contains(
            comparisonType: StringComparison.Ordinal,
            value: Combined
        )) {
            return (Combined, Combined);
        }

        if (stem.Contains(
            comparisonType: StringComparison.Ordinal,
            value: DmgOnly
        )) {
            return (DmgOnly, (stem.Contains(
                comparisonType: StringComparison.Ordinal,
                value: CgbOnly
            )
                ? CgbOnly
                : null));
        }

        return (null, (stem.Contains(
            comparisonType: StringComparison.Ordinal,
            value: Generic
        )
            ? Generic
            : null));
    }
    // Builds the Audio or HexPattern case a matched out-tag implies: whatever follows the tag is either an
    // audio0/audio1 marker or the maximal run of hex digits (frameBufferMatchesOut stops at the first character
    // tileFromChar rejects, so trailing text past the digits — e.g. a second model's tag — is never consumed here).
    private static LedgerCase? GambatteResultCase(string rom, string relativePath, ConsoleModel model, string stem, string? tag) {
        if (tag is null) {
            return null;
        }

        var value = stem[(stem.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: tag
        ) + tag.Length)..];

        if (value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "audio0"
        )) {
            return new LedgerCase(ExpectedAudio: AudioExpectation.Silence, FrameCap: GambatteFrameCap, FullPath: rom, Model: model, Probe: ProbeKind.Audio, RelativePath: relativePath, Suite: "gambatte");
        }

        if (value.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "audio1"
        )) {
            return new LedgerCase(ExpectedAudio: AudioExpectation.Sound, FrameCap: GambatteFrameCap, FullPath: rom, Model: model, Probe: ProbeKind.Audio, RelativePath: relativePath, Suite: "gambatte");
        }

        var hex = new string(value: value.TakeWhile(predicate: static c => char.IsAsciiHexDigit(c: c)).ToArray());

        return ((hex.Length > 0)
            ? new LedgerCase(ExpectedHexPattern: hex, FrameCap: GambatteFrameCap, FullPath: rom, Model: model, Probe: ProbeKind.HexPattern, RelativePath: relativePath, Suite: "gambatte")
            : null);
    }
    private static IReadOnlyList<LedgerCase> ManualSpritePriorityRoms(string? root, string suite, string suiteRelativeDirectory) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDir(
            relative: suiteRelativeDirectory,
            root: root
        );
        var rom = Path.Combine(
            path1: directory,
            path2: "sprite_priority.gb"
        );

        if (!File.Exists(path: rom)) {
            return [];
        }

        return [
            new LedgerCase(ExpectedImageCandidates: [Path.Combine(path1: directory, path2: "sprite_priority-dmg.png")], FrameCap: ScreenshotFrameCapDefault, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: "sprite_priority.gb", Suite: suite),
            new LedgerCase(ExpectedImageCandidates: [Path.Combine(path1: directory, path2: "sprite_priority-cgb.png")], FrameCap: ScreenshotFrameCapDefault, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: "sprite_priority.gb", Suite: suite),
        ];
    }
    private static IReadOnlyList<string> EnumerateFiles(string directory, string pattern, bool recurse) =>
        Directory
            .EnumerateFiles(
            path: directory,
            searchOption: (recurse
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly),
            searchPattern: pattern
        )
            .OrderBy(
            keySelector: static path => path,
            comparer: StringComparer.OrdinalIgnoreCase
        )
            .ToArray();
    private static string Rel(string root, string fullPath) =>
        Path.GetRelativePath(
        path: fullPath,
        relativeTo: root
    ).Replace(
        newChar: '/',
        oldChar: Path.DirectorySeparatorChar
    );
    private static LedgerCase[] SingleCgbAcidCase(string? root, string suite, string suiteRelativeDirectory, string fileStem) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDir(
            relative: suiteRelativeDirectory,
            root: root
        );
        var rom = Path.Combine(
            path1: directory,
            path2: (fileStem + ".gbc")
        );

        if (!File.Exists(path: rom)) {
            return [];
        }

        return [
            new LedgerCase(
                ExpectedImageCandidates: [Path.Combine(
                path1: directory,
                path2: (fileStem + ".png")
            )],
                FrameCap: AcidScreenshotFrameCap,
                FullPath: rom,
                Model: ConsoleModel.CgbE,
                Probe: ProbeKind.Screenshot,
                RelativePath: (fileStem + ".gbc"),
                Suite: suite
            ),
        ];
    }
    private static LedgerCase[] SingleUnrunnableCase(string? root, string suite, string suiteRelativeDirectory, string romFileName, string reason) {
        if (root is null) {
            return [];
        }

        var directory = SuiteDir(
            relative: suiteRelativeDirectory,
            root: root
        );
        var rom = Path.Combine(
            path1: directory,
            path2: romFileName
        );

        if (!File.Exists(path: rom)) {
            return [];
        }

        return [
            new LedgerCase(Disposition: CaseDisposition.Unrunnable, FrameCap: 1, FullPath: rom, Model: ConsoleModel.DmgC, Probe: ProbeKind.Screenshot, RelativePath: romFileName, Suite: suite, UnrunnableReason: reason),
            new LedgerCase(Disposition: CaseDisposition.Unrunnable, FrameCap: 1, FullPath: rom, Model: ConsoleModel.CgbE, Probe: ProbeKind.Screenshot, RelativePath: romFileName, Suite: suite, UnrunnableReason: reason),
        ];
    }
    private static string SuiteDir(string root, string relative) =>
        Path.Combine(
        path1: root,
        path2: relative.Replace(
            newChar: Path.DirectorySeparatorChar,
            oldChar: '/'
        )
    );
    private static IReadOnlyList<LedgerCase> TaggedLedgerCases(string? root, string suite, string suiteRelativeDirectory, bool recurse, int frameCap, ProbeKind probe) {
        if (root is null) {
            return [];
        }

        return FromRomCases(
            cases: RomCatalog.TaggedRoms(
                frameCap: frameCap,
                group: suite,
                recurse: recurse,
                root: root,
                suiteRelativeDirectory: suiteRelativeDirectory
            ),
            probe: probe,
            suiteRoot: SuiteDir(
                relative: suiteRelativeDirectory,
                root: root
            )
        );
    }
}
