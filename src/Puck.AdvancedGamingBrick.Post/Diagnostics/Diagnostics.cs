namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// The diagnostic surface of the POST: the cosim-oracle tooling and single-ROM inspectors that drive the
/// accuracy surface. These are investigative tools, not
/// self-checking stages; <see cref="TryRun"/> dispatches them from CLI flags before the POST battery runs, so the battery
/// stays the default. The menu-driven accuracy suite (<see cref="RunAccuracySuite"/>) and the AGS aging cartridge
/// (<see cref="RunAgs"/>) are conformance runs shared with their Tier-B measurement stages
/// (<see cref="AccuracySuiteStage"/> / <see cref="AgsStage"/>). Split by mode across the <c>Diagnostics/</c> folder
/// (partial-class files, one per CLI flag); this file holds only the dispatch entry and the state every mode shares.
/// </summary>
internal static partial class Diagnostics {
    /// <summary>The BIOS image every machine is built with. Defaults to a zeroed stub; the entry point loads the
    /// open-source replacement BIOS into it when one is available.</summary>
    public static ReadOnlyMemory<byte> BiosImage { get; set; } = new byte[ReplacementBios.ImageSize];

    /// <summary>The number of suites the menu-driven accuracy suite steps through.</summary>
    public const int AccuracySuiteCount = 14;

