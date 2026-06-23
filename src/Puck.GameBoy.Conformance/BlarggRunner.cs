using System.Text;

namespace Puck.GameBoy.Conformance;

/// <summary>
/// Runs Blargg's test ROMs, which report their results over the serial port (and on screen). The runner captures
/// the serial byte stream and waits for the terminal "Passed" / "Failed" marker, then prints the verdict. A single
/// ROM file or a directory of ROMs may be given.
/// </summary>
internal static class BlarggRunner {
    // Blargg ROMs finish in well under this many cycles; the ceiling only guards against a hung ROM.
    private static readonly long CycleCeiling = (long.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_BLARGG_CEILING"), out var ceiling) ? ceiling : 80_000_000L);

    public static int Run(string path, TextWriter output) {
        if (Directory.Exists(path: path)) {
            var roms = Directory.GetFiles(path: path, searchPattern: "*.gb");

            Array.Sort(array: roms, comparer: StringComparer.OrdinalIgnoreCase);

            var failures = 0;

            output.WriteLine(value: $"== Blargg ({path}) ==");

            foreach (var rom in roms) {
                if (!RunOne(romPath: rom, output: output)) {
                    failures += 1;
                }
            }

            output.WriteLine(value: $"blargg: {roms.Length - failures}/{roms.Length} passed");

            return ((failures == 0) ? 0 : 1);
        }

        if (File.Exists(path: path)) {
            return (RunOne(romPath: path, output: output) ? 0 : 1);
        }

        output.WriteLine(value: $"blargg: path not found: {path}");

        return 2;
    }

    private static bool RunOne(string romPath, TextWriter output) {
        var rom = File.ReadAllBytes(path: romPath);

        // Pick the console the suite was written for. Only CGB-ONLY ROMs (header 0x143 = 0xC0, e.g. cgb_sound and
        // interrupt_time) run on the CGB; CGB-enhanced ROMs (0x80, e.g. oam_bug, halt_bug, cpu_instrs) are testing
        // DMG-compatible behavior and must run on the DMG — the OAM corruption bug, in particular, exists only there.
        // (Games keep the usual 0x80 -> CGB mapping in GameRunner; this DMG bias is specific to the test ROMs.)
        var model = (((rom.Length > 0x143) && ((rom[0x143] & 0xC0) == 0xC0) && (Environment.GetEnvironmentVariable(variable: "PUCK_BLARGG_FORCE_DMG") is null))
            ? ConsoleModel.Cgb
            : ConsoleModel.Dmg);
        var machine = new GameBoyMachine(
            cartridge: Cartridge.Load(rom: rom),
            model: model
        );

        // Blargg ROMs report results one of two ways: older suites print over the serial port, while the newer
        // ones (e.g. dmg_sound) write to cartridge RAM — a 0xDE 0xB0 0x61 signature at 0xA001-0xA003, the result
        // code at 0xA000 (0x80 while running, then 0 = passed / nonzero = failure code), and the text at 0xA004.
        var serial = new StringBuilder();
        var finished = false;

        machine.Bus.Serial.ByteTransmitted = value => {
            _ = serial.Append(value: (char)value);

            if ((serial.Length >= 6) && (EndsWith(builder: serial, suffix: "Passed") || Contains(builder: serial, fragment: "Failed"))) {
                finished = true;
            }
        };

        var resultCode = -1;
        string? screenResult = null;

        var histogram = ((Environment.GetEnvironmentVariable(variable: "PUCK_BLARGG_PCHIST") is not null) ? new Dictionary<ushort, long>() : null);

        for (var step = 0L; (step < CycleCeiling) && !finished; step += 1) {
            if ((histogram is not null) && ((step & 0x3F) == 0)) {
                var pc = machine.Cpu.ProgramCounter;

                histogram[pc] = (histogram.GetValueOrDefault(key: pc) + 1);
            }

            machine.Step();

            // Poll the result periodically (cheap relative to the run): some ROMs write it to cartridge RAM, while the
            // oldest (halt_bug, interrupt_time) only print it on screen, so also scan the background tile map.
            if ((step & 0x1FFF) == 0) {
                if (HasMemoryResult(machine: machine, out var code)) {
                    resultCode = code;
                    finished = true;
                }
                else if ((screenResult = ReadScreenResult(machine: machine)) is not null) {
                    finished = true;
                }
            }
        }

        if (histogram is not null) {
            output.WriteLine(value: "  -- PC histogram (top 12 by sampled occupancy) --");

            foreach (var entry in histogram.OrderByDescending(keySelector: e => e.Value).Take(count: 12)) {
                output.WriteLine(value: $"    PC=0x{entry.Key:X4}  {entry.Value}");
            }
        }

        if (Environment.GetEnvironmentVariable(variable: "PUCK_BLARGG_SCREENDUMP") is not null) {
            DumpScreenTiles(machine: machine, output: output);
        }

        var serialText = serial.ToString().Replace(oldChar: '\n', newChar: ' ').Trim();
        var memoryText = ReadMemoryText(machine: machine);
        var passed = (finished && ((resultCode == 0) || ((resultCode < 0) && (Contains(builder: serial, fragment: "Passed") || (screenResult == "Passed")))));
        var status = (finished ? (passed ? "PASS" : "FAIL") : "TIMEOUT");
        var fullText = ((memoryText.Length > 0) ? memoryText : ((serialText.Length > 0) ? serialText : (screenResult is null ? "" : $"(on screen) {screenResult}")));
        var text = ((fullText.Length > 90) ? (fullText[..90] + "…") : fullText);
        var diagnostic = (finished
            ? (((resultCode > 0) ? $"#{resultCode} " : "") + text)
            : $"stuck at PC=0x{machine.Cpu.ProgramCounter:X4} [{text}]");

        output.WriteLine(value: $"  {status}  {Path.GetFileName(path: romPath)}: {diagnostic}");

        return passed;
    }

    // Reconstructs the on-screen text from the background tile maps (Blargg's font stores each glyph at the tile index
    // equal to its ASCII code, so a tile byte IS its character) and reports the test's "Passed"/"Failed" verdict.
    private static string? ReadScreenResult(GameBoyMachine machine) {
        var vram = machine.Bus.VideoRam;

        // Either background map may be in use; scan both (each is 32x32 tile indices at VRAM offset 0x1800 / 0x1C00).
        for (var map = 0x1800; map <= 0x1C00; map += 0x400) {
            if ((map + (32 * 32)) > vram.Length) {
                continue;
            }

            for (var row = 0; row < 32; row += 1) {
                var line = new StringBuilder(capacity: 32);

                for (var col = 0; col < 32; col += 1) {
                    var tile = vram[map + (row * 32) + col];

                    _ = line.Append(value: (((tile >= 0x20) && (tile < 0x7F)) ? (char)tile : ' '));
                }

                var text = line.ToString();

                if (text.Contains(value: "Passed", comparisonType: StringComparison.Ordinal)) {
                    return "Passed";
                }

                if (text.Contains(value: "Failed", comparisonType: StringComparison.Ordinal)) {
                    return "Failed";
                }
            }
        }

        return null;
    }

    private static void DumpScreenTiles(GameBoyMachine machine, TextWriter output) {
        var vram = machine.Bus.VideoRam;

        for (var map = 0x1800; map <= 0x1C00; map += 0x400) {
            output.WriteLine(value: $"  -- tile map 0x{(0x8000 + map):X4} (raw tile indices, first 6 rows x 20 cols) --");

            for (var row = 0; row < 6; row += 1) {
                var line = new StringBuilder();

                for (var col = 0; col < 20; col += 1) {
                    _ = line.Append(value: $"{vram[map + (row * 32) + col]:X2} ");
                }

                output.WriteLine(value: $"  {line}");
            }
        }
    }

    // The cartridge-RAM result is present once the signature is written and the code has left its 0x80 "running" state.
    private static bool HasMemoryResult(GameBoyMachine machine, out int code) {
        code = -1;

        var bus = machine.Bus;
        var hasSignature =
            (bus.ReadByte(address: 0xA001) == 0xDE) &&
            (bus.ReadByte(address: 0xA002) == 0xB0) &&
            (bus.ReadByte(address: 0xA003) == 0x61);

        if (!hasSignature) {
            return false;
        }

        var value = bus.ReadByte(address: 0xA000);

        if (value == 0x80) {
            return false;
        }

        code = value;

        return true;
    }
    private static string ReadMemoryText(GameBoyMachine machine) {
        var bus = machine.Bus;

        if ((bus.ReadByte(address: 0xA001) != 0xDE) || (bus.ReadByte(address: 0xA002) != 0xB0)) {
            return string.Empty;
        }

        var text = new StringBuilder();

        for (var address = 0xA004; address < 0xB000; address += 1) {
            var character = bus.ReadByte(address: (ushort)address);

            if (character == 0) {
                break;
            }

            _ = text.Append(value: ((character >= 0x20) && (character < 0x7F)) ? (char)character : ' ');
        }

        return text.ToString().Replace(oldChar: '\n', newChar: ' ').Trim();
    }

    private static bool EndsWith(StringBuilder builder, string suffix) {
        if (builder.Length < suffix.Length) {
            return false;
        }

        var offset = (builder.Length - suffix.Length);

        for (var index = 0; index < suffix.Length; index += 1) {
            if (builder[offset + index] != suffix[index]) {
                return false;
            }
        }

        return true;
    }
    private static bool Contains(StringBuilder builder, string fragment) {
        for (var start = 0; start <= (builder.Length - fragment.Length); start += 1) {
            var matched = true;

            for (var index = 0; index < fragment.Length; index += 1) {
                if (builder[start + index] != fragment[index]) {
                    matched = false;

                    break;
                }
            }

            if (matched) {
                return true;
            }
        }

        return false;
    }
}
