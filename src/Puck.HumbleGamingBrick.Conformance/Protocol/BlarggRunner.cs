using System.Text;

namespace Puck.HumbleGamingBrick.Conformance.Protocol;

/// <summary>Runs a ROM that reports via the blargg protocol: ASCII progress over the serial link ending in
/// "Passed"/"Failed", backed by a memory result block at <c>0xA000</c> (signature <c>0xDE 0xB0 0x61</c> at
/// <c>0xA001..0xA003</c>, status <c>0xA000</c> = <c>0x80</c> while running then a final code where 0 is pass). The
/// serial text is the primary channel; the memory block is the fallback.</summary>
internal static class BlarggRunner {
    public static TestOutcome Run(RomCase romCase, Sm83Machine machine) {
        var text = new StringBuilder();

        machine.Bus.Serial.ByteTransmitted = value => text.Append(value: (char)value);

        var bus = machine.Bus;
        var cap = (ulong)romCase.CycleLimit;
        var sawRunning = false;
        var steps = 0L;

        while (bus.ElapsedDots < cap) {
            machine.Step();

            // Polling every step would be quadratic on the serial text; sample periodically.
            if ((++steps & 0x3FFFL) != 0L) {
                continue;
            }

            var rendered = text.ToString();

            if (rendered.Contains(value: "Passed", comparisonType: StringComparison.Ordinal)) {
                return new(Case: romCase, Status: TestStatus.Pass, Detail: Clean(text: rendered));
            }

            if (rendered.Contains(value: "Failed", comparisonType: StringComparison.Ordinal)) {
                return new(Case: romCase, Status: TestStatus.Fail, Detail: Clean(text: rendered));
            }

            if ((bus.ReadByte(address: 0xA001) == 0xDE) && (bus.ReadByte(address: 0xA002) == 0xB0) && (bus.ReadByte(address: 0xA003) == 0x61)) {
                var status = bus.ReadByte(address: 0xA000);

                if (status == 0x80) {
                    sawRunning = true;
                }
                else if (sawRunning) {
                    return (status == 0x00)
                        ? new(Case: romCase, Status: TestStatus.Pass, Detail: Clean(text: rendered))
                        : new(Case: romCase, Status: TestStatus.Fail, Detail: FormattableString.Invariant($"result code 0x{status:X2}; {Clean(text: rendered)}"));
                }
            }
        }

        var final = text.ToString();

        if (final.Contains(value: "Passed", comparisonType: StringComparison.Ordinal)) {
            return new(Case: romCase, Status: TestStatus.Pass, Detail: Clean(text: final));
        }

        if (final.Contains(value: "Failed", comparisonType: StringComparison.Ordinal)) {
            return new(Case: romCase, Status: TestStatus.Fail, Detail: Clean(text: final));
        }

        return new(Case: romCase, Status: TestStatus.Inconclusive, Detail: FormattableString.Invariant($"no result within cycle cap; serial = \"{Clean(text: final)}\""));
    }

    // Collapse the serial stream's control characters and whitespace into a single readable line.
    private static string Clean(string text) {
        var builder = new StringBuilder(capacity: text.Length);

        foreach (var character in text) {
            builder.Append(value: char.IsControl(c: character) ? ' ' : character);
        }

        return string.Join(separator: ' ', values: builder.ToString().Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