    /// <summary>Dispatches the diagnostic CLI flags — each runs a single investigative mode and returns; when none
    /// matches, the caller proceeds to the POST battery.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="exitCode">The exit code the handled mode produced (0 when it does not gate).</param>
    /// <returns><see langword="true"/> when a diagnostic flag was handled (return <paramref name="exitCode"/>, skip the
    /// battery); otherwise <see langword="false"/>.</returns>
    public static bool TryRun(string[] args, out int exitCode) {
        exitCode = 0;

        // --oracle: run the self-authored cycle-oracle probe battery (survey #1) — measured vs documented per probe.
        if (Array.IndexOf(array: args, value: "--oracle") >= 0) {
            exitCode = OracleProbes.RunOracle(args: args);

            return true;
        }

        // --bench [--bench-rom <rom>] [--bench-frames <n>] [--bench-fleet <csv>]: the AGB machine-fleet performance
        // instrument (fleet scaling shapes, burst catch-up, Create/Snapshot/Restore/Fork latency+allocation,
        // MT-vs-ST bit-lock guard) — the Advanced-core counterpart to the Humble Post's --bench.
        foreach (var arg in args) {
            if (string.Equals(a: arg, b: "--bench", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                exitCode = BenchDiagnostic.Run(args: args);

                return true;
            }
        }

        // --save-test: verify the cartridge save-persistence (.sav) round-trip, standalone.
        if (Array.IndexOf(array: args, value: "--save-test") >= 0) {
            var (pass, detail) = SaveRoundTripProbe.Run();

            Console.WriteLine(value: $"== save persistence round-trip: {(pass ? "PASS" : "FAIL")} — {detail} ==");
            exitCode = (pass ? 0 : 1);

            return true;
        }

        // --state-roundtrip [rom]: verify the whole-machine savestate (Snapshot/Restore) round-trip over the generated
        // micro-ROMs and, when a path is supplied, a real ROM too.
        var stateRoundTripIndex = Array.IndexOf(array: args, value: "--state-roundtrip");

        if (stateRoundTripIndex >= 0) {
            var romArg = ((((stateRoundTripIndex + 1) < args.Length) && !args[(stateRoundTripIndex + 1)].StartsWith(value: "--", comparisonType: StringComparison.Ordinal))
                ? args[(stateRoundTripIndex + 1)]
                : null);

            exitCode = StateRoundTrip(romPath: romArg);

            return true;
        }

        // --hash-divergence <romA> [romB]: the per-tick hash-divergence localizer (kept out of TryRun to bound its
        // cyclomatic complexity — see TryHashDivergence).
        if (TryHashDivergence(args: args, exitCode: out var hashDivergenceExitCode)) {
            exitCode = hashDivergenceExitCode;

            return true;
        }

        // --trace-crash <rom>: report the first branch into unmapped memory.
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (args[index] == "--trace-crash") {
                TraceCrash(romPath: args[(index + 1)]);

                return true;
            }
        }

        // --render <rom> <out.png> [steps]: boot a ROM and dump its framebuffer, to eyeball the PPU output.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--render") {
                var steps = ((((index + 3) < args.Length) && long.TryParse(args[(index + 3)], out var parsed))
                    ? parsed
                    : 6_000_000L);

                Render(romPath: args[(index + 1)], outputPath: args[(index + 2)], steps: steps);

                return true;
            }
        }

        // --render-hash <rom> <steps>: print the framebuffer hash after N steps, for capturing a render floor.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--render-hash") {
                var (_, _, detail) = RenderHashProbe.Run(romPath: args[(index + 1)], steps: long.Parse(s: args[(index + 2)]), expected: 0ul, bios: BiosImage);

                Console.WriteLine(value: $"  [HASH] {Path.GetFileName(path: args[(index + 1)])}: {detail}");

                return true;
            }
        }

        // --pctrace <rom> <steps>: print executing 0x08… instruction addresses, to diff against the cosim oracle.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--pctrace") {
                PcTrace(romPath: args[(index + 1)], steps: long.Parse(s: args[(index + 2)]));

                return true;
            }
        }

        // --statetrace <rom> <steps>: full per-instruction CPU state, to diff against the cosim oracle's --statetrace.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--statetrace") {
                if (ParityBiosGuard(mode: "--statetrace", args: args)) {
                    exitCode = 2;

                    return true;
                }

                StateTrace(romPath: args[(index + 1)], steps: long.Parse(s: args[(index + 2)]));

                return true;
            }
        }

        // --gen-rom <kind> <out.gba>: hand-assemble a timer/IRQ micro-ROM to disk for lockstep against the cosim oracle.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--gen-rom") {
                MicroRoms.Generate(kind: args[(index + 1)], outPath: args[(index + 2)]);

                return true;
            }
        }

        // --lockstep <rom> <steps> [direct]: step Puck against the cosim oracle in lockstep to the first divergence.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--lockstep") {
                if (ParityBiosGuard(mode: "--lockstep", args: args)) {
                    exitCode = 2;

                    return true;
                }

                exitCode = Lockstep(romPath: args[(index + 1)], steps: long.Parse(s: args[(index + 2)]), direct: (Array.IndexOf(array: args, value: "direct") >= 0));

                return true;
            }
        }

        // --iodump <rom> <steps>: dump every I/O register halfword, to diff against the cosim oracle's iodump.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--iodump") {
                IoDump(romPath: args[(index + 1)], steps: long.Parse(s: args[(index + 2)]));

                return true;
            }
        }

        // --probe <rom> <steps> | --link-init-trace <rom> <loHex> <hiHex> <count> [skip]: blank-screen boot diagnostics.
        if (TryDiagnostic(args: args)) {
            return true;
        }

        // --trace-cycles <rom> <steps>: per-instruction cycle trace, to diff against the cosim oracle.
        for (var index = 0; (index < (args.Length - 2)); ++index) {
            if (args[index] == "--trace-cycles") {
                if (ParityBiosGuard(mode: "--trace-cycles", args: args)) {
                    exitCode = 2;

                    return true;
                }

                TraceCycles(romPath: args[(index + 1)], steps: long.Parse(s: args[(index + 2)]));

                return true;
            }
        }

        // --accuracy-suite <rom>: run the menu-driven accuracy suite headlessly via the debug-log register.
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (args[index] == "--accuracy-suite") {
                exitCode = RunAccuracySuite(romPath: args[(index + 1)], name: "accuracy suite");

                return true;
            }
        }

        // --ags <rom>: run the AGS aging cartridge (TCHK10 dump) headlessly and print the per-test result stream.
        for (var index = 0; (index < (args.Length - 1)); ++index) {
            if (args[index] == "--ags") {
                _ = RunAgs(romPath: args[(index + 1)], name: Path.GetFileName(path: args[(index + 1)]));

                return true;
            }
        }

        // --dump-snapshot [--frames N] [--rom <path>] [--out <file>]: boot the synthetic cartridge (or --rom), run N
        // frames (default 300), and write the raw snapshot image + a sidecar section table to disk — offline
        // cross-build diffing input for C1's zero-byte-shift proof (--hash-divergence has no cross-build mode).
        if (Array.IndexOf(array: args, value: "--dump-snapshot") >= 0) {
            exitCode = DumpSnapshot(args: args);

            return true;
        }

        return false;
    }
}
