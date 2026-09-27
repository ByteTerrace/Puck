namespace Puck.HumbleGamingDeck.Post;

/// <summary>Checks that the host persists declared PRG and CHR NVRAM while leaving volatile board memory out of the save.</summary>
internal static class BatterySaveProbe {
    /// <summary>Round-trips a board with separate 4 KiB volatile and nonvolatile halves in both CPU and PPU memory.</summary>
    /// <param name="artifactsDirectory">The directory for the temporary save, removed after verification.</param>
    /// <returns>The failure outcome, or <see langword="null"/> when the saved bytes and reloaded memory agree.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifactsDirectory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is empty or invalid.</exception>
    /// <exception cref="NotSupportedException"><paramref name="artifactsDirectory"/> uses an unsupported path format.</exception>
    /// <exception cref="IOException">The temporary directory or save cannot be created, read, or removed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the temporary directory or save is denied.</exception>
    public static PostStageOutcome? Verify(string artifactsDirectory) {
        Directory.CreateDirectory(path: artifactsDirectory);
        var path = Path.Combine(path1: artifactsDirectory, path2: $"battery-{Guid.NewGuid():N}.sav");
        var image = PostMachine.CreateImage();

        image[6] = 2;
        image[10] = 0x66;
        image[11] = 0x66;
        try {
            using (var core = new HumbleGamingDeckCore(cartridgeImage: image, savePath: path)) {
                var machine = core.Machine;

                for (var offset = 0; (offset < 8192); ++offset) {
                    machine.Bus.Write(address: ((ushort)(0x6000 + offset)), value: PrgByte(offset: offset));
                    machine.Bus.Mapper.PpuWrite(address: ((ushort)offset), nametables: machine.Nametables, value: ChrByte(offset: offset));
                }
                core.FlushSave(force: false);
            }

            // NES 2.0 byte 10 and byte 11 each declare 4 KiB of NVRAM. The save concatenates those two regions.
            // https://www.nesdev.org/wiki/NES_2.0
            var saved = File.ReadAllBytes(path: path);

            if (saved.Length != 8192) {
                return PostStageOutcome.Fail(detail: $"battery save holds {saved.Length} bytes; expected 4096 PRG NVRAM bytes and 4096 CHR NVRAM bytes");
            }
            for (var offset = 0; (offset < 4096); ++offset) {
                if ((saved[offset] != PrgByte(offset: (4096 + offset))) || (saved[(4096 + offset)] != ChrByte(offset: (4096 + offset)))) {
                    return PostStageOutcome.Fail(detail: $"battery save differs at NVRAM offset {offset}; volatile memory must not enter the save");
                }
            }

            using var restored = new HumbleGamingDeckCore(cartridgeImage: image, savePath: path);
            var restoredMachine = restored.Machine;

            for (var offset = 0; (offset < 8192); ++offset) {
                var expectedPrg = ((offset < 4096) ? ((byte)0) : PrgByte(offset: offset));
                var expectedChr = ((offset < 4096) ? ((byte)0) : ChrByte(offset: offset));

                if ((restoredMachine.Bus.Peek(address: ((ushort)(0x6000 + offset))) != expectedPrg) ||
                    (restoredMachine.Bus.Mapper.PpuPeek(address: ((ushort)offset), nametables: restoredMachine.Nametables) != expectedChr)) {
                    return PostStageOutcome.Fail(detail: $"battery reload differs at board offset {offset}; only declared nonvolatile memory must survive");
                }
            }

            return null;
        } finally {
            File.Delete(path: path);
        }
    }

    private static byte PrgByte(int offset) =>
        ((byte)((offset * 17) ^ (offset >> 8) ^ 0x5A));
    private static byte ChrByte(int offset) =>
        ((byte)((offset * 29) ^ (offset >> 8) ^ 0xA5));
}
