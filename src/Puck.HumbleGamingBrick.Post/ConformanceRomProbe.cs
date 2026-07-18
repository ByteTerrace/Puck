using System.Text;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>The verdict a <see cref="ConformanceRomProbe"/> run produced.</summary>
internal enum ConformanceRomResult {
    /// <summary>The ROM reported success.</summary>
    Pass,
    /// <summary>The ROM reported a failure.</summary>
    Fail,
    /// <summary>The ROM produced no result within its frame cap.</summary>
    Inconclusive,
}

/// <summary>
/// Runs a conformance test ROM and reads its verdict. The primary channel is the serial text the ROM prints (captured through
/// <see cref="SerialComponent.ByteTransmitted"/>), which ends in "Passed" or "Failed"; the fallback is the <c>0xA000</c>
/// memory result block (signature <c>0xDE 0xB0 0x61</c> at <c>0xA001</c>–<c>0xA003</c>, status <c>0xA000</c> = <c>0x80</c>
/// while running then a final code where <c>0x00</c> is pass), read through the machine's own system bus. The machine is
/// advanced one frame at a time and polled after each, exiting as soon as a result appears.
/// </summary>
internal static class ConformanceRomProbe {
    private const byte PassStatus = 0x00;
    private const byte RunningStatus = 0x80;
    private const byte Signature0 = 0xDE;
    private const ushort Signature0Address = 0xA001;
    private const byte Signature1 = 0xB0;
    private const ushort Signature1Address = 0xA002;
    private const byte Signature2 = 0x61;
    private const ushort Signature2Address = 0xA003;
    private const ushort StatusAddress = 0xA000;

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="romCase">The case to run.</param>
    /// <returns>The verdict and a one-line detail.</returns>
    public static (ConformanceRomResult Result, string Detail) Run(RomCase romCase) {
        var rom = File.ReadAllBytes(path: romCase.FullPath);

        using var machine = PostMachine.Build(model: romCase.Model, rom: rom);

        var bus = machine.GetRequiredService<ISystemBus>();
        var serialText = new StringBuilder();

        machine.GetRequiredService<SerialComponent>().ByteTransmitted = value => serialText.Append(value: (char)value);

        var sawRunning = false;

        for (var frame = 0; (frame < romCase.FrameCap); ++frame) {
            PostMachine.RunFrames(instance: machine, frames: 1);

            var rendered = serialText.ToString();

            if (rendered.Contains(value: "Passed", comparisonType: StringComparison.Ordinal)) {
                return (ConformanceRomResult.Pass, Clean(text: rendered));
            }

            if (rendered.Contains(value: "Failed", comparisonType: StringComparison.Ordinal)) {
                return (ConformanceRomResult.Fail, Clean(text: rendered));
            }

            if ((bus.ReadByte(address: Signature0Address) == Signature0)
                && (bus.ReadByte(address: Signature1Address) == Signature1)
                && (bus.ReadByte(address: Signature2Address) == Signature2)) {
                var status = bus.ReadByte(address: StatusAddress);

                if (status == RunningStatus) {
                    sawRunning = true;
                } else if (sawRunning) {
                    return ((status == PassStatus)
                        ? (ConformanceRomResult.Pass, Clean(text: rendered))
                        : (ConformanceRomResult.Fail, $"result code 0x{status:X2}"));
                }
            }
        }

        var final = serialText.ToString();

        if (final.Contains(value: "Passed", comparisonType: StringComparison.Ordinal)) {
            return (ConformanceRomResult.Pass, Clean(text: final));
        }

        if (final.Contains(value: "Failed", comparisonType: StringComparison.Ordinal)) {
            return (ConformanceRomResult.Fail, Clean(text: final));
        }

        return (ConformanceRomResult.Inconclusive, $"no result within {romCase.FrameCap} frames; serial=\"{Clean(text: final)}\"");
    }

    // Collapse the serial stream's control characters and runs of whitespace into a single readable line.
    private static string Clean(string text) {
        var builder = new StringBuilder(capacity: text.Length);

        foreach (var character in text) {
            _ = builder.Append(value: (char.IsControl(c: character) ? ' ' : character));
        }

        return string.Join(separator: ' ', values: builder.ToString().Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries));
    }
}
