using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: the 128-bit win-condition REGION is exactly what the engine's win check reads — the top 16 bytes of the
/// highest SRAM address of an MBC5 128&#160;KiB cartridge, read bank-independently and side-effect-free. On such a cart the
/// region is physical <c>0x1FFF0</c>–<c>0x1FFFF</c> = RAM bank <c>0x0F</c> offset <c>0x1FF0</c>, seen in the CPU window at
/// <c>0xBFF0</c>–<c>0xBFFF</c> when bank <c>0x0F</c> is paged. This stage writes a recognizable 128-bit value there through
/// the real MBC5 register protocol and proves:
/// <list type="bullet">
///   <item><see cref="ICartridge.ReadExternalRam"/> at <c>ExternalRamByteCount - 16</c> returns those bytes, and so does the
///   tail of <see cref="ICartridge.ExportExternalRam"/> — the host's two read paths agree on the highest address.</item>
///   <item>the CPU-window read at <c>0xBFF0</c> matches only while bank <c>0x0F</c> is paged, yet <c>ReadExternalRam</c> is
///   UNAFFECTED by re-paging — so the host poll never fights the running game over bank selection.</item>
///   <item>the region round-trips through a battery save into a fresh machine (the persistence the demo's <c>.sav</c> stands on).</item>
/// </list>
/// The gate MATH (order-independent convergence, subset-proof meta XOR) is proven separately by the engine battery's
/// <c>victory-gate</c> stage; this is the hardware-mapping half.
/// </summary>
internal sealed class VictoryRegionStage : IPostStage {
    private const int Expected128KiB = 0x2_0000;
    private const int RegionByteCount = 16;

    // The "one" v4 GUID (ffffffff-ffff-4fff-bfff-ffffffffffff) in canonical big-endian byte order — mirrors
    // Puck.Scene.VictoryConstants.OneV4Guid, hardcoded here because the emulator battery does not reference Puck.Scene.
    private static readonly byte[] Region = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x4F, 0xFF, 0xBF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <inheritdoc/>
    public string Name =>
        "victory-region";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create(cartridgeType: 0x1B, ramSize: 0x04); // MBC5+RAM+BATTERY, 128 KiB RAM

        using var machine = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);

        var cartridge = machine.GetRequiredService<ICartridge>();

        if (cartridge.ExternalRamByteCount != Expected128KiB) {
            return PostStageOutcome.Fail(detail: $"the synthetic MBC5 header decoded {cartridge.ExternalRamByteCount} bytes of RAM (expected 128 KiB = {Expected128KiB}); the highest-address region needs the full 16-bank chip");
        }

        var topOffset = (cartridge.ExternalRamByteCount - RegionByteCount); // 0x1FFF0

        // Write the region through the real MBC5 protocol: enable RAM (0x0A to 0x0000–0x1FFF), select the HIGHEST bank
        // 0x0F (to 0x4000–0x5FFF), then store into the top of the window 0xBFF0–0xBFFF.
        cartridge.WriteControl(address: 0x0000, value: 0x0A);
        cartridge.WriteControl(address: 0x4000, value: 0x0F);

        for (var index = 0; (index < RegionByteCount); index++) {
            cartridge.WriteRam(address: (ushort)(0xBFF0 + index), value: Region[index]);
        }

        // ReadExternalRam at the top offset returns the region.
        var read = new byte[RegionByteCount];

        cartridge.ReadExternalRam(offset: topOffset, destination: read);

        if (!read.AsSpan().SequenceEqual(other: Region)) {
            return PostStageOutcome.Fail(detail: "ReadExternalRam at ExternalRamByteCount-16 did not return the bytes written to the top of bank 0x0F — the region is not the highest SRAM address");
        }

        // The tail of ExportExternalRam agrees (the two host read paths must not disagree on the highest address).
        var exported = cartridge.ExportExternalRam();

        if (!exported.AsSpan(start: topOffset).SequenceEqual(other: Region)) {
            return PostStageOutcome.Fail(detail: "the tail of ExportExternalRam disagrees with ReadExternalRam at the top offset");
        }

        // The CPU-window read matches while bank 0x0F is paged.
        for (var index = 0; (index < RegionByteCount); index++) {
            var observed = cartridge.ReadRam(address: (ushort)(0xBFF0 + index));

            if (observed != Region[index]) {
                return PostStageOutcome.Fail(detail: $"the CPU-window read at 0x{(0xBFF0 + index):X4} (bank 0x0F) returned 0x{observed:X2}, expected 0x{Region[index]:X2}");
            }
        }

        // Bank independence: re-page to bank 0 (as a running game would). ReadExternalRam STILL returns the highest bank's
        // bytes — it never touched the bank select — while the CPU window now reads bank 0 (which we never wrote), proving
        // the two are different addresses and the host poll is immune to the game's paging.
        cartridge.WriteControl(address: 0x4000, value: 0x00);

        var afterRepage = new byte[RegionByteCount];

        cartridge.ReadExternalRam(offset: topOffset, destination: afterRepage);

        if (!afterRepage.AsSpan().SequenceEqual(other: Region)) {
            return PostStageOutcome.Fail(detail: "ReadExternalRam changed after the game re-paged the RAM bank — the host poll is not bank-independent");
        }

        if (cartridge.ReadRam(address: 0xBFF0) == Region[0]) {
            return PostStageOutcome.Fail(detail: "the CPU window at 0xBFF0 still read the region after re-paging to bank 0 — the region is not bank-specific, so it is not the HIGHEST bank's byte");
        }

        // The region round-trips through a battery save into a fresh machine (deterministic resume).
        using var reboot = PostMachine.Build(model: ConsoleModel.Cgb, rom: rom);

        var rebootCartridge = reboot.GetRequiredService<ICartridge>();

        rebootCartridge.ImportExternalRam(source: exported);

        var afterReboot = new byte[RegionByteCount];

        rebootCartridge.ReadExternalRam(offset: topOffset, destination: afterReboot);

        if (!afterReboot.AsSpan().SequenceEqual(other: Region)) {
            return PostStageOutcome.Fail(detail: "the win-condition region did not survive a battery-save round-trip into a fresh machine");
        }

        return PostStageOutcome.Pass(detail: $"MBC5 128 KiB: the 16-byte region at physical 0x{topOffset:X5} (bank 0x0F / CPU 0xBFF0) reads identically through ReadExternalRam and the ExportExternalRam tail, is bank-independent across a re-page, and round-trips through a battery save");
    }
}
