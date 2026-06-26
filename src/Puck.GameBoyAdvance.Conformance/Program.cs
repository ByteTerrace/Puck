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

// --save-test: verify the cartridge save-persistence (.sav export/import) round-trip, standalone.
if (Array.IndexOf(array: args, value: "--save-test") >= 0) {
    return RomRunner.RunSaveRoundtrip();
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

// --statetrace <rom> <steps>: full per-instruction CPU state (PC/CPSR/r0..r14/cycles), to diff against the
// mGBA cosim's --statetrace and find the first true register divergence (not just the first PC mismatch).
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--statetrace") {
        RomRunner.StateTrace(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

        return 0;
    }
}

// --gen-rom <kind> <out.gba>: hand-assemble a timer/IRQ micro-ROM (timer-irq | cascade-irq | ime-delay) to disk,
// for differential lockstep against the ARES oracle with near-zero cumulative drift.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--gen-rom") {
        MicroRoms.Generate(kind: args[index + 1], outPath: args[index + 2]);

        return 0;
    }
}

// --lockstep <rom> <steps>: step Puck against the ARES oracle (ares-cosim) in lockstep; halt at the first
// functional divergence and characterise the per-instruction cycle-delta drift (the M-CYCLE target).
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--lockstep") {
        return RomRunner.Lockstep(romPath: args[index + 1], steps: long.Parse(args[index + 2]), direct: Array.IndexOf(array: args, value: "direct") >= 0);
    }
}

// --iodump <rom> <steps>: dump every I/O register halfword, to diff against ares-cosim's iodump.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--iodump") {
        RomRunner.IoDump(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

        return 0;
    }
}

// --probe <rom> <steps> | --emerald-trace <rom> <loHex> <hiHex> <count> [skip]: blank-screen boot diagnostics.
if (RomRunner.TryDiagnostic(args: args)) {
    return 0;
}

// --trace-cycles <rom> <steps>: per-instruction cycle trace, to diff against the mGBA cosim oracle.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--trace-cycles") {
        RomRunner.TraceCycles(romPath: args[index + 1], steps: long.Parse(args[index + 2]));

        return 0;
    }
}

// --mgba-suite <rom>: run the menu-driven mGBA test suite (mgba-emu/suite) headlessly via the debug-log register.
for (var index = 0; index < args.Length - 1; ++index) {
    if (args[index] == "--mgba-suite") {
        return RomRunner.RunMgbaSuite(romPath: args[index + 1], name: "mGBA suite");
    }
}

// --ags <rom>: run the AGS aging cartridge (TCHK10 dump) headlessly and print the per-test result stream.
for (var index = 0; index < args.Length - 1; ++index) {
    if (args[index] == "--ags") {
        _ = RomRunner.RunAgs(romPath: args[index + 1], name: Path.GetFileName(args[index + 1]));

        return 0;
    }
}

var failures = 0;

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

    // Save-backup conformance (same r12 verdict convention): exercises the SRAM/Flash command protocols and,
    // critically, the Flash 64K/128K state machine + bank switching that commercial saves (e.g. Emerald) need.
    Console.WriteLine("== jsmolka save tests ==");

    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "save", "none.gba"), name: "save/none");
    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "save", "sram.gba"), name: "save/sram");
    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "save", "flash64.gba"), name: "save/flash64");
    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "save", "flash128.gba"), name: "save/flash128");

    // Save persistence: prove the export/import (.sav) round-trip preserves the backup across a fresh cartridge.
    failures += RomRunner.RunSaveRoundtrip();

    // Note: jsmolka's bios.gba is intentionally excluded — it asserts the *official* Nintendo BIOS's exact
    // open-bus read values, which no clean-room replacement reproduces. BIOS IRQ/SWI behaviour is validated by
    // the BiosIrqDispatch smoke test instead.

    // FuzzARM, if cloned alongside gba-tests, adds randomized CPU coverage.
    var fuzzArmRoot = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(assetRoot)) ?? assetRoot, "FuzzARM");

    if (Directory.Exists(fuzzArmRoot)) {
        Console.WriteLine($"== FuzzARM (assets at {fuzzArmRoot}) ==");

        failures += RomRunner.RunFuzzArm(romPath: Path.Combine(fuzzArmRoot, "ARM_Any.gba"), name: "ARM_Any");
        failures += RomRunner.RunFuzzArm(romPath: Path.Combine(fuzzArmRoot, "THUMB_Any.gba"), name: "THUMB_Any");
        failures += RomRunner.RunFuzzArm(romPath: Path.Combine(fuzzArmRoot, "ARM_DataProcessing.gba"), name: "ARM_DataProcessing");
        failures += RomRunner.RunFuzzArm(romPath: Path.Combine(fuzzArmRoot, "THUMB_DataProcessing.gba"), name: "THUMB_DataProcessing");
        failures += RomRunner.RunFuzzArm(romPath: Path.Combine(fuzzArmRoot, "FuzzARM.gba"), name: "FuzzARM");
    }

    // nes (jsmolka): a tiny NES-style CPU/PPU exerciser using the r12 verdict convention.
    Console.WriteLine("== jsmolka misc ==");
    failures += RomRunner.RunJsmolka(romPath: Path.Combine(assetRoot, "nes", "nes.gba"), name: "nes");
    failures += RomRunner.RunRenderHash(romPath: Path.Combine(assetRoot, "ppu", "shades.gba"), name: "ppu/shades", steps: 6_000_000, expected: 0x19E7C5AF1FB0BF25ul);

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
