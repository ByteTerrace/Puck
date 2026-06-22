using Puck.GameBoy.Conformance;

// Headless Game Boy conformance runner. Exit codes: 0 = all gates passed, 1 = a gate failed, 2 = infra error.
// Test ROM / vector assets are read from the PUCK_GB_TESTROMS environment variable or a --roms <dir> argument;
// suites whose assets are absent are skipped (reported), so the runner still exercises the asset-free gates.
try {
    // Diagnostic mode: --trace <romPath> runs one ROM to its breakpoint and dumps CPU + high-RAM diagnostics.
    for (var index = 0; index < (args.Length - 1); index += 1) {
        if (args[index] == "--trace") {
            return MooneyeTrace.Run(
                output: Console.Out,
                romPath: args[index + 1]
            );
        }

        // --blargg <romPathOrDir>: run a Blargg test ROM (or a directory of them) and report the serial result.
        if (args[index] == "--blargg") {
            return BlarggRunner.Run(
                output: Console.Out,
                path: args[index + 1]
            );
        }

        // --run <romPath> <frames> <outputPng>: boot a game and dump a framebuffer PNG.
        if ((args[index] == "--run") && (index + 3 < args.Length)) {
            return GameRunner.Run(
                frames: int.Parse(s: args[index + 2]),
                output: Console.Out,
                outputPath: args[index + 3],
                romPath: args[index + 1]
            );
        }
    }

    var failures = 0;
    var passes = 0;

    Console.WriteLine("Puck.GameBoy conformance");

    (string Suite, IReadOnlyList<(string Name, Func<string?> Run)> Tests)[] smokeSuites = [
        ("CPU", CpuSmokeTests.All),
        ("Timer", TimerSmokeTests.All),
        ("OAM DMA", OamDmaSmokeTests.All),
        ("PPU", PpuSmokeTests.All),
        ("Cartridge", CartridgeSmokeTests.All),
        ("Joypad/Serial", IoSmokeTests.All),
        ("APU", ApuSmokeTests.All),
        ("Host pacing", HostPacingSmokeTests.All),
    ];

    foreach (var (suite, tests) in smokeSuites) {
        Console.WriteLine($"== {suite} smoke tests (no assets) ==");

        foreach (var (name, run) in tests) {
            var detail = run();

            if (detail is null) {
                passes += 1;

                Console.WriteLine($"  PASS  {name}");
            }
            else {
                failures += 1;

                Console.WriteLine($"  FAIL  {name}: {detail}");
            }
        }
    }

    Console.WriteLine($"smoke: {passes} passed, {failures} failed");

    var assetRoot = ResolveAssetRoot(arguments: args);

    if (assetRoot is null) {
        Console.WriteLine("== ROM suites: SKIPPED (set PUCK_GB_TESTROMS or pass --roms <dir>) ==");
    }
    else if (!Directory.Exists(path: assetRoot)) {
        Console.WriteLine($"== ROM suites: SKIPPED (asset path not found: {assetRoot}) ==");
    }
    else {
        Console.WriteLine($"== mooneye acceptance ({assetRoot}) ==");

        var (mooneyePassed, mooneyeFailed, mooneyeSkipped) = MooneyeRunner.RunAcceptance(
            assetRoot: assetRoot,
            output: Console.Out
        );

        Console.WriteLine(
            $"mooneye acceptance: {mooneyePassed} passed, {mooneyeFailed} failed, {mooneyeSkipped} skipped (non-DMG model)"
        );
    }

    // The exit code gates on the asset-free smoke tests; the mooneye suite is reported as a progress signal while
    // the PPU and mappers are still being built.
    return ((failures == 0) ? 0 : 1);
}
catch (Exception error) {
    Console.Error.WriteLine($"conformance infra error: {error}");

    return 2;
}

static string? ResolveAssetRoot(string[] arguments) {
    for (var index = 0; index < (arguments.Length - 1); index += 1) {
        if (arguments[index] == "--roms") {
            return arguments[index + 1];
        }
    }

    var fromEnvironment = Environment.GetEnvironmentVariable(variable: "PUCK_GB_TESTROMS");

    return (string.IsNullOrWhiteSpace(fromEnvironment)
        ? null
        : fromEnvironment);
}
