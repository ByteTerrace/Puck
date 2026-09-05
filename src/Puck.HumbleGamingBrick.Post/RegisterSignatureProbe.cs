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
/// The register file is polled through <see cref="ICpu"/> after every frame, so the signature is caught the frame it
/// is written regardless of which trap the ROM lands on; <see cref="Sm83StateCodec.ReadTail"/> reads the CPU's
/// lockup flag back through its existing <c>SaveState</c> seam to end the run early once the core can no longer make
/// forward progress, rather than burning the rest of the frame cap on a machine that is done.
/// </summary>
internal static class RegisterSignatureProbe {
    private const byte FailByte = 0x42;

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
        var scratch = new StateWriter(capacity: Sm83StateCodec.ByteCount);
        var buffer = new byte[Sm83StateCodec.ByteCount];

        for (var frame = 0; (frame < romCase.FrameCap); ++frame) {
            PostMachine.RunFrames(
                frames: 1,
                instance: machine
            );

            if (IsPassSignature(cpu: cpu)) {
                return (RegisterSignatureResult.Pass, $"fib signature after {(frame + 1)} frames");
            }

            if (IsFailSignature(cpu: cpu)) {
                return (RegisterSignatureResult.Fail, $"0x42 failure signature after {(frame + 1)} frames");
            }

            Sm83StateCodec.ReadTail(
                buffer: buffer,
                cpu: sm83,
                eiPending: out _,
                halted: out _,
                ime: out _,
                lockedUp: out var lockedUp,
                scratch: scratch
            );

            if (lockedUp) {
                return (RegisterSignatureResult.Inconclusive, $"CPU locked up after {(frame + 1)} frames without a signature ({RegisterDump(cpu: cpu)})");
            }
        }

        return (RegisterSignatureResult.Inconclusive, $"no signature within {romCase.FrameCap} frames ({RegisterDump(cpu: cpu)})");
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
