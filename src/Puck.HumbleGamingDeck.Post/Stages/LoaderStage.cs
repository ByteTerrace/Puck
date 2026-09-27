namespace Puck.HumbleGamingDeck.Post;

/// <summary>Checks header resolution, memory mirroring, and the CPU open bus behind undecoded and partly driven
/// addresses.</summary>
internal sealed class LoaderStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "loader-headers-bus";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var image = PostMachine.CreateImage();
        var cartridge = HgdCartridge.Load(image: image);
        var machine = new HgdMachine(configuration: new HgdMachineConfiguration(
            cartridge: cartridge,
            powerOn: new HgdPowerOnProfile(workRamFill: 0xA5)
        ));
        var bus = machine.Bus;
        var mapper = bus.Mapper;

        bus.Write(address: 0x0012, value: 0x43);
        if ((bus.Read(address: 0x1812) != 0x43) || (bus.Read(address: 0x4018) != 0x43)) {
            return PostStageOutcome.Fail(detail: "work-RAM mirror or the undecoded $4018 window differs");
        }
        bus.Write(address: 0x4018, value: 0x72);
        if (((bus.Read(address: 0x4016) & 0xE0) != 0x60) || (bus.Peek(address: 0x8000) != bus.Peek(address: 0xC000))) {
            return PostStageOutcome.Fail(detail: "controller open-bus bits, side-effect-free peek, or NROM-128 mirroring differs");
        }
        bus.Write(address: 0x6000, value: 0x99);
        mapper.PpuWrite(address: 0x0110, value: 0x28, nametables: machine.Nametables);
        if ((bus.Read(address: 0x6000) != 0x99) || (mapper.PpuRead(address: 0x0110, nametables: machine.Nametables) != 0x28)) {
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

        return (CheckObjectMemoryPeek() ?? (CheckOverflowReads() ?? PostStageOutcome.Pass(detail: "NES 2.0 exponent sizes, iNES diagnostic, named board refusal, RAM mirrors, register open bus, OAM register peeks and overflow reads, NROM PRG/CHR memory")));
    }

    private static PostStageOutcome? CheckObjectMemoryPeek() {
        using var instance = PostMachine.Build();
        var ppu = instance.Machine.Ppu;

        for (var index = 0; (index < 256); ++index) {
            ppu.WriteRegister(register: 4, value: 0xFF);
        }
        ppu.WriteRegister(register: 3, value: 0);
        ppu.WriteRegister(register: 4, value: 0);
        ppu.WriteRegister(register: 3, value: 2);
        ppu.WriteRegister(register: 4, value: 0xFF);
        ppu.WriteRegister(register: 3, value: 2);
        if ((ppu.PeekRegister(register: 4) != 0xE3) || (ppu.ReadRegister(register: 4) != 0xE3)) {
            return PostStageOutcome.Fail(detail: "$2004 peek/read must mask the unimplemented attribute bits to $E3");
        }
        for (var dot = 0; (dot < ((261 * 341) + 2)); ++dot) {
            ppu.StepDot(masterTick: ((((ulong)dot) + 1) * 4));
        }
        ppu.WriteRegister(register: 1, value: 0x18);
        for (var dot = 0; (dot < 341); ++dot) {
            ppu.StepDot(masterTick: ((((ulong)dot) + ((261 * 341) + 3)) * 4));
        }

        // Secondary OAM clearing drives $FF on dots 1-64, independently of the primary OAM address.
        // https://www.nesdev.org/wiki/PPU_sprite_evaluation
        if ((ppu.PeekRegister(register: 4) != 0xFF) || (ppu.ReadRegister(register: 4) != 0xFF)) {
            return PostStageOutcome.Fail(detail: "$2004 peek/read must expose the object-memory bus during rendering");
        }
        for (var dot = 0; (dot < 320); ++dot) {
            ppu.StepDot(masterTick: ((((ulong)dot) + ((262 * 341) + 3)) * 4));
        }
        if ((ppu.PeekRegister(register: 4) != 0) || (ppu.ReadRegister(register: 4) != 0)) {
            return PostStageOutcome.Fail(detail: "$2004 must expose the first secondary OAM byte during background prefetch");
        }

        return null;
    }
    private static PostStageOutcome? CheckOverflowReads() {
        using var instance = PostMachine.Build();
        var ppu = instance.Machine.Ppu;
        var tick = 0UL;

        for (var index = 0; (index < 256); ++index) {
            ppu.WriteRegister(register: 4, value: 0xFF);
        }
        for (var sprite = 0; (sprite < 9); ++sprite) {
            ppu.WriteRegister(register: 3, value: ((byte)(sprite * 4)));
            ppu.WriteRegister(register: 4, value: 0);
            ppu.WriteRegister(register: 4, value: ((byte)(0x20 + sprite)));
            ppu.WriteRegister(register: 4, value: 3);
            ppu.WriteRegister(register: 4, value: ((byte)(0x40 + sprite)));
        }
        for (var dot = 0; (dot < ((261 * 341) + 2)); ++dot) {
            ppu.StepDot(masterTick: (tick += 4));
        }
        ppu.WriteRegister(register: 1, value: 0x18);
        for (var dot = 0; (dot < (339 + 129)); ++dot) {
            ppu.StepDot(masterTick: (tick += 4));
        }

        // Eight sprites consume dots 65-128. The ninth sets overflow on dot 130; its remaining three bytes are still
        // read before evaluation resumes at later Y bytes. Full secondary OAM drives its first Y byte on even dots.
        // https://www.nesdev.org/wiki/PPU_sprite_evaluation
        ReadOnlySpan<byte> expected = [0, 0, 0x28, 0, 3, 0, 0x48, 0, 0xFF, 0];

        for (var index = 0; (index < expected.Length); ++index) {
            ppu.StepDot(masterTick: (tick += 4));
            var actual = ppu.ReadRegister(register: 4);

            if ((actual != expected[index]) || (((ppu.PeekRegister(register: 2) & 0x20) != 0) != (index >= 1))) {
                return PostStageOutcome.Fail(detail: $"sprite overflow dot {(129 + index)}: OAM bus ${actual:X2}, expected ${expected[index]:X2}, with overflow set from dot 130");
            }
        }

        return null;
    }
}
