using Puck.GamingBricks;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>The verdict a <see cref="RegisterSignatureProbe"/> run produced.</summary>
internal enum RegisterSignatureResult {
    /// <summary>The register file held the Fibonacci pass signature.</summary>
    Pass,
    /// <summary>The register file held the <c>0x42</c> failure signature.</summary>
    Fail,
    /// <summary>Neither signature appeared within the frame cap.</summary>
    Inconclusive,
}
/// <summary>
/// Reads a ROM's verdict directly from the register file, for suites that report the mooneye Fibonacci-or-<c>0x42</c>
/// signature (B,C,D,E,H,L = 3,5,8,13,21,34 for pass; all six <c>0x42</c> for fail) but never transmit it over serial —
/// the wilbertpol fork and the older SameSuite ROMs, whose exit convention is to land on an opcode trap
/// (<c>0x40 LD B,B</c>, or an undefined opcode that <see cref="Sm83"/> treats as a permanent lockup) and then idle.
/// The register file is polled through <see cref="ICpu"/> after every frame, but a value that happens to match the
/// signature mid-computation is not evidence the ROM finished — the signature is accepted only once the run has also
/// corroborated the exit itself: either the core has locked up (<see cref="Sm83StateCodec.ReadTail"/> reads that flag
/// back through the CPU's existing <c>SaveState</c> seam), or an <c>0x40 LD B,B</c> has been fetched at an instruction
/// boundary (<see cref="ICpuTraceSink"/>). Once corroborated, the run ends early rather than burning the rest of the
/// frame cap on a machine that is done.
/// </summary>
internal static class RegisterSignatureProbe {
    private const byte FailByte = 0x42;
    private const byte OpcodeTrap = 0x40;

    private sealed class OpcodeTrapSink : ICpuTraceSink {
        private readonly SystemBus m_bus;

        public OpcodeTrapSink(SystemBus bus) =>
            m_bus = bus;

        public bool SawTrap { get; private set; }

        public void OnInstructionBoundary(ushort pc, byte a, byte f, byte b, byte c, byte d, byte e, byte h, byte l, ushort sp) {
            if (
                !SawTrap &&
                (m_bus.DebugReadByte(address: pc) == OpcodeTrap)
            ) {
                SawTrap = true;
            }
        }
    }

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="romCase">The case to run.</param>
    /// <returns>The verdict and a one-line detail.</returns>
    public static (RegisterSignatureResult Result, string Detail) Run(RomCase romCase) {
        var rom = File.ReadAllBytes(path: romCase.FullPath);

        using var machine = PostMachine.Build(
            model: romCase.Model,
            rom: rom
        );

        var cpu = machine.GetRequiredService<ICpu>();
        var sm83 = machine.GetRequiredService<Sm83>();
        var trap = new OpcodeTrapSink(bus: machine.GetRequiredService<SystemBus>());
        var scratch = new StateWriter(capacity: Sm83StateCodec.ByteCount);
        var buffer = new byte[Sm83StateCodec.ByteCount];

        sm83.SetTraceSink(sink: trap);

        for (var frame = 0; (frame < romCase.FrameCap); ++frame) {
            PostMachine.RunFrames(
                frames: 1,
                instance: machine
            );

            Sm83StateCodec.ReadTail(
                buffer: buffer,
                cpu: sm83,
                eiPending: out _,
                halted: out _,
                ime: out _,
                lockedUp: out var lockedUp,
                scratch: scratch
            );
            var corroborated = (lockedUp || trap.SawTrap);
            var corroboration = (lockedUp
                ? "core locked up"
                : "LD B,B trap observed");

            if (corroborated) {
                if (IsPassSignature(cpu: cpu)) {
                    return (RegisterSignatureResult.Pass, $"fib signature after {(frame + 1)} frames ({corroboration})");
                }

                if (IsFailSignature(cpu: cpu)) {
                    return (RegisterSignatureResult.Fail, $"0x42 failure signature after {(frame + 1)} frames ({corroboration})");
                }
            }

            if (lockedUp) {
                return (RegisterSignatureResult.Inconclusive, $"CPU locked up after {(frame + 1)} frames without a corroborated signature ({RegisterDump(cpu: cpu)})");
            }
        }

        return (RegisterSignatureResult.Inconclusive, $"no corroborated signature within {romCase.FrameCap} frames ({RegisterDump(cpu: cpu)})");
    }

    private static bool IsFailSignature(ICpu cpu) =>
        (
            (cpu.B == FailByte) &&
            (cpu.C == FailByte) &&
            (cpu.D == FailByte) &&
            (cpu.E == FailByte) &&
            (cpu.H == FailByte) &&
            (cpu.L == FailByte)
        );
    private static bool IsPassSignature(ICpu cpu) =>
        (
            (cpu.B == 3) &&
            (cpu.C == 5) &&
            (cpu.D == 8) &&
            (cpu.E == 13) &&
            (cpu.H == 21) &&
            (cpu.L == 34)
        );
    private static string RegisterDump(ICpu cpu) =>
        $"b={cpu.B:X2} c={cpu.C:X2} d={cpu.D:X2} e={cpu.E:X2} h={cpu.H:X2} l={cpu.L:X2}";
}
