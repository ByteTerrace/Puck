using System.Globalization;

namespace Puck.HumbleGamingBrick.Conformance.Protocol;

/// <summary>Runs a ROM that reports via the mooneye protocol (also used by same-suite and the auto age tests):
/// a pass loads the Fibonacci numbers 3/5/8/13/21/34 into B/C/D/E/H/L, a fail loads 0x42 into all six; both then
/// execute the <c>LD B,B</c> (0x40) debug breakpoint and emit the same bytes over the serial link. The runner stops
/// at the breakpoint (or after six serial bytes) and reads the verdict from the registers, with serial as a
/// fallback.</summary>
internal static class MooneyeRunner {
    private static readonly byte[] PassSerial = [3, 5, 8, 13, 21, 34];

    public static TestOutcome Run(RomCase romCase, Sm83Machine machine) {
        var serial = new List<byte>(capacity: 8);

        machine.Bus.Serial.ByteTransmitted = value => {
            if (serial.Count < 64) {
                serial.Add(item: value);
            }
        };

        var cpu = machine.Cpu;
        var bus = machine.Bus;
        var cap = (ulong)romCase.CycleLimit;
        var instructions = 0L;

        while (bus.ElapsedDots < cap) {
            // The LD B,B breakpoint marks the result point — registers are already set when it is reached. Guard a
            // handful of leading instructions so an incidental early 0x40 cannot be mistaken for the terminal.
            if ((instructions > 16L) && (bus.ReadByte(address: cpu.ProgramCounter) == 0x40)) {
                break;
            }

            machine.Step();
            instructions += 1L;

            if (serial.Count >= 6) {
                break;
            }
        }

        var registersPass = (cpu.B == 3) && (cpu.C == 5) && (cpu.D == 8) && (cpu.E == 13) && (cpu.H == 21) && (cpu.L == 34);
        var registersFail = (cpu.B == 0x42) && (cpu.C == 0x42) && (cpu.D == 0x42) && (cpu.E == 0x42) && (cpu.H == 0x42) && (cpu.L == 0x42);
        var serialPass = (serial.Count >= 6) && serial.Take(count: 6).SequenceEqual(second: PassSerial);
        var serialFail = (serial.Count >= 6) && serial.Take(count: 6).All(predicate: static b => b == 0x42);

        if (registersPass || serialPass) {
            return new(Case: romCase, Status: TestStatus.Pass, Detail: "registers = 3/5/8/13/21/34");
        }

        if (registersFail || serialFail) {
            return new(Case: romCase, Status: TestStatus.Fail, Detail: DescribeRegisters(cpu: cpu));
        }

        return new TestOutcome(
            Case: romCase,
            Status: TestStatus.Inconclusive,
            Detail: FormattableString.Invariant($"no result signal; {DescribeRegisters(cpu: cpu)}; serial bytes = {serial.Count}")
        );
    }

    private static string DescribeRegisters(Interfaces.ICpu cpu) =>
        string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"B={cpu.B:X2} C={cpu.C:X2} D={cpu.D:X2} E={cpu.E:X2} H={cpu.H:X2} L={cpu.L:X2}"
        );
}
