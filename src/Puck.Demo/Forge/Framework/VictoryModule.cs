namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The 128-bit META VICTORY seam every framework game shares (Stage 2 of the self-editing arcade arc): on a game's win
/// edge it copies this cabinet's authored 16-byte "share" — seeded by the host into the framework's
/// <see cref="FrameworkMemoryMap.VictoryShareSource"/> work-RAM slot at boot — verbatim into the TOP 16 bytes of the
/// cartridge's external (battery) RAM, the region the host's meta gate reads and XORs across cabinets
/// (<c>VictoryGate</c> / <c>MetaVictoryWatch</c>). Whole-region-on-win, not progressive: the game writes all 16 share
/// bytes at once when it completes, which is all a binary "complete X games" gate needs.
///
/// <para>The write follows SaveModule's exact anti-corruption discipline — SRAM is enabled (<c>0x0A → 0x0000</c>) only
/// for the duration of the copy and disabled again on the way out — and the region lives ABOVE the save block
/// (<see cref="SaveModule.SramBase"/> at 0xA000, ≤ 77 bytes) at the very top of the 8&#160;KiB MBC1 window, so it never
/// collides with the persisted high-score save. Boot CLEARS this region (see <see cref="EmitBootReset"/>) so a stale
/// <c>.sav</c> can never auto-fire the gate on reboot; the share is re-earned every session.</para>
/// </summary>
internal sealed class VictoryModule {
    /// <summary>The first byte of the win region — the top <see cref="FrameworkMemoryMap.VictoryShareByteCount"/> bytes
    /// of the framework cartridge's 8&#160;KiB SRAM window (0xA000..0xBFFF), i.e. 0xBFF0..0xBFFF. The host reads exactly
    /// this region (the highest SRAM address, bank-independent) as the cabinet's 128-bit meta contribution.</summary>
    public const ushort RegionBase = 0xBFF0;

    private const ushort RamEnableAddress = 0x0000;
    private const byte RamEnableValue = 0x0A;

    private readonly Sm83Emitter m_emitter;
    private readonly int m_storeLabel;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public VictoryModule(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        m_emitter = emitter;
        m_storeLabel = emitter.NewLabel();
    }

    /// <summary>Emits a call to the store subroutine at the game's win edge: seed slot → SRAM win region, with the
    /// SRAM-enable/disable discipline around it. A game invokes this from its <c>StateGameOver</c> enter (the qualifying
    /// completion moment).</summary>
    public void EmitStoreShare() => m_emitter.Call(label: m_storeLabel);

    /// <summary>Emits the module's library subroutine. Called once by the framework facade, after the other module
    /// libraries.</summary>
    public void EmitLibrary() {
        m_emitter.MarkLabel(label: m_storeLabel);
        EmitRamEnable();
        FrameworkKernel.EmitBlockCopy(
            emitter: m_emitter,
            sourceAddress: FrameworkMemoryMap.VictoryShareSource,
            destinationAddress: RegionBase,
            byteCount: FrameworkMemoryMap.VictoryShareByteCount
        );
        EmitRamDisable();
        m_emitter.Return();
    }

    /// <summary>Emits the boot-time win-region RESET (called by the framework boot AFTER the save load): SRAM-enable,
    /// zero the top-16 region, SRAM-disable — so a persisted <c>.sav</c> whose top-16 bytes still hold a previous
    /// session's share can never auto-satisfy the meta gate on the next boot. The share is re-earned each session.</summary>
    /// <param name="emitter">The routine emitter.</param>
    public static void EmitBootReset(Sm83Emitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        EmitRamEnableInto(emitter: emitter);
        FrameworkKernel.EmitBlockFill(emitter: emitter, destinationAddress: RegionBase, byteCount: FrameworkMemoryMap.VictoryShareByteCount, value: 0x00);
        EmitRamDisableInto(emitter: emitter);
    }

    private void EmitRamEnable() => EmitRamEnableInto(emitter: m_emitter);

    private void EmitRamDisable() => EmitRamDisableInto(emitter: m_emitter);

    private static void EmitRamEnableInto(Sm83Emitter emitter) {
        emitter.LoadAImmediate(value: RamEnableValue);
        emitter.StoreAToAddress(address: RamEnableAddress);
    }

    private static void EmitRamDisableInto(Sm83Emitter emitter) {
        emitter.XorA();
        emitter.StoreAToAddress(address: RamEnableAddress);
    }
}
