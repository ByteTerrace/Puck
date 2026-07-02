using System.Runtime.InteropServices;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Boots a ROM, runs it for a fixed number of instructions, and hashes the resulting framebuffer (FNV-1a 64-bit). Used as
/// a deterministic visual-regression floor: the core is fully deterministic, so a known-good render must reproduce its
/// hash exactly, and a mismatch flags an unintended change to the CPU/bus/PPU/timing pipeline. Passing <c>0</c> for the
/// expected hash puts the probe in capture mode (it reports the actual hash for recording a new floor), which the
/// <c>--render-hash</c> diagnostic uses.
/// </summary>
internal static class RenderHashProbe {
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325ul;
    private const ulong FnvPrime = 0x100000001B3ul;

    /// <summary>Runs a ROM and hashes its framebuffer.</summary>
    /// <param name="romPath">The ROM path.</param>
    /// <param name="steps">The number of instructions to step before hashing.</param>
    /// <param name="expected">The expected hash, or <c>0</c> for capture mode.</param>
    /// <param name="bios">The BIOS image to boot with.</param>
    /// <returns><see langword="true"/> when the hash matches the floor, <see langword="false"/> when it diverges, or
    /// <see langword="null"/> in capture mode; paired with the actual hash and a one-line detail.</returns>
    public static (bool? Pass, ulong Hash, string Detail) Run(string romPath, long steps, ulong expected, ReadOnlyMemory<byte> bios) {
        var rom = File.ReadAllBytes(path: romPath);

        using var machine = PostMachine.Build(bios: bios, rom: rom);

        for (long i = 0; (i < steps); ++i) {
            machine.Machine.Step();
        }

        var bytes = MemoryMarshal.AsBytes(span: machine.Machine.Framebuffer);
        var hash = FnvOffsetBasis;

        foreach (var value in bytes) {
            hash = ((hash ^ value) * FnvPrime);
        }

        if (expected == 0ul) {
            return (null, hash, $"0x{hash:X16} (capture)");
        }

        return (hash == expected)
            ? (true, hash, "frame hash matches floor")
            : (false, hash, $"frame hash 0x{hash:X16} != floor 0x{expected:X16}");
    }
}
