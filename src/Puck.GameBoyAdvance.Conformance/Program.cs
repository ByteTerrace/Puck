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

// --render <rom> <out.png>: boot a ROM and dump its framebuffer, to eyeball the PPU output.
for (var index = 0; index < args.Length - 2; ++index) {
    if (args[index] == "--render") {
        RomRunner.Render(romPath: args[index + 1], outputPath: args[index + 2]);

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
}

return failures;
