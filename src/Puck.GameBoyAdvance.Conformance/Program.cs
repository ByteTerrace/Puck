using Puck.GameBoyAdvance.Conformance;

// Entry point for the Game Boy Advance conformance harness. For now it runs the self-contained CPU smoke
// vectors; the external ROM suites (armwrestler / gba-suite / FuzzARM) attach later, sourced from the
// PUCK_GBA_TESTROMS environment variable or a --roms <dir> argument, mirroring the GBC harness convention.
// Load the open-source replacement BIOS if available (PUCK_GBA_BIOS), so IRQ dispatch and SWIs work.
var biosPath = Environment.GetEnvironmentVariable(variable: "PUCK_GBA_BIOS");

if (!string.IsNullOrEmpty(biosPath) && File.Exists(biosPath)) {
    var biosBytes = File.ReadAllBytes(path: biosPath);

    if (biosBytes.Length == Puck.GameBoyAdvance.ReplacementBios.ImageSize) {
        RomRunner.BiosImage = biosBytes;

        Console.WriteLine($"== BIOS: loaded replacement BIOS from {biosPath} ==");
    }
}

// --trace-crash <rom>: run a ROM and report the instruction that first branches into unmapped memory.
for (var index = 0; index < args.Length - 1; ++index) {
    if (args[index] == "--trace-crash") {
        RomRunner.TraceCrash(romPath: args[index + 1]);

        return 0;
    }
}

// --render <rom> <out.png> [steps]: boot a ROM and dump its framebuffer, to eyeball the PPU output.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--render") {
        var steps = ((index + 3) < args.Length) && long.TryParse(args[index + 3], out var parsed)
            ? parsed
            : 6_000_000L;

        RomRunner.Render(romPath: args[index + 1], outputPath: args[index + 2], steps: steps);

        return 0;
    }
}

// --render-hash <rom> <steps>: print the framebuffer hash after N steps, for capturing a render floor.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--render-hash") {
        _ = RomRunner.RunRenderHash(romPath: args[index + 1], name: Path.GetFileName(args[index + 1]), steps: long.Parse(args[index + 2]), expected: 0ul);

        return 0;
    }
}

// --pctrace <rom> <steps>: print executing 0x08… instruction addresses, to diff against the mGBA cosim.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--pctrace") {
        RomRunner.PcTrace(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

        return 0;
    }
}

// --probe <rom> <steps>: dump machine state after running, to diagnose a blank-screen boot.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--probe") {
        RomRunner.Probe(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

        return 0;
    }
}

// --trace-cycles <rom> <steps>: per-instruction cycle trace, to diff against the mGBA cosim oracle.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--trace-cycles") {
        RomRunner.TraceCycles(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

        return 0;
    }
}

// --ags <rom>: run the AGS aging cartridge (TCHK10 dump) headlessly and print the per-test result stream.
for (var index = 0; index < args.Length - 1; ++index) {
    if (args[index] == "--ags") {
        _ = RomRunner.RunAgs(romPath: args[index + 1], name: Path.GetFileName(args[index + 1]));

        return 0;
    }
}

var failures = SmokeTests.Run();

var assetRoot = Environment.GetEnvironmentVariable(variable: "PUCK_GBA_TESTROMS");

for (var index = 0; index < args.Length - 1; ++index) {
    if (args[index] == "--roms") {
        assetRoot = args[index + 1];
    }
}

if (string.IsNullOrEmpty(assetRoot)) {
    Console.WriteLine("== ROM suites: SKIPPED (set PUCK_GBA_TESTROMS or pass --roms <dir>) ==");
}
else {
    Console.WriteLine($"== jsmolka gba-tests (assets at {assetRoot}) ==");

    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "arm", "arm.gba"), name: "arm");
    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "thumb", "thumb.gba"), name: "thumb");
    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "memory", "memory.gba"), name: "memory");

    // Note: jsmolka's bios.gba is intentionally excluded — it asserts the *official* Nintendo BIOS's exact
    // open-bus read values, which no clean-room replacement reproduces. BIOS IRQ/SWI behaviour is validated by
    // the BiosIrqDispatch smoke test instead.

    // FuzzARM, if cloned alongside gba-tests, adds randomized CPU coverage.
    var fuzzArmRoot = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(assetRoot)) ?? assetRoot, "FuzzARM");

    if (Directory.Exists(fuzzArmRoot)) {
        Console.WriteLine($"== FuzzARM (assets at {fuzzArmRoot}) ==");

        failures += RomRunner.RunFuzzArm(romPath: Path.Combine(fuzzArmRoot, "ARM_Any.gba"), name: "ARM_Any");
        failures += RomRunner.RunFuzzArm(romPath: Path.Combine(fuzzArmRoot, "THUMB_Any.gba"), name: "THUMB_Any");
    }

    // Deterministic render-hash floors: the core is fully deterministic, so a known-good frame must reproduce
    // its FNV-1a hash exactly. These guard the whole CPU→bus→PPU pipeline against silent regressions while we
    // work the accuracy frontier. jsmolka's screen demos come from the suite dir; large commercial ROMs (which
    // are user-supplied, not committed) come from PUCK_GBA_GAMES / --games and SKIP cleanly when absent.
    Console.WriteLine("== render-hash floors ==");

    failures += RomRunner.RunRenderHash(romPath: Path.Combine(assetRoot, "ppu", "hello.gba"), name: "ppu/hello", steps: 6_000_000, expected: 0x62B76C0E0223A81Cul);
    failures += RomRunner.RunRenderHash(romPath: Path.Combine(assetRoot, "ppu", "stripes.gba"), name: "ppu/stripes", steps: 6_000_000, expected: 0x2F1E64B48356B525ul);

    var gamesRoot = Environment.GetEnvironmentVariable(variable: "PUCK_GBA_GAMES");

    for (var index = 0; index < args.Length - 1; ++index) {
        if (args[index] == "--games") {
            gamesRoot = args[index + 1];
        }
    }

    if (!string.IsNullOrEmpty(gamesRoot) && Directory.Exists(gamesRoot)) {
        failures += RomRunner.RunRenderHash(romPath: Path.Combine(gamesRoot, "A.gba"), name: "A (Golden Sun)", steps: 120_000_000, expected: 0x83AF051D6A622EA2ul);
        failures += RomRunner.RunRenderHash(romPath: Path.Combine(gamesRoot, "AGS Aging Cartridge (World) (v7.1).gba"), name: "AGS menu", steps: 6_000_000, expected: 0x37893C186522CBD2ul);
    }
}

return failures;
