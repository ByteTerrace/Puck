using System.Text;

namespace Puck.GameBoy.Conformance;

/// <summary>
/// Runs Blargg's test ROMs, which report their results over the serial port (and on screen). The runner captures
/// the serial byte stream and waits for the terminal "Passed" / "Failed" marker, then prints the verdict. A single
/// ROM file or a directory of ROMs may be given.
/// </summary>
internal static class BlarggRunner {
    // Blargg ROMs finish in well under this many cycles; the ceiling only guards against a hung ROM.
    private const long CycleCeiling = 80_000_000L;

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
        var machine = new GameBoyMachine(
            cartridge: Cartridge.Load(rom: File.ReadAllBytes(path: romPath)),
            model: ConsoleModel.Dmg
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

        for (var step = 0L; (step < CycleCeiling) && !finished; step += 1) {
            machine.Step();

            // Poll the memory-mapped result periodically (cheap relative to the run).
            if ((step & 0x1FFF) == 0) {
                if (HasMemoryResult(machine: machine, out var code)) {
                    resultCode = code;
                    finished = true;
                }
            }
        }

        var serialText = serial.ToString().Replace(oldChar: '\n', newChar: ' ').Trim();
        var memoryText = ReadMemoryText(machine: machine);
        var passed = (finished && ((resultCode == 0) || ((resultCode < 0) && Contains(builder: serial, fragment: "Passed"))));
        var status = (finished ? (passed ? "PASS" : "FAIL") : "TIMEOUT");
        var fullText = ((memoryText.Length > 0) ? memoryText : serialText);
        var text = ((fullText.Length > 90) ? (fullText[..90] + "…") : fullText);
        var diagnostic = (finished
            ? (((resultCode > 0) ? $"#{resultCode} " : "") + text)
            : $"stuck at PC=0x{machine.Cpu.ProgramCounter:X4} [{text}]");

        output.WriteLine(value: $"  {status}  {Path.GetFileName(path: romPath)}: {diagnostic}");

        return passed;
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
