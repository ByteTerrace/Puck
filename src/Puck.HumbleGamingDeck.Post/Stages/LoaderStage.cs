namespace Puck.HumbleGamingDeck.Post;

/// <summary>Checks header resolution, memory mirroring, and unowned register windows.</summary>
internal sealed class LoaderStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "loader-headers-bus";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var image = PostMachine.CreateImage();
        var cartridge = HgdCartridge.Load(image: image);
        var mapper = cartridge.CreateMapper();
        var bus = new HgdSystemBus(mapper: mapper, workRamFill: 0xA5);

        bus.Write(address: 0x0012, value: 0x43);
        if ((bus.Read(address: 0x1812) != 0x43) || (bus.Read(address: 0x2002) != 0x43)) {
            return PostStageOutcome.Fail(detail: "work-RAM mirror or undriven PPU window differs");
        }
        bus.Write(address: 0x4015, value: 0x72);
        if ((bus.Read(address: 0x4016) != 0x72) || (bus.Peek(address: 0x8000) != bus.Peek(address: 0xC000)) || (bus.OpenBus != 0x72)) {
            return PostStageOutcome.Fail(detail: "open-bus write, side-effect-free peek, or NROM-128 mirroring differs");
        }
        bus.Write(address: 0x6000, value: 0x99);
        mapper.PpuWrite(address: 0x0110, value: 0x28);
        if ((bus.Read(address: 0x6000) != 0x99) || (mapper.PpuRead(address: 0x0110) != 0x28)) {
            return PostStageOutcome.Fail(detail: "NROM PRG/CHR writable memory differs");
        }
        image[4] = (14 << 2);
        image[5] = (13 << 2);
        image[9] = 0xFF;
        var exponent = HgdCartridgeHeader.Parse(header: image);

        if ((exponent.PrgRomSize != 16384) || (exponent.ChrRomSize != 8192)) {
            return PostStageOutcome.Fail(detail: "NES 2.0 exponent sizes differ");
        }
        image[7] = 0;
        image[9] = 0;
        image[10] = 0;
        image[11] = 0;
        image[12] = 0x44;
        try {
            _ = HgdCartridgeHeader.Parse(header: image);

            return PostStageOutcome.Fail(detail: "damaged iNES reserved bytes were accepted");
        } catch (InvalidDataException exception) when (exception.Message.Contains(comparisonType: StringComparison.Ordinal, value: "Ambiguous")) {
        }
        image = PostMachine.CreateImage();
        image[6] = 0x10;
        try {
            _ = HgdCartridge.Load(image: image);

            return PostStageOutcome.Fail(detail: "unimplemented MMC1 was accepted");
        } catch (NotSupportedException exception) when (exception.Message.Contains(comparisonType: StringComparison.Ordinal, value: "MMC1")) {
        }

        return PostStageOutcome.Pass(detail: "NES 2.0 exponent sizes, iNES diagnostic, named board refusal, RAM mirrors, register open bus, NROM PRG/CHR memory");
    }
}
