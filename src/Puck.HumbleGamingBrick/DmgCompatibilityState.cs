namespace Puck.HumbleGamingBrick;

/// <summary>
/// The single authority for whether Color silicon is currently running its DMG-compatibility mode — the hardware fact
/// KEY0 (<c>0xFF4C</c>) carries: a real Color boot ROM writes it once, right before it locks and hands off, for a
/// cartridge whose header does not declare Color support. Every Color-only register a compatibility-mode cartridge
/// cannot reach (KEY1, RP, the palette DATA ports, SVBK, VBK, the HDMA registers, OPRI) asks this ONE question instead
/// of re-deriving <c>(model.SupportsColor() &amp;&amp; !header.SupportsColor)</c> itself, so the fact is computed in
/// exactly one place.
/// </summary>
/// <remarks>Seeded at construction from the boot model and the immutable cartridge header — exactly the answer a real
/// boot ROM's own header inspection produces — and re-derived on a live model swap until a boot ROM actually writes
/// KEY0. From that write on the mode is a hardware latch rather than a derivation, so it is snapshotted and
/// <see cref="ApplyModel"/> no longer re-derives it: a swap onto hardware without Color silicon still drops the mode,
/// which has no meaning there, but a swap between Color revisions leaves the latch alone, since a swap is not a
/// boot.</remarks>
public sealed class DmgCompatibilityState : IModeSwitchable, ISnapshotable {
    /// <summary>The KEY0 bit the Color boot ROM sets to hand off in DMG-compatibility mode.</summary>
    public const byte Key0CompatibilityBit = 0x04;

    private readonly CartridgeHeader m_header;

    private bool m_isActive;
    // Whether a boot ROM has executed its KEY0 write. Until it has, the mode is a pure function of the model and the
    // header and a live model swap re-derives it; afterwards the latch is the machine's own state.
    private bool m_key0Latched;

    /// <summary>Creates the authority seeded from the boot model and the cartridge header.</summary>
    /// <param name="configuration">The machine configuration, whose <see cref="MachineConfiguration.Model"/> seeds the mode.</param>
    /// <param name="header">The cartridge header, whose <see cref="CartridgeHeader.SupportsColor"/> is the other half of the fact.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public DmgCompatibilityState(MachineConfiguration configuration, CartridgeHeader header) {
        ArgumentNullException.ThrowIfNull(argument: configuration);
        ArgumentNullException.ThrowIfNull(argument: header);

        m_header = header;
        m_isActive = Derive(
            header: header,
            model: configuration.Model
        );
    }

    /// <summary>Gets whether Color silicon is currently running its DMG-compatibility mode: the Color-only registers
    /// (KEY1, RP, the palette data ports, SVBK/VBK, HDMA, OPRI) read sealed rather than live. Always
    /// <see langword="false"/> on hardware without Color silicon.</summary>
    public bool IsActive =>
        m_isActive;

    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) =>
        m_isActive = (m_key0Latched
            ? (m_isActive && model.SupportsColor())
            : Derive(
                header: m_header,
                model: model
            ));
    /// <summary>Applies a real boot ROM's write to KEY0 (<c>0xFF4C</c>) — the hardware event that hands the mode to
    /// the cartridge, and the only writer of the mode once it has run. Bit 2 is the documented DMG-compatibility flag;
    /// the undocumented PGB bits are not modeled.</summary>
    /// <param name="value">The byte written to FF4C.</param>
    public void ApplyKey0(byte value) {
        m_isActive = ((value & Key0CompatibilityBit) != 0);
        m_key0Latched = true;
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBoolean(value: m_isActive);
        writer.WriteBoolean(value: m_key0Latched);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_isActive = reader.ReadBoolean();
        m_key0Latched = reader.ReadBoolean();
    }

    private static bool Derive(ConsoleModel model, CartridgeHeader header) =>
        (model.SupportsColor() && !header.SupportsColor);
}
